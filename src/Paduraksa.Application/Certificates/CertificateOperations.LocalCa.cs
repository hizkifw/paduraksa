using Microsoft.Extensions.Logging;

using Paduraksa.Application.Commands;
using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;

namespace Paduraksa.Application.Certificates;

/// <summary>
/// The local CA's life after its creation (docs/design/vault-and-certificates.md "Local CA name constraints and root rotation"): what the root is limited to,
/// its rotation, and the roots a rotation leaves behind.
/// </summary>
public sealed partial class CertificateOperations
{
    /// <summary>Raised after a rotation re-issued the admin listener's certificate, so the listener serves the new one now rather than at its next daily check.</summary>
    public event Action? AdminCertificateReissued;

    /// <summary>
    /// The names the admin listener's certificate is wanted for right now (the process's own composition from its settings and the machine's name), set by
    /// the service that serves it. Null in a host without one: the names of the certificate as stored are used then.
    /// </summary>
    public Func<IReadOnlyList<string>>? DesiredAdminNames { get; set; }

    /// <summary>
    /// The local CA as it stands: the root that signs and what it is limited to, and the roots a rotation replaced with how many
    /// certificates each still has signed.
    /// </summary>
    public async Task<LocalCaStatus> GetLocalCaAsync(CancellationToken cancellationToken = default)
    {
        var roots = await _certificates.ListAsync(new CertificateQuery(Source: CertificateSource.LocalCaRoot, Status: CertificateStatus.Active), cancellationToken).ConfigureAwait(false);
        var leaves = await _certificates.ListAsync(new CertificateQuery(Source: CertificateSource.LocalCa), cancellationToken).ConfigureAwait(false);
        var (suggestedDomains, suggestedAddresses) = await SuggestionsAsync(leaves, cancellationToken).ConfigureAwait(false);
        var active = NewestActive(roots);
        if (active is null)
        {
            return new LocalCaStatus(null, [], [], suggestedDomains, suggestedAddresses);
        }

        var held = await _catalog.DescribeAsync(active.Id, cancellationToken).ConfigureAwait(false);
        List<RetiringLocalCaRoot> retiring = [];
        foreach (var root in roots.Where(r => r.Id != active.Id).OrderByDescending(r => r.CreatedAt))
        {
            retiring.Add(new RetiringLocalCaRoot(root, leaves.Count(leaf => string.Equals(leaf.Issuer, root.Subject, StringComparison.Ordinal))));
        }

        return new LocalCaStatus(active, held?.Info.NameConstraints ?? [], retiring, suggestedDomains, suggestedAddresses);
    }

    /// <summary>
    /// What an operator who limits a root is offered to start from (a root is unlimited unless they choose to): the private-use domains and
    /// private ranges, the names the admin certificate is wanted for, and the names of the certificates the local CA has issued, which is what the hosts
    /// that use it are served with.
    /// </summary>
    private async Task<(IReadOnlyList<string> Domains, IReadOnlyList<string> Addresses)> SuggestionsAsync(IReadOnlyList<CertificateSummary> leaves, CancellationToken cancellationToken)
    {
        var (adminDomains, adminAddresses) = SplitNames(await DesiredAdminNamesAsync(cancellationToken).ConfigureAwait(false));
        var (leafDomains, leafAddresses) = SplitNames([.. leaves.Where(static l => l.Id != AdminCertificate.Id).SelectMany(static l => l.Names)]);
        return (
            [.. LocalCaConstraints.DefaultDomains.Concat(adminDomains).Concat(leafDomains.Select(static d => d.StartsWith("*.", StringComparison.Ordinal) ? d[2..] : d)).Distinct(StringComparer.OrdinalIgnoreCase)],
            [.. LocalCaConstraints.PrivateRanges.Concat(adminAddresses).Concat(leafAddresses).Distinct(StringComparer.OrdinalIgnoreCase)]);
    }

    /// <summary>The names the admin certificate is wanted for: what the process composes now, else what the stored certificate carries.</summary>
    private async Task<IReadOnlyList<string>> DesiredAdminNamesAsync(CancellationToken cancellationToken) =>
        DesiredAdminNames?.Invoke() ?? (await _certificates.GetAsync(AdminCertificate.Id, cancellationToken).ConfigureAwait(false))?.Summary.Names ?? [];

