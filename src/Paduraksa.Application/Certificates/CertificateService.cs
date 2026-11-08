using System.Globalization;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Paduraksa.Application.Audit;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Config;
using Paduraksa.Application.Identity;
using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Data;
using Paduraksa.Data.Certificates;

namespace Paduraksa.Application.Certificates;

/// <summary>
/// The certificate inventory's write and read side (PLAN §5.1, §9.2): metadata about the certificates the vault holds.
/// The next wave's ACME client, upload and local-CA flows call it <b>after</b> the vault created, replaced or imported a
/// key and chain (they pass the <see cref="CertificateInfo"/> the vault returned); it never calls the vault.
/// </summary>
/// <remarks>
/// <para><b>Certificates are not config.</b> They live in the config database next to the revisions but outside the
/// config document: a change here creates no revision. Each change is audited in the transaction that makes it
/// (<c>certificate.add</c>, <c>certificate.update</c>, <c>certificate.delete</c>; refusals are audited too), and when it
/// affects what is served, the <b>latest revision is compiled again with the new inventory and published</b>, through
/// the same single writer as config commands (<see cref="ConfigService.ApplyCertificateChangeAsync"/>), so publishes
/// stay ordered. <see cref="CertificateChangeResult.Publish"/> reports that publish.</para>
/// <para><b>Deleting</b> is refused (<see cref="CertificateChangeStatus.Invalid"/>) while the latest revision binds a host
/// to the certificate with a Specific binding, or names it as the default certificate; the errors carry the paths of
/// those hosts. A caller that also removes the vault's key must do so only after this reports
/// <see cref="CertificateChangeStatus.Committed"/>. Older revisions may still name the certificate: a rollback to one
/// that does is refused until the certificate exists again, like any binding to an unusable certificate.</para>
/// <para><b>Replacing</b> a certificate (renewal, re-upload, re-issue) is <see cref="RegisterAsync"/> with the same ID:
/// the metadata is swapped and, because the ID is stable, Specific bindings survive (PLAN §9.2).</para>
/// <para><b>Public data only.</b> The row holds the public chain and facts; there is no key material in this layer, and
/// nothing here logs a chain, only IDs, names and fingerprints.</para>
/// </remarks>
public sealed partial class CertificateService
{
    /// <summary>The longest label.</summary>
    public const int MaxLabelLength = 256;

    /// <summary>The longest notes text.</summary>
    public const int MaxNotesLength = 4096;

    private readonly IDbContextFactory<ConfigDbContext> _contexts;
    private readonly ConfigService _config;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly IBuiltInCertificates? _builtIns;
    private readonly IPermissionEvaluator? _permissions;

