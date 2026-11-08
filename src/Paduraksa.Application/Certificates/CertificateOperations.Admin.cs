using Microsoft.Extensions.Logging;

using Paduraksa.Application.Commands;
using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;

namespace Paduraksa.Application.Certificates;

/// <summary>
/// The process-managed admin certificate (PLAN §3, §10.1): the local-CA leaf the admin listener serves, registered in the
/// inventory under <see cref="AdminCertificate.Id"/>. It reuses the ordering of the other creation flows (pending row, the
/// vault, then the active row), so a failure never leaves a hidden key, and it never replaces a certificate the vault
/// already holds for itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here republishes the live config.</b> The admin certificate is the process's own: no host can bind it (the
/// validator refuses such a binding and the inventory loader leaves it out of the compiler's inventory), so a change to it
/// can't change what the proxy serves. Registering it quietly (<see cref="CertificateService.RegisterQuietlyAsync"/>)
/// keeps the startup step from waiting for the public app's YARP — the core host can finish it before any listener binds,
/// so the admin port serves the local-CA certificate from its first request. The next compile reads the inventory fresh.
/// </para>
/// <para>
/// <b>A lost vault is repaired, not waited out.</b> When the inventory has no row for the admin certificate (or the root)
/// but the vault holds one, it is adopted from <see cref="ICertificateCatalog"/> — never re-issued — so the served
/// fingerprint survives and the recovery and this service cannot race each other into a second certificate. When it is the
/// other way round (the row is there, the vault's key is gone: vault.db was deleted or restored from an older copy), the
/// stale root row is retired and the leaf is issued again, so the admin port comes back with no operator action at all —
/// which matters because every manual way out (deleting the root, deleting the admin certificate, re-issuing it by hand) is
/// refused by design.
/// </para>
/// </remarks>
public sealed partial class CertificateOperations
{
    /// <summary>
    /// Ensures the local CA root exists and the admin certificate is current: issues the leaf under
    /// <see cref="AdminCertificate.Id"/> when the vault has none (or a crash left the row not active), and otherwise leaves
    /// it alone unless its names changed or it is near expiry (<see cref="AdminCertificatePolicy.ShouldReissue"/>), in which
    /// case it is re-issued under the same ID. The root is created first (quietly) when there is none; there is never a
    /// second one.
    /// </summary>
    /// <remarks>
    /// A re-issue keeps the ID, so nothing that refers to the certificate by ID breaks; nothing in the config can refer to it
    /// anyway (it is reserved for the admin listener, see <see cref="AdminCertificate"/>), so no Specific binding is involved.
    /// The caller re-loads the serving context afterwards and the listener swaps to it. An
    /// <see cref="CertificateOperationStatus.Unchanged"/> result means the certificate as stored is what was asked for. The
    /// result describes the leaf; a failure to prepare the root is reported with the root's status.
    /// </remarks>
    public async Task<CertificateOperationResult> EnsureAdminCertificateAsync(
        IReadOnlyList<string> names, CommandOrigin origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(origin);

        CertificateOperationResult root = await EnsureAdminRootAsync(origin, cancellationToken).ConfigureAwait(false);
        if (root.Status is not (CertificateOperationStatus.Completed or CertificateOperationStatus.Unchanged) || root.Certificate is null)
        {
            return root;
        }

        if (!await IsAllowedAsync(CertificateOperation.IssueFromLocalCa, origin, cancellationToken).ConfigureAwait(false))
        {
            return Forbidden();
        }

        // Composed by AdminCertificateNames, so there is always at least localhost, 127.0.0.1 and ::1; normalising again
        // keeps this robust for a caller that passes its own list.
        List<string> wanted = CertificateNames.Normalize(names, out _);
        if (wanted.Count == 0)
        {
            throw new ArgumentException("The admin certificate needs at least one usable name.", nameof(names));
        }

        CertificateDetail? current = await _certificates.GetAsync(AdminCertificate.Id, cancellationToken).ConfigureAwait(false);
        VaultCertificate? held = await HeldAdminLeafAsync(cancellationToken).ConfigureAwait(false);
        if (current is null && held is not null && await AdoptVaultAdminLeafAsync(held, wanted, origin, cancellationToken).ConfigureAwait(false) is { } adopted)
        {
            return adopted;
        }

        // No row, a row a crash left not active, or a row whose key the vault lost (vault.db deleted or restored from an older
        // copy): the leaf has to be made. There is nothing to re-issue in that last case, and the row is reused.
        if (held is null || current is null || current.Summary.Status != CertificateStatus.Active)
        {
            CertificateOperationResult created = await CreateWithNamesAsync(
                CertificateSource.LocalCa,
                wanted,
                AdminCertificate.Label,
                origin,
                (id, covered, ct) => _issuance.IssueFromLocalCaAsync(id, KeySpec.EcdsaP256, covered, DefaultLeafValidity, ct),
                cancellationToken,
                existingPendingId: current is null ? null : AdminCertificate.Id,
                newId: AdminCertificate.Id,
                republish: false).ConfigureAwait(false);

            // A row can appear between the read above and the pending insert (the recovery, before it learned to skip the
            // admin certificate): use what it wrote instead of reporting the collision.
            if (created.Status == CertificateOperationStatus.Invalid && created.Errors.Any(static error => error.Code == CertificateErrorCodes.Exists))
            {
                CertificateDetail? raced = await _certificates.GetAsync(AdminCertificate.Id, cancellationToken).ConfigureAwait(false);
                return raced is null ? created : new CertificateOperationResult(CertificateOperationStatus.Unchanged, raced.Summary);
            }

            return created;
        }

        if (!AdminCertificatePolicy.ShouldReissue(current.Summary, wanted, _timeProvider.GetUtcNow()))
        {
            return await RepairLabelAsync(current.Summary, origin, cancellationToken).ConfigureAwait(false);
        }

        CertificateInfo info;
        try
        {
            info = await _issuance.IssueFromLocalCaAsync(AdminCertificate.Id, KeySpec.EcdsaP256, wanted, DefaultLeafValidity, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Invalid(new ConfigError(string.Empty, CertificateErrorCodes.IssuanceRefused, ex.Message));
        }

        return await RegisterVaultResultAsync(AdminCertificate.Id, CertificateSource.LocalCa, info, label: null, isNew: false, pendingFirst: false, origin, cancellationToken, republish: false).ConfigureAwait(false);
    }

    /// <summary>The admin leaf the vault holds (public facts, no key opened), or null when it holds none for that ID.</summary>
    private async Task<VaultCertificate?> HeldAdminLeafAsync(CancellationToken cancellationToken)
    {
        VaultCertificate? held = await _catalog.DescribeAsync(AdminCertificate.Id, cancellationToken).ConfigureAwait(false);
        return held is { IsLocalCaRoot: false } ? held : null;
    }

    /// <summary>
    /// The local CA root for the admin flow, quietly (nothing can bind it either): the inventory's active one while the vault
    /// still holds its key, or the one the vault holds that the inventory lost — adopted under
    /// <see cref="CertificateRecovery.Label"/> so it does not matter which of the two writers registers it first — or a newly
    /// created one when the vault has none.
    /// </summary>
    /// <remarks>
    /// <b>A root row whose key the vault lost</b> (vault.db deleted or restored from an older copy, the config database intact)
    /// is retired as <see cref="CertificateStatus.Failed"/> and replaced, so the admin certificate comes back without an
    /// operator: the row alone would otherwise look healthy forever, every re-issue would fail for want of a root, and every
    /// way out of it (deleting the root, deleting the admin certificate, re-issuing it by hand) is refused.
    /// </remarks>
    // TODO(M5): nothing renews the local CA root. The vault caps a leaf at the root's remaining validity, so in the root's
    // last 90 days every re-issued admin leaf gets shorter until the root expires (then the placeholder serves and the
    // download still offers an expired root). Rotating the root needs a plan for the leaves issued under the old one; it is
    // listed as open in PLAN §9.1.
    private async Task<CertificateOperationResult> EnsureAdminRootAsync(CommandOrigin origin, CancellationToken cancellationToken)
    {
        IReadOnlyList<CertificateSummary> roots = await _certificates.ListAsync(new CertificateQuery(Source: CertificateSource.LocalCaRoot), cancellationToken).ConfigureAwait(false);
        if (roots.FirstOrDefault(static root => root.Status == CertificateStatus.Active) is { } active)
        {
            if (await _catalog.DescribeAsync(active.Id, cancellationToken).ConfigureAwait(false) is { IsLocalCaRoot: true })
            {
                return new CertificateOperationResult(CertificateOperationStatus.Unchanged, active);
            }

            CertificateChangeResult retired = await _certificates.SetStatusAsync(
                active.Id, CertificateStatus.Failed, "its key is missing from the vault, so a new local CA root replaces it", origin, cancellationToken).ConfigureAwait(false);
            if (retired.Status is not (CertificateChangeStatus.Committed or CertificateChangeStatus.Unchanged))
            {
                return FromChange(retired);
            }

            LogRootKeyMissing(active.Id);
        }

        VaultCertificate? held = (await _catalog.ListAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(static certificate => certificate.IsLocalCaRoot);
        if (held is not null)
        {
            return await AdoptRootAsync(held.Id, held.Info, origin, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            // A root row that is not active is what a crash left behind: it is completed under its own ID (the vault's ensure
            // is idempotent for it). Only an inventory with no root row at all reaches the vault's create.
            return await CreateWithNamesAsync(
                CertificateSource.LocalCaRoot,
                [],
                null,
                origin,
                (id, _, ct) => _issuance.EnsureLocalCaAsync(id, KeySpec.EcdsaP256, DefaultRootValidity, ct),
                cancellationToken,
                existingPendingId: roots.Count > 0 ? roots[0].Id : null,
                republish: false).ConfigureAwait(false);
        }
        catch (LocalCaExistsException exists)
        {
            return await AdoptRootAsync(exists.ExistingId, exists.Info, origin, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<CertificateOperationResult> AdoptRootAsync(CertificateId id, CertificateInfo info, CommandOrigin origin, CancellationToken cancellationToken)
    {
        CertificateChangeResult registered = await _certificates.RegisterQuietlyAsync(
            new CertificateRegistration(id, CertificateSource.LocalCaRoot, info, CertificateStatus.Active, CertificateRecovery.Label), origin, cancellationToken).ConfigureAwait(false);
        return registered.Status switch
        {
            CertificateChangeStatus.Committed or CertificateChangeStatus.Unchanged => new CertificateOperationResult(CertificateOperationStatus.Completed, registered.Certificate, Publish: registered.Publish),
            _ => FromChange(registered),
        };
    }

    /// <summary>
    /// Adopts the admin leaf the vault holds when the inventory has no row for it (a database loss), so no new key is made
    /// and the served fingerprint survives. Null when the vault has none, it is not servable, or it does not carry exactly
    /// the wanted names (then the leaf is issued again): an adopted leaf must satisfy
    /// <see cref="AdminCertificatePolicy.ShouldReissue"/>, or the next run would re-issue it anyway and the adoption would
    /// have bought nothing.
    /// </summary>
    private async Task<CertificateOperationResult?> AdoptVaultAdminLeafAsync(VaultCertificate held, IReadOnlyList<string> wanted, CommandOrigin origin, CancellationToken cancellationToken)
    {
        if (!AdminCertificatePolicy.IsServable(held.Info, _timeProvider.GetUtcNow()))
        {
            return null;
        }

        List<string> covered = CertificateNames.Normalize(held.Info.SubjectAlternativeNames, out _);
        if (!AdminCertificatePolicy.SameNames(covered, wanted))
        {
            return null;
        }

        CertificateChangeResult registered = await _certificates.RegisterQuietlyAsync(
            new CertificateRegistration(AdminCertificate.Id, CertificateSource.LocalCa, held.Info, CertificateStatus.Active, AdminCertificate.Label), origin, cancellationToken).ConfigureAwait(false);
        return registered.Status switch
        {
            CertificateChangeStatus.Committed or CertificateChangeStatus.Unchanged => new CertificateOperationResult(CertificateOperationStatus.Completed, registered.Certificate, Publish: registered.Publish),
            _ => FromChange(registered),
        };
    }

    /// <summary>Puts the managed label back when the row lost it (a recovered one); notes are kept and the result carries the stored row.</summary>
    private async Task<CertificateOperationResult> RepairLabelAsync(CertificateSummary current, CommandOrigin origin, CancellationToken cancellationToken)
    {
        if (current.Label == AdminCertificate.Label)
        {
            return new CertificateOperationResult(CertificateOperationStatus.Unchanged, current);
        }

        CertificateChangeResult repaired = await _certificates.UpdateDetailsAsync(AdminCertificate.Id, AdminCertificate.Label, current.Notes, origin, cancellationToken).ConfigureAwait(false);
        return new CertificateOperationResult(CertificateOperationStatus.Unchanged, repaired.Certificate ?? current);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The local CA root {Root} has no key in the vault (vault.db was deleted or restored), so it is retired as failed and a new root replaces it; the admin certificate is issued from the new one.")]
    private partial void LogRootKeyMissing(CertificateId root);
}