    /// <summary>
    /// Replaces the local CA root. The new root is made first and the old one's key is destroyed with it, in one vault transaction; the
    /// admin listener's certificate is issued again from the new root at once; every other certificate the old root signed keeps serving
    /// until <see cref="ReissueLocalCaLeavesAsync"/> (or the one-click re-issue) replaces it. The old root stays in the inventory, still
    /// downloadable, until it is retired with <see cref="DeleteAsync"/> once nothing it signed is left.
    /// </summary>
    public async Task<CertificateOperationResult> RotateLocalCaAsync(LocalCaRotateRequest request, CommandOrigin origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(origin);
        if (!await IsAllowedAsync(CertificateOperation.RotateLocalCa, origin, cancellationToken).ConfigureAwait(false))
        {
            return Forbidden();
        }

        // A limited root always permits what the admin listener's certificate is wanted for *now* (not what it was last issued for: the machine may
        // have been renamed since), so a rotation can never leave the admin port without a certificate it may be issued.
        var adminNames = await DesiredAdminNamesAsync(cancellationToken).ConfigureAwait(false);
        if (ConstraintsOf(request.PermittedDomains, request.PermittedAddresses, adminNames, out var constraints) is { } invalid)
        {
            return Invalid(invalid);
        }

        var roots = await _certificates.ListAsync(new CertificateQuery(Source: CertificateSource.LocalCaRoot, Status: CertificateStatus.Active), cancellationToken).ConfigureAwait(false);
        if (NewestActive(roots) is not { } current)
        {
            return Invalid(new ConfigError(string.Empty, CertificateErrorCodes.LocalCaMissing, "there is no local CA to rotate; create it first"));
        }

        var newId = CertificateId.New();
        LocalCaRotation rotation;
        try
        {
            rotation = await _issuance.RotateLocalCaAsync(newId, request.KeySpec, request.Validity ?? DefaultRootValidity, constraints, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            LogIssuanceRefused(newId, ex);
            return Invalid(new ConfigError(string.Empty, CertificateErrorCodes.IssuanceRefused, IssuanceRefusedMessage));
        }

        // The vault has swapped roots, and that can't be undone: recording the new one is tried twice, so a passing failure doesn't leave the dashboard on the
        // old root until a restart. If both fail it is reported and the startup recovery registers what the vault holds; nothing here deletes it.
        CertificateRegistration registration = new(newId, CertificateSource.LocalCaRoot, rotation.Root, CertificateStatus.Active, CertificateRecovery.Label);
        var registered = await _certificates.RegisterAsync(registration, origin, cancellationToken).ConfigureAwait(false);
        if (registered.Status is not (CertificateChangeStatus.Committed or CertificateChangeStatus.Unchanged))
        {
            registered = await _certificates.RegisterAsync(registration, origin, cancellationToken).ConfigureAwait(false);
        }

        if (registered.Status is not (CertificateChangeStatus.Committed or CertificateChangeStatus.Unchanged))
        {
            LogRotationNotRecorded(newId, current.Id);
            var failed = FromChange(registered);
            return failed with
            {
                Warnings = [.. failed.Warnings, new ConfigError(string.Empty, CertificateErrorCodes.RotationNotRecorded, "the root was replaced in the vault but couldn't be recorded here, even after trying again; the old root is still shown until the new one is registered at the next restart")],
            };
        }

        await _certificates.RecordLocalCaRotationAsync(newId, rotation.RetiredId, string.Join(", ", rotation.Root.NameConstraints ?? []), origin, cancellationToken).ConfigureAwait(false);
        LogRotated(newId, rotation.RetiredId);
        List<ConfigError> warnings = [];
        if (adminNames.Count > 0)
        {
            var reissued = await EnsureAdminCertificateAsync(adminNames, origin, cancellationToken).ConfigureAwait(false);
            if (reissued.Status is CertificateOperationStatus.Completed)
            {
                AdminCertificateReissued?.Invoke();
            }
            else if (reissued.Status is not CertificateOperationStatus.Unchanged)
            {
                warnings.Add(new ConfigError(string.Empty, CertificateErrorCodes.AdminCoverage, "the new root couldn't issue the admin listener's certificate (a name outside its constraints, most likely); the admin port keeps the old one until it can"));
            }
        }

        return new CertificateOperationResult(CertificateOperationStatus.Completed, registered.Certificate, Publish: registered.Publish, Warnings: warnings);
    }

    /// <summary>
    /// Issues again, under the same IDs and from the current root, every certificate a retiring root signed (the admin listener's own was
    /// already). Specific bindings survive, as with any re-issue; a certificate whose names the new root's constraints don't permit is left
    /// as it is and listed in the result.
    /// </summary>
    public async Task<LocalCaReissueResult> ReissueLocalCaLeavesAsync(CommandOrigin origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!await IsAllowedAsync(CertificateOperation.RotateLocalCa, origin, cancellationToken).ConfigureAwait(false))
        {
            return new LocalCaReissueResult(0, [new ConfigError(string.Empty, "forbidden", "you may not re-issue certificates")]);
        }

        var status = await GetLocalCaAsync(cancellationToken).ConfigureAwait(false);
        var retiringSubjects = status.Retiring.Select(r => r.Root.Subject).ToHashSet(StringComparer.Ordinal);
        var leaves = await _certificates.ListAsync(new CertificateQuery(Source: CertificateSource.LocalCa), cancellationToken).ConfigureAwait(false);
        var count = 0;
        List<ConfigError> failures = [];
        foreach (var leaf in leaves.Where(l => l.Id != AdminCertificate.Id && retiringSubjects.Contains(l.Issuer)))
        {
            var result = await ReissueAsync(leaf.Id, null, origin, cancellationToken).ConfigureAwait(false);
            if (result.Status is CertificateOperationStatus.Completed or CertificateOperationStatus.Unchanged)
            {
                count++;
            }
            else
            {
                failures.AddRange(result.Errors.Select(e => e with { Path = leaf.Id.ToString() }));
            }
        }

        return new LocalCaReissueResult(count, failures);
    }

