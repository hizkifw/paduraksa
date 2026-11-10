using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Data;
using Paduraksa.Proxy.Tls;

namespace Paduraksa.Application.Certificates;

/// <summary>
/// Builds the <see cref="CertificateInventory"/> for one compile: the cold-path preload that lets the synchronous
/// snapshot compiler resolve certificates the vault only serves asynchronously. Only the valid certificates the document
/// can choose are loaded, through <see cref="IServerCertificateSource"/>.
/// </summary>
/// <remarks>
/// <para><b>A certificate that can't be loaded</b> (the source returns null: no key, no chain; or throws) is unusable,
/// not fatal: its reason is recorded, a warning is logged, and the selection runs again without it, so an Auto host
/// falls back to the next best certificate and a Specific binding to it degrades to a compile warning. A certificate
/// that is valid but needed by nothing isn't loaded at all.</para>
/// <para><b>Dedup.</b> The vault returns the same context instance for an unchanged certificate and the proxy's
/// certificate table deduplicates by instance, so recompiling reuses every options object while a renewed certificate
/// takes effect with the next publish. The loader never disposes a context.</para>
/// <para><b>Failures reading the table</b> (a degraded start) give an inventory of the built-in certificates only; a
/// caller that must not fail on that passes no context.</para>
/// </remarks>
internal sealed partial class CertificateInventoryLoader
{
    /// <summary>How far ahead of its <c>NotBefore</c> a certificate counts as valid: clocks differ, and a certificate
    /// issued a moment ago must not wait for ours.</summary>
    public static readonly TimeSpan NotBeforeTolerance = TimeSpan.FromMinutes(5);

    private readonly IServerCertificateSource? _source;
    private readonly IBuiltInCertificates? _builtIns;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly bool _simulation;
    private readonly ICertificateCatalog? _catalog;

    public CertificateInventoryLoader(
        IServerCertificateSource? source,
        IBuiltInCertificates? builtIns,
        TimeProvider timeProvider,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _source = source;
        _builtIns = builtIns;
        _timeProvider = timeProvider;
        _logger = logger ?? NullLogger.Instance;
    }

    private CertificateInventoryLoader(ICertificateCatalog? catalog, IBuiltInCertificates? builtIns, TimeProvider timeProvider, ILogger? logger)
        : this(new SimulationCertificateSource(), builtIns, timeProvider, logger)
    {
        _simulation = true;
        _catalog = catalog;
    }

    /// <summary>
    /// A loader for configs that are only simulated: whether a certificate is usable is decided from what the
    /// vault <b>describes</b> (<paramref name="catalog"/>: public facts, no key opened), with the reasons the real loader
    /// gives; and what is served is a placeholder context (<see cref="SimulationCertificateSource"/>), never the vault's.
    /// Without a catalog every certificate the table says is valid counts as usable: nothing can say otherwise.
    /// </summary>
    public static CertificateInventoryLoader ForSimulation(
        ICertificateCatalog? catalog, IBuiltInCertificates? builtIns, TimeProvider timeProvider, ILogger? logger = null) =>
        new(catalog, builtIns, timeProvider, logger);

    /// <summary>
    /// The inventory for compiling <paramref name="document"/>. <paramref name="context"/> reads the certificate table;
    /// null: only the built-in certificates.
    /// </summary>
    public async Task<CertificateInventory> LoadAsync(ConfigDbContext? context, ConfigDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        var now = _timeProvider.GetUtcNow();
        List<InventoryCertificate> entries = [];
        HashSet<CertificateId> seen = [];
        CertificateId? fallbackDefault = null;
        List<DateTimeOffset> boundaries = [];

        // The admin listener's own certificate never takes part in the proxy's selection: it is reserved
        // for the admin port, so Auto never picks it, a Specific binding to it is refused by the validator, and its validity
        // boundaries never trigger a config republish (the table query below leaves it out too).
        foreach (var builtIn in _builtIns?.Current ?? [])
        {
            if (builtIn.Id == AdminCertificate.Id)
            {
                continue;
            }

            if (seen.Add(builtIn.Id))
            {
                entries.Add(Judge(builtIn.Id, CertificateStatus.Active, CertificateNames.Normalize(builtIn.Info.SubjectAlternativeNames, out _), builtIn.Info.NotBefore, builtIn.Info.NotAfter, now, boundaries));
                if (builtIn.IsFallbackDefault)
                {
                    fallbackDefault = builtIn.Id;
                }
            }
        }

        if (context is not null)
        {
            foreach (var stored in await ReadTableAsync(context, now, boundaries, cancellationToken).ConfigureAwait(false))
            {
                if (seen.Add(stored.Id))
                {
                    entries.Add(stored);
                }
            }
        }

        Dictionary<CertificateId, ServerCertificate> loaded = [];
        Dictionary<CertificateId, string> failed = [];
        while (true)
        {
            InventoryCertificate[] valid = [.. entries.Where(e => e.IsValid && !failed.ContainsKey(e.Id))];
            CertificateId[] toLoad = [.. CertificateSelection.Needed(document, valid, fallbackDefault).Where(id => !loaded.ContainsKey(id))];
            if (toLoad.Length == 0)
            {
                break;
            }

            foreach (var id in toLoad)
            {
                var (serving, reason) = _simulation
                    ? await ServeForSimulationAsync(id, now, cancellationToken).ConfigureAwait(false)
                    : (await LoadOneAsync(id, cancellationToken).ConfigureAwait(false), null);
                if (serving is not null)
                {
                    loaded[id] = serving;
                }
                else
                {
                    failed[id] = reason ?? (_source is null ? "no certificate source is configured" : NoUsableKeyAndChain);
                }
            }
        }

        return new CertificateInventory(
            [
                .. entries.Select(e => loaded.TryGetValue(e.Id, out var serving)
                    ? e with { Serving = serving }
                    : failed.TryGetValue(e.Id, out var reason) ? e with { UnusableReason = reason } : e),
            ],
            fallbackDefault,
            boundaries.Count == 0 ? null : boundaries.Min());
    }