    internal CertificateService(
        IDbContextFactory<ConfigDbContext> contexts,
        ConfigService config,
        TimeProvider timeProvider,
        ILogger<CertificateService> logger,
        IBuiltInCertificates? builtIns = null,
        IPermissionEvaluator? permissions = null)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _contexts = contexts;
        _config = config;
        _timeProvider = timeProvider;
        _builtIns = builtIns;
        _permissions = permissions;
        _logger = logger;
    }

    /// <summary>
    /// Records a certificate the vault holds: adds it, or replaces the metadata of the one with the same ID (a renewal).
    /// Names are normalised like host names; names that aren't DNS names, wildcards or IP addresses are dropped (logged),
    /// and a certificate left with no name is invalid, except the local CA root, which is a CA and has no names.
    /// </summary>
    public async Task<CertificateChangeResult> RegisterAsync(CertificateRegistration registration, CommandOrigin origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(origin);
        if (await DeniedAsync(origin, "certificate.add", registration.Id, cancellationToken).ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        return await _config.ApplyCertificateChangeAsync(origin, scope => RegisterCoreAsync(scope, registration, origin, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Registers a certificate the vault holds under <paramref name="registration"/> <b>only if the inventory has no row for it</b> and
    /// the audit trail does not say it was deleted on purpose, both checked under the config writer's lock, so a registration that
    /// races with this (a vault-first upload) is never overwritten and a certificate deleted by an operator is never resurrected
    /// (see <see cref="CertificateRecovery"/>). Never changes an existing row, its label included.
    /// </summary>
    internal async Task<CertificateRecoveryStep> RegisterRecoveredAsync(CertificateRegistration registration, CommandOrigin origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(origin);
        CertificateRecoveryStep step = CertificateRecoveryStep.Failed;
        string target = registration.Id.ToString();
        _ = await _config.ApplyCertificateChangeAsync(
            origin,
            async scope =>
            {
                if (await scope.Context.Certificates.AnyAsync(c => c.Id == registration.Id, cancellationToken).ConfigureAwait(false))
                {
                    step = CertificateRecoveryStep.AlreadyRegistered;
                    return new CertificateChangeOutcome(new CertificateChangeResult(CertificateChangeStatus.Unchanged), null, false);
                }

                if (await scope.Context.AuditEvents.AnyAsync(a => a.Action == "certificate.delete" && a.Outcome == AuditOutcome.Success && a.Target == target, cancellationToken).ConfigureAwait(false))
                {
                    step = CertificateRecoveryStep.WasDeleted;
                    return new CertificateChangeOutcome(new CertificateChangeResult(CertificateChangeStatus.Unchanged), null, false);
                }

                CertificateChangeOutcome outcome = await RegisterCoreAsync(scope, registration, origin, cancellationToken).ConfigureAwait(false);
                step = outcome.Result.Status is CertificateChangeStatus.Committed or CertificateChangeStatus.Unchanged ? CertificateRecoveryStep.Registered : CertificateRecoveryStep.Failed;
                return outcome;
            },
            cancellationToken).ConfigureAwait(false);
        return step;
    }

    /// <summary>
    /// Records a certificate that is requested but has no chain yet (an ACME order in progress): <see cref="CertificateStatus.Pending"/>,
    /// never served. <see cref="RegisterAsync"/> with the issued chain completes it. Registering an ID that exists is invalid.
    /// </summary>
    public async Task<CertificateChangeResult> RegisterPendingAsync(
        CertificateId id, CertificateSource source, IReadOnlyList<string> names, string? label, CommandOrigin origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(origin);
        if (await DeniedAsync(origin, "certificate.add", id, cancellationToken).ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        return await _config.ApplyCertificateChangeAsync(origin, scope => RegisterPendingCoreAsync(scope, id, source, names, label, origin, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets the state of a certificate: <see cref="CertificateStatus.Failed"/> with a reason when issuance or renewal
    /// failed and nothing usable came of it (the scheduler's next wave), or back to <see cref="CertificateStatus.Active"/>.
    /// A certificate that has no chain can't become active.
    /// </summary>
    public async Task<CertificateChangeResult> SetStatusAsync(CertificateId id, CertificateStatus status, string? message, CommandOrigin origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (await DeniedAsync(origin, "certificate.update", id, cancellationToken).ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        return await _config.ApplyCertificateChangeAsync(origin, scope => SetStatusCoreAsync(scope, id, status, message, origin, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Replaces a certificate's label and notes (null clears). Doesn't affect serving: nothing is published.</summary>
    public async Task<CertificateChangeResult> UpdateDetailsAsync(CertificateId id, string? label, string? notes, CommandOrigin origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (await DeniedAsync(origin, "certificate.update", id, cancellationToken).ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        return await _config.ApplyCertificateChangeAsync(origin, scope => UpdateDetailsCoreAsync(scope, id, label, notes, origin, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes a certificate's metadata, unless the latest revision still binds to it (see the remarks).</summary>
    public async Task<CertificateChangeResult> DeleteAsync(CertificateId id, CommandOrigin origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (await DeniedAsync(origin, "certificate.delete", id, cancellationToken).ConfigureAwait(false) is { } denied)
        {
            return denied;
        }

        return await _config.ApplyCertificateChangeAsync(origin, scope => DeleteCoreAsync(scope, id, origin, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The refusal for an origin without <see cref="Permission.CertificatesWrite"/> (audited as <see cref="AuditOutcome.Denied"/>), or
    /// null if it may go on. Always null without an <see cref="IPermissionEvaluator"/> (tests).
    /// </summary>
    private async Task<CertificateChangeResult?> DeniedAsync(CommandOrigin origin, string action, CertificateId id, CancellationToken cancellationToken) =>
        await PermissionGate.MayAsync(_permissions, _config, origin, Permission.CertificatesWrite, action, id.IsEmpty ? null : id.ToString(), cancellationToken).ConfigureAwait(false)
            ? null
            : new CertificateChangeResult(CertificateChangeStatus.Denied);

    /// <summary>The certificates matching <paramref name="query"/> (all when null), newest first.</summary>
    public async Task<IReadOnlyList<CertificateSummary>> ListAsync(CertificateQuery? query = null, CancellationToken cancellationToken = default)
    {
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            IQueryable<CertificateRow> rows = context.Certificates.AsNoTracking();
            if (query?.Source is { } source)
            {
                rows = rows.Where(c => c.Source == source);
            }

            if (query?.Status is { } status)
            {
                rows = rows.Where(c => c.Status == status);
            }

            if (query?.CoversName is { } covers)
            {
                if (CertificateNames.Normalize(covers) is not { } name)
                {
                    return [];
                }

                string[] candidates = CertificateNames.WildcardOf(name) is { } wildcard ? [name, wildcard] : [name];
                rows = rows.Where(c => context.CertificateNames.Any(n => n.CertificateId == c.Id && candidates.Contains(n.Name)));
            }

            List<CertificateRow> found = await rows.ToListAsync(cancellationToken).ConfigureAwait(false);
            ILookup<CertificateId, string> names = await NamesOfAsync(context, found.Select(c => c.Id), cancellationToken).ConfigureAwait(false);
            DateTimeOffset now = _timeProvider.GetUtcNow();
            return [.. found.OrderByDescending(c => c.CreatedAt).ThenBy(c => c.Id).Select(c => ToSummary(c, [.. names[c.Id]], now))];
        }
    }

    /// <summary>One certificate with its public chain; null when there is none.</summary>
    public async Task<CertificateDetail?> GetAsync(CertificateId id, CancellationToken cancellationToken = default)
    {
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            CertificateRow? row = await context.Certificates.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken).ConfigureAwait(false);
            if (row is null)
            {
                return null;
            }

            ILookup<CertificateId, string> names = await NamesOfAsync(context, [id], cancellationToken).ConfigureAwait(false);
            return new CertificateDetail(
                ToSummary(row, [.. names[id]], _timeProvider.GetUtcNow()),
                JsonSerializer.Deserialize<string[]>(row.ChainIssuers) ?? [],
                row.PublicChainPem);
        }
    }

    private async Task<CertificateChangeOutcome> RegisterCoreAsync(
        CertificateChangeScope scope, CertificateRegistration registration, CommandOrigin origin, CancellationToken cancellationToken)
    {
        ConfigDbContext context = scope.Context;
        CertificateInfo info = registration.Info;
        // A refusal is audited as what was attempted: an add for a new ID, an update for one that exists.
        string action = !registration.Id.IsEmpty && await context.Certificates.AnyAsync(c => c.Id == registration.Id, cancellationToken).ConfigureAwait(false)
            ? "certificate.update"
            : "certificate.add";
        if (Validate(registration) is { } problem)
        {
            return Rejected(origin, action, registration.Id, problem);
        }

        if (IsBuiltIn(registration.Id))
        {
            return Rejected(origin, action, registration.Id, BuiltInError());
        }

        List<string> names = CertificateNames.Normalize(info.SubjectAlternativeNames, out List<string> rejected);
        if (rejected.Count > 0)
        {
            LogNamesDropped(registration.Id, rejected.Count);
        }

        if (names.Count == 0 && registration.Source != CertificateSource.LocalCaRoot)
        {
            return Rejected(origin, action, registration.Id, new ConfigError(string.Empty, CertificateErrorCodes.NamesRequired, "the certificate covers no usable name (a DNS name, a wildcard or an IP address)"));
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        CertificateRow? row = await context.Certificates.FirstOrDefaultAsync(c => c.Id == registration.Id, cancellationToken).ConfigureAwait(false);
        List<string> oldNames = row is null ? [] : await context.CertificateNames.Where(n => n.CertificateId == registration.Id).Select(n => n.Name).ToListAsync(cancellationToken).ConfigureAwait(false);
        bool isNew = row is null;
        ServingFacts before = row is null ? default : ServingFacts.Of(row, oldNames);

        row ??= new CertificateRow { Id = registration.Id, CreatedAt = now };
        row.Source = registration.Source;
        row.Status = registration.Status;
        row.StatusMessage = null;
        row.Subject = info.Subject;
        row.Issuer = info.Issuer;
        row.SerialNumber = info.SerialNumber;
        row.AuthorityKeyIdentifier = info.AuthorityKeyIdentifier;
        row.Sha256Fingerprint = info.Sha256Fingerprint;
        row.KeyAlgorithm = info.KeyAlgorithm;
        row.NotBefore = info.NotBefore;
        row.NotAfter = info.NotAfter;
        row.ChainIssuers = JsonSerializer.Serialize(info.ChainIssuers);
        row.PublicChainPem = info.PublicChainPem;
        row.Label = registration.Label ?? row.Label;
        row.Notes = registration.Notes ?? row.Notes;

        ServingFacts after = ServingFacts.Of(row, names);
        if (!isNew && context.Entry(row).State == EntityState.Unchanged && after == before)
        {
            return new CertificateChangeOutcome(new CertificateChangeResult(CertificateChangeStatus.Unchanged, ToSummary(row, names, now)), null, false);
        }

        row.UpdatedAt = now;
        if (isNew)
        {
            context.Certificates.Add(row);
        }

        await ReplaceNamesAsync(context, row.Id, oldNames, names, cancellationToken).ConfigureAwait(false);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        AuditEntry audit = Entry(origin, isNew ? "certificate.add" : "certificate.update", row.Id, AuditOutcome.Success) with
        {
            Details = Json(w =>
            {
                w.WriteString("source", row.Source.ToName());
                w.WriteString("status", row.Status.ToName());
                w.WriteString("fingerprint", row.Sha256Fingerprint);
                w.WriteString("notAfter", row.NotAfter.ToString("O", CultureInfo.InvariantCulture));
                w.WriteNumber("names", names.Count);
            }),
        };
        LogRegistered(row.Id, isNew ? "added" : "updated", row.Source, row.Sha256Fingerprint);
        return new CertificateChangeOutcome(
            new CertificateChangeResult(CertificateChangeStatus.Committed, ToSummary(row, names, now)),
            audit,
            Republish: before != after);
    }

    private async Task<CertificateChangeOutcome> RegisterPendingCoreAsync(
        CertificateChangeScope scope, CertificateId id, CertificateSource source, IReadOnlyList<string> requested, string? label, CommandOrigin origin, CancellationToken cancellationToken)
    {
        ConfigDbContext context = scope.Context;
        ConfigError? problem = id.IsEmpty ? new ConfigError(string.Empty, ConfigErrorCodes.IdEmpty, "the certificate ID can't be empty")
            : !Enum.IsDefined(source) ? new ConfigError("/source", ConfigErrorCodes.EnumUndefined, $"unknown certificate source {source}")
            : LengthProblem("/label", "label", label, MaxLabelLength) ?? (IsBuiltIn(id) ? BuiltInError() : null);
        List<string> names = CertificateNames.Normalize(requested, out List<string> rejected);
        problem ??= rejected.Count > 0 ? new ConfigError("/names", CertificateErrorCodes.NameInvalid, $"'{rejected[0]}' is not a DNS name, a wildcard or an IP address")
            : names.Count == 0 && source != CertificateSource.LocalCaRoot ? new ConfigError("/names", CertificateErrorCodes.NamesRequired, "a certificate needs at least one name")
            : await context.Certificates.AnyAsync(c => c.Id == id, cancellationToken).ConfigureAwait(false)
                ? new ConfigError(string.Empty, CertificateErrorCodes.Exists, "a certificate with this ID is already registered")
                : null;
        if (problem is not null)
        {
            return Rejected(origin, "certificate.add", id, problem);
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        CertificateRow row = new()
        {
            Id = id,
            Source = source,
            Status = CertificateStatus.Pending,
            NotBefore = now,
            NotAfter = now,
            Label = label,
            CreatedAt = now,
            UpdatedAt = now,
        };
        context.Certificates.Add(row);
        await ReplaceNamesAsync(context, id, [], names, cancellationToken).ConfigureAwait(false);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        AuditEntry audit = Entry(origin, "certificate.add", id, AuditOutcome.Success) with
        {
            Details = Json(w =>
            {
                w.WriteString("source", source.ToName());
                w.WriteString("status", CertificateStatus.Pending.ToName());
                w.WriteNumber("names", names.Count);
            }),
        };
        LogRegistered(id, "added (pending)", source, string.Empty);
        return new CertificateChangeOutcome(new CertificateChangeResult(CertificateChangeStatus.Committed, ToSummary(row, names, now)), audit, Republish: false);
    }

    private async Task<CertificateChangeOutcome> SetStatusCoreAsync(
        CertificateChangeScope scope, CertificateId id, CertificateStatus status, string? message, CommandOrigin origin, CancellationToken cancellationToken)
    {
        ConfigDbContext context = scope.Context;
        if (!Enum.IsDefined(status))
        {
            return Rejected(origin, "certificate.update", id, new ConfigError("/status", ConfigErrorCodes.EnumUndefined, $"unknown certificate status {status}"));
        }

        if (LengthProblem("/message", "message", message, MaxNotesLength) is { } tooLong)
        {
            return Rejected(origin, "certificate.update", id, tooLong);
        }

        CertificateRow? row = await context.Certificates.FirstOrDefaultAsync(c => c.Id == id, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return NotFound(origin, "certificate.update", id);
        }

        if (status == CertificateStatus.Active && row.PublicChainPem.Length == 0)
        {
            return Rejected(origin, "certificate.update", id, new ConfigError("/status", CertificateErrorCodes.NoChain, "a certificate without a chain can't be active; register it with the issued chain"));
        }

        string? statusMessage = status == CertificateStatus.Failed ? message : null;
        List<string> names = await context.CertificateNames.Where(n => n.CertificateId == id).Select(n => n.Name).ToListAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (row.Status == status && row.StatusMessage == statusMessage)
        {
            return new CertificateChangeOutcome(new CertificateChangeResult(CertificateChangeStatus.Unchanged, ToSummary(row, names, now)), null, false);
        }

        ServingFacts before = ServingFacts.Of(row, names);
        row.Status = status;
        row.StatusMessage = statusMessage;
        row.UpdatedAt = now;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        AuditEntry audit = Entry(origin, "certificate.update", id, AuditOutcome.Success) with
        {
            Details = Json(w => w.WriteString("status", status.ToName())),
        };
        return new CertificateChangeOutcome(
            new CertificateChangeResult(CertificateChangeStatus.Committed, ToSummary(row, names, now)),
            audit,
            Republish: before != ServingFacts.Of(row, names));
    }

    private async Task<CertificateChangeOutcome> UpdateDetailsCoreAsync(
        CertificateChangeScope scope, CertificateId id, string? label, string? notes, CommandOrigin origin, CancellationToken cancellationToken)
    {
        ConfigDbContext context = scope.Context;
        label = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        notes = string.IsNullOrWhiteSpace(notes) ? null : notes;
        if ((LengthProblem("/label", "label", label, MaxLabelLength) ?? LengthProblem("/notes", "notes", notes, MaxNotesLength)) is { } tooLong)
        {
            return Rejected(origin, "certificate.update", id, tooLong);
        }

        CertificateRow? row = await context.Certificates.FirstOrDefaultAsync(c => c.Id == id, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return NotFound(origin, "certificate.update", id);
        }

        List<string> names = await context.CertificateNames.Where(n => n.CertificateId == id).Select(n => n.Name).ToListAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (row.Label == label && row.Notes == notes)
        {
            return new CertificateChangeOutcome(new CertificateChangeResult(CertificateChangeStatus.Unchanged, ToSummary(row, names, now)), null, false);
        }

        row.Label = label;
        row.Notes = notes;
        row.UpdatedAt = now;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        AuditEntry audit = Entry(origin, "certificate.update", id, AuditOutcome.Success) with
        {
            Details = Json(w => w.WriteString("fields", "label,notes")),
        };
        return new CertificateChangeOutcome(new CertificateChangeResult(CertificateChangeStatus.Committed, ToSummary(row, names, now)), audit, Republish: false);
    }

    private async Task<CertificateChangeOutcome> DeleteCoreAsync(CertificateChangeScope scope, CertificateId id, CommandOrigin origin, CancellationToken cancellationToken)
    {
        ConfigDbContext context = scope.Context;
        if (!await context.Certificates.AnyAsync(c => c.Id == id, cancellationToken).ConfigureAwait(false))
        {
            return NotFound(origin, "certificate.delete", id);
        }

        List<ConfigError> users = BindingsTo(scope.Document, id);
        if (users.Count > 0)
        {
            return new CertificateChangeOutcome(
                new CertificateChangeResult(CertificateChangeStatus.Invalid, Errors: users),
                Entry(origin, "certificate.delete", id, AuditOutcome.Invalid) with
                {
                    Details = Json(w =>
                    {
                        w.WriteString("status", "invalid");
                        w.WriteStartArray("errors");
                        w.WriteStringValue(CertificateErrorCodes.InUse);
                        w.WriteEndArray();
                        w.WriteNumber("uses", users.Count);
                    }),
                },
                Republish: false);
        }

        await context.CertificateNames.Where(n => n.CertificateId == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await context.Certificates.Where(c => c.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        LogDeleted(id);
        return new CertificateChangeOutcome(
            new CertificateChangeResult(CertificateChangeStatus.Committed),
            Entry(origin, "certificate.delete", id, AuditOutcome.Success),
            Republish: true);
    }

    /// <summary>The places in <paramref name="document"/> that bind to <paramref name="id"/>: Specific hosts and the default.</summary>
    private static List<ConfigError> BindingsTo(ConfigDocument document, CertificateId id)
    {
        List<ConfigError> users = [];
        for (int i = 0; i < document.Hosts.Count; i++)
        {
            HostConfig host = document.Hosts[i];
            if (host.Certificate is { Mode: CertificateBindingMode.Specific } binding && binding.CertificateId == id)
            {
                users.Add(new ConfigError(
                    $"/hosts/{i.ToString(CultureInfo.InvariantCulture)}/certificate",
                    CertificateErrorCodes.InUse,
                    $"host {host.Id} ({string.Join(", ", host.Names)}) is bound to this certificate; bind it to another certificate or to Auto first"));
            }
        }

        if (document.Settings.DefaultCertificateId == id)
        {
            users.Add(new ConfigError("/settings/defaultCertificateId", CertificateErrorCodes.InUse, "it is the default certificate; choose another default first"));
        }

        return users;
    }

    /// <summary>The ID belongs to a built-in certificate, which the inventory table must not shadow (the built-in wins).</summary>
    private bool IsBuiltIn(CertificateId id) => _builtIns?.Current.Any(b => b.Id == id) == true;

    private static ConfigError BuiltInError() =>
        new(string.Empty, CertificateErrorCodes.BuiltIn, "this ID belongs to a built-in certificate (the process's own, not the vault's); choose another ID");

    private static ConfigError? Validate(CertificateRegistration registration)
    {
        CertificateInfo info = registration.Info;
        return registration.Id.IsEmpty ? new ConfigError("/id", ConfigErrorCodes.IdEmpty, "the certificate ID can't be empty")
            : !Enum.IsDefined(registration.Source) ? new ConfigError("/source", ConfigErrorCodes.EnumUndefined, $"unknown certificate source {registration.Source}")
            : !Enum.IsDefined(registration.Status) ? new ConfigError("/status", ConfigErrorCodes.EnumUndefined, $"unknown certificate status {registration.Status}")
            : info is null ? new ConfigError("/info", ConfigErrorCodes.ValueNull, "the certificate info is missing")
            : info.SubjectAlternativeNames is null || info.PublicChainPem is null || info.Sha256Fingerprint is null || info.Issuer is null || info.Subject is null
                || info.SerialNumber is null || info.KeyAlgorithm is null || info.ChainIssuers is null
                ? new ConfigError("/info", ConfigErrorCodes.ValueNull, "the certificate info has missing parts")
            : info.NotAfter <= info.NotBefore ? new ConfigError("/info/notAfter", CertificateErrorCodes.ValidityInvalid, "NotAfter must be after NotBefore")
            : info.Sha256Fingerprint.Length == 0 ? new ConfigError("/info/sha256Fingerprint", CertificateErrorCodes.FingerprintRequired, "the certificate needs its SHA-256 fingerprint")
            : registration.Status == CertificateStatus.Active && info.PublicChainPem.Length == 0
                ? new ConfigError("/info/publicChainPem", CertificateErrorCodes.NoChain, "an active certificate needs its public chain")
            : LengthProblem("/label", "label", registration.Label, MaxLabelLength) ?? LengthProblem("/notes", "notes", registration.Notes, MaxNotesLength);
    }

    private static ConfigError? LengthProblem(string path, string what, string? value, int max) =>
        value is not null && value.Length > max ? new ConfigError(path, CertificateErrorCodes.TextTooLong, $"the {what} is limited to {max} characters") : null;

    private static async Task ReplaceNamesAsync(
        ConfigDbContext context, CertificateId id, List<string> oldNames, List<string> names, CancellationToken cancellationToken)
    {
        foreach (string removed in oldNames.Except(names, StringComparer.OrdinalIgnoreCase))
        {
            await context.CertificateNames.Where(n => n.CertificateId == id && n.Name == removed).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (string added in names.Except(oldNames, StringComparer.OrdinalIgnoreCase))
        {
            context.CertificateNames.Add(new CertificateNameRow { CertificateId = id, Name = added });
        }
    }

    private static async Task<ILookup<CertificateId, string>> NamesOfAsync(ConfigDbContext context, IEnumerable<CertificateId> ids, CancellationToken cancellationToken)
    {
        CertificateId[] wanted = [.. ids];
        var rows = await context.CertificateNames.AsNoTracking()
            .Where(n => wanted.Contains(n.CertificateId))
            .Select(n => new { n.CertificateId, n.Name })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.OrderBy(n => n.Name, StringComparer.Ordinal).ToLookup(n => n.CertificateId, n => n.Name);
    }

    private static CertificateSummary ToSummary(CertificateRow row, IReadOnlyList<string> names, DateTimeOffset now) => new(
        row.Id,
        row.Source,
        row.Status,
        row.StatusMessage,
        row.Status == CertificateStatus.Pending || now < row.NotBefore ? CertificateValidity.NotYetValid : now > row.NotAfter ? CertificateValidity.Expired : CertificateValidity.Valid,
        [.. names.Order(StringComparer.Ordinal)],
        row.NotBefore,
        row.NotAfter,
        row.Subject,
        row.Issuer,
        row.SerialNumber,
        row.AuthorityKeyIdentifier,
        row.Sha256Fingerprint,
        row.KeyAlgorithm,
        row.Label,
        row.Notes,
        row.CreatedAt,
        row.UpdatedAt);

    private static AuditEntry Entry(CommandOrigin origin, string action, CertificateId id, AuditOutcome outcome) =>
        new(origin.Actor, origin.Channel, action, outcome) { Target = id.ToString(), ClientIp = origin.ClientIp };

    private static CertificateChangeOutcome Rejected(CommandOrigin origin, string action, CertificateId id, ConfigError error) =>
        new(
            new CertificateChangeResult(CertificateChangeStatus.Invalid, Errors: [error]),
            Entry(origin, action, id, AuditOutcome.Invalid) with { Details = Json(w => w.WriteString("status", "invalid")) },
            Republish: false);

    private static CertificateChangeOutcome NotFound(CommandOrigin origin, string action, CertificateId id) =>
        new(
            new CertificateChangeResult(CertificateChangeStatus.NotFound),
            Entry(origin, action, id, AuditOutcome.Conflict) with { Details = Json(w => w.WriteString("status", "notFound")) },
            Republish: false);

    private static string Json(Action<Utf8JsonWriter> write)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>What decides whether a change affects serving: the compiler's view of a certificate.</summary>
    private readonly record struct ServingFacts(bool Active, DateTimeOffset NotBefore, DateTimeOffset NotAfter, string Fingerprint, string Chain, string Names)
    {
        public static ServingFacts Of(CertificateRow row, IEnumerable<string> names) => new(
            row.Status == CertificateStatus.Active,
            row.NotBefore,
            row.NotAfter,
            row.Sha256Fingerprint,
            row.PublicChainPem,
            string.Join('\n', names.Select(n => n.ToLowerInvariant()).Order(StringComparer.Ordinal)));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Certificate {Certificate} {Change} ({Source}, fingerprint {Fingerprint}).")]
    private partial void LogRegistered(CertificateId certificate, string change, CertificateSource source, string fingerprint);

    [LoggerMessage(Level = LogLevel.Information, Message = "Certificate {Certificate} deleted.")]
    private partial void LogDeleted(CertificateId certificate);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Certificate {Certificate} lists {Count} name(s) that are not DNS names, wildcards or IP addresses; they are left out of the inventory.")]
    private partial void LogNamesDropped(CertificateId certificate, int count);
}

/// <summary>The stable <see cref="ConfigError.Code"/>s of <see cref="CertificateService"/>.</summary>
public static class CertificateErrorCodes
{
    /// <summary>A certificate still bound by a host's Specific binding, or the default certificate, can't be deleted.</summary>
    public const string InUse = "certificate.delete.inUse";

    public const string NamesRequired = "certificate.names.required";
    public const string NameInvalid = "certificate.name.invalid";
    public const string NoChain = "certificate.chain.required";
    public const string ValidityInvalid = "certificate.validity.invalid";
    public const string FingerprintRequired = "certificate.fingerprint.required";
    public const string Exists = "certificate.exists";
    public const string BuiltIn = "certificate.builtIn";
    public const string TextTooLong = "certificate.text.tooLong";

    // The vault flows (CertificateOperations).
    public const string NotReplaceable = "certificate.replace.notAllowed";
    public const string NotReissuable = "certificate.reissue.notAllowed";
    public const string IssuanceRefused = "certificate.issue.refused";
    public const string LocalCaMissing = "certificate.localCa.missing";
    public const string LocalCaInUse = "certificate.localCa.inUse";
    public const string KeyNotDeleted = "certificate.vault.keyNotDeleted";
    public const string ImportTooLarge = "certificate.import.tooLarge";
    public const string ImportUnreadable = "certificate.import.unreadable";
    public const string ImportWrongPassword = "certificate.import.wrongPassword";
    public const string ImportUnsupportedKeyFormat = "certificate.import.unsupportedKeyFormat";
    public const string ImportNoMatchingKey = "certificate.import.noMatchingKey";
    public const string ImportNoUsableName = "certificate.import.noUsableName";
    public const string ImportWeakKey = "certificate.import.weakKey";
    public const string ImportCaCertificate = "certificate.import.caCertificate";
}

/// <summary>What <see cref="CertificateService.RegisterRecoveredAsync"/> found and did for one certificate.</summary>
internal enum CertificateRecoveryStep
{
    /// <summary>There was no row and no delete on record: it is registered now.</summary>
    Registered,

    /// <summary>The inventory has a row already; nothing was touched.</summary>
    AlreadyRegistered,

    /// <summary>The audit trail records a successful delete of it: the database was not lost, the vault's key deletion failed. Nothing was registered.</summary>
    WasDeleted,

    /// <summary>It could not be registered (refused, or the store does not accept changes).</summary>
    Failed,
}