    private static CertificateSummary? NewestActive(IReadOnlyList<CertificateSummary> roots) =>
        roots.Where(static r => r.Status == CertificateStatus.Active).OrderByDescending(static r => r.CreatedAt).ThenByDescending(static r => r.NotBefore).FirstOrDefault();

    /// <summary>
    /// What a root is limited to: null (unlimited) when the operator chose no names, else the names they chose and what the admin certificate needs; or
    /// the error naming what isn't a domain or a network.
    /// </summary>
    private static ConfigError? ConstraintsOf(IReadOnlyList<string>? domains, IReadOnlyList<string>? addresses, IReadOnlyList<string> adminNames, out LocalCaConstraints? constraints)
    {
        constraints = null;
        if ((domains ?? []).Count == 0 && (addresses ?? []).Count == 0)
        {
            return null;
        }

        var (adminDomains, adminAddresses) = SplitNames(adminNames);
        var limited = new LocalCaConstraints(
            [.. (domains ?? []).Concat(adminDomains).Distinct(StringComparer.OrdinalIgnoreCase)],
            [.. (addresses ?? []).Concat(adminAddresses).Distinct(StringComparer.OrdinalIgnoreCase)]);
        if (LocalCaConstraintsCheck.Problem(limited) is { } problem)
        {
            return new ConfigError("/permittedDomains", CertificateErrorCodes.LocalCaConstraintsInvalid, problem);
        }

        constraints = limited;
        return null;
    }

    private static (string[] Domains, string[] Addresses) SplitNames(IReadOnlyList<string> names) =>
        ([.. names.Where(static n => !System.Net.IPAddress.TryParse(n, out _))], [.. names.Where(static n => System.Net.IPAddress.TryParse(n, out _))]);

    [LoggerMessage(Level = LogLevel.Information, Message = "The local CA root {Root} replaced {Retired}.")]
    private partial void LogRotated(CertificateId root, CertificateId retired);

    [LoggerMessage(Level = LogLevel.Error, Message = "The vault rotated the local CA root to {Root} (replacing {Retired}) but the inventory couldn't record it; the next recovery registers it.")]
    private partial void LogRotationNotRecorded(CertificateId root, CertificateId retired);
}

/// <summary>What the vault will accept as a local CA's constraints, checked first so the answer names the entry (the vault checks again).</summary>
internal static class LocalCaConstraintsCheck
{
    public static string? Problem(LocalCaConstraints constraints)
    {
        if (constraints.Domains.Count > LocalCaConstraints.MaxEntries || constraints.Addresses.Count > LocalCaConstraints.MaxEntries)
        {
            return $"at most {LocalCaConstraints.MaxEntries} domains and {LocalCaConstraints.MaxEntries} networks";
        }

        foreach (var domain in constraints.Domains)
        {
            var name = (domain ?? string.Empty).Trim().TrimStart('.');
            if (name.StartsWith("*.", StringComparison.Ordinal))
            {
                name = name[2..];
            }

            if (name.Length is 0 or > 253 || name.Contains('*', StringComparison.Ordinal) || System.Net.IPAddress.TryParse(name, out _) || Uri.CheckHostName(name) != UriHostNameType.Dns)
            {
                return $"'{domain}' is not a DNS domain";
            }
        }

        foreach (var address in constraints.Addresses)
        {
            var text = (address ?? string.Empty).Trim();
            if (!System.Net.IPAddress.TryParse(text, out _) && !System.Net.IPNetwork.TryParse(text, out _))
            {
                return $"'{address}' is not an IP address or a CIDR network";
            }
        }

        return null;
    }
}