    /// <summary>What the real loader says when the vault can't serve a certificate.</summary>
    private const string NoUsableKeyAndChain = "the vault has no usable key and chain for it";

    /// <summary>The simulation's stand-in for <see cref="LoadOneAsync"/>: usable exactly when the real load would
    /// succeed, judged from public facts. Otherwise the vault must describe the certificate (not the local CA root,
    /// whose key never serves) and the chain it holds must be inside its validity period (the same reasons as <see
    /// cref="Judge"/>: the table's row can be older than the vault's renewed pair).</summary>
    private async Task<(ServerCertificate? Serving, string? Reason)> ServeForSimulationAsync(CertificateId id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var builtIn = false;
        foreach (var candidate in _builtIns?.Current ?? [])
        {
            builtIn |= candidate.Id == id;
        }

        if (!builtIn && _catalog is not null)
        {
            VaultCertificate? held;
            try
            {
                held = await _catalog.DescribeAsync(id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogLoadFailed(id, ex.GetType().Name);
                return (null, NoUsableKeyAndChain);
            }

            if (held is null || held.IsLocalCaRoot)
            {
                return (null, NoUsableKeyAndChain);
            }

            if (ValidityReason(CertificateStatus.Active, held.Info.NotBefore, held.Info.NotAfter, now) is { } reason)
            {
                return (null, reason);
            }
        }

        return (await LoadOneAsync(id, cancellationToken).ConfigureAwait(false), null);
    }

    private static async Task<List<InventoryCertificate>> ReadTableAsync(
        ConfigDbContext context, DateTimeOffset now, List<DateTimeOffset> boundaries, CancellationToken cancellationToken)
    {
        var rows = await context.Certificates
            .AsNoTracking()
            .Where(c => c.Id != AdminCertificate.Id)
            .Select(c => new { c.Id, c.Status, c.NotBefore, c.NotAfter })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var names = await context.CertificateNames
            .AsNoTracking()
            .Select(n => new { n.CertificateId, n.Name })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var namesById = names.ToLookup(n => n.CertificateId, n => n.Name);
        return [.. rows.Select(r => Judge(r.Id, r.Status, [.. namesById[r.Id]], r.NotBefore, r.NotAfter, now, boundaries))];
    }

    private async Task<ServerCertificate?> LoadOneAsync(CertificateId id, CancellationToken cancellationToken)
    {
        if (_source is null)
        {
            LogNotLoaded(id);
            return null;
        }

        try
        {
            if (await _source.LoadForServingAsync(id, cancellationToken).ConfigureAwait(false) is { } context)
            {
                return new ServerCertificate(context) { Id = id };
            }

            LogNotLoaded(id);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The vault's own exceptions say what failed without carrying key material; the type is all that's logged.
            LogLoadFailed(id, ex.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// Judges one certificate at <paramref name="now"/>. An active certificate's validity boundaries still ahead (when it
    /// becomes valid, allowing <see cref="NotBeforeTolerance"/>; the moment after it expires) go to
    /// <paramref name="boundaries"/>: the inventory reports the earliest, so something can republish when it passes.
    /// </summary>
    private static InventoryCertificate Judge(
        CertificateId id,
        CertificateStatus status,
        IReadOnlyList<string> names,
        DateTimeOffset notBefore,
        DateTimeOffset notAfter,
        DateTimeOffset now,
        List<DateTimeOffset> boundaries)
    {
        var validFrom = notBefore - NotBeforeTolerance;
        var expiredFrom = notAfter + TimeSpan.FromSeconds(1);
        var reason = ValidityReason(status, notBefore, notAfter, now);
        if (status == CertificateStatus.Active)
        {
            if (validFrom > now)
            {
                boundaries.Add(validFrom);
            }

            if (expiredFrom > now)
            {
                boundaries.Add(expiredFrom);
            }
        }

        return new InventoryCertificate(id, names, notBefore, notAfter, null, reason);
    }

    /// <summary>Why a certificate with this status and validity period can't be used at <paramref name="now"/>; null when it can.</summary>
    private static string? ValidityReason(CertificateStatus status, DateTimeOffset notBefore, DateTimeOffset notAfter, DateTimeOffset now)
    {
        var validFrom = notBefore - NotBeforeTolerance;
        var expiredFrom = notAfter + TimeSpan.FromSeconds(1);
        return status switch
        {
            CertificateStatus.Pending => "it is pending: no chain is attached yet",
            CertificateStatus.Failed => "its issuance failed",
            _ when now < validFrom => $"it isn't valid before {notBefore:u}",
            _ when now >= expiredFrom => $"it expired at {notAfter:u}",
            _ => null,
        };
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The certificate source has no serving context for certificate {Certificate} (no key or no chain): it is left out of this compile.")]
    private partial void LogNotLoaded(CertificateId certificate);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't load certificate {Certificate} for serving ({ExceptionType}); it is left out of this compile.")]
    private partial void LogLoadFailed(CertificateId certificate, string exceptionType);
}
