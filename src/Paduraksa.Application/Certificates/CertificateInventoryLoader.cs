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
/// snapshot compiler resolve certificates the vault only serves asynchronously (PLAN §8.2, §9.1).
/// </summary>
/// <remarks>
/// <para><b>Steps.</b> (1) Read the certificate metadata (the inventory table, plus the process's
/// <see cref="IBuiltInCertificates"/>). (2) Judge each against the clock (<see cref="TimeProvider"/>): only an
/// <see cref="CertificateStatus.Active"/> certificate inside its validity period is valid. (3) Work out which valid
/// certificates the document can choose (<see cref="CertificateSelection.Needed"/>: the named default, Specific
/// bindings, the best match per name of each enabled Auto host) and load exactly those through
/// <see cref="IServerCertificateSource"/>. (4) Wrap each context in a <see cref="ServerCertificate"/> and hand back the
/// immutable inventory.</para>
/// <para><b>A certificate that can't be loaded</b> (the source returns null: no key, no chain; or throws) is unusable,
/// not fatal: its reason is recorded, a warning is logged, and the selection runs again without it, so an Auto host
/// falls back to the next best certificate and a Specific binding to it degrades to a compile warning (see
/// <c>ProxyConfigMapping</c>). A certificate that is valid but needed by nothing isn't loaded at all.</para>
/// <para><b>Dedup.</b> Contexts come from the source as they are: the vault returns the same instance for an unchanged
/// certificate, and the proxy's certificate table deduplicates by that instance, so recompiling with an unchanged
/// inventory reuses every options object, while a renewed certificate (a new instance) takes effect with the next publish.
/// The loader never disposes a context.</para>
/// <para><b>Failures reading the table</b> (the database is unusable, which is also why startup may be degraded) give an
/// inventory of the built-in certificates only. A caller that must not fail on that (startup) passes no context.</para>
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

    /// <summary>
    /// The inventory for compiling <paramref name="document"/>. <paramref name="context"/> reads the certificate table;
    /// null: only the built-in certificates.
    /// </summary>
    public async Task<CertificateInventory> LoadAsync(ConfigDbContext? context, ConfigDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        List<InventoryCertificate> entries = [];
        HashSet<CertificateId> seen = [];
        CertificateId? fallbackDefault = null;
        List<DateTimeOffset> boundaries = [];

        foreach (BuiltInCertificate builtIn in _builtIns?.Current ?? [])
        {
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
            foreach (InventoryCertificate stored in await ReadTableAsync(context, now, boundaries, cancellationToken).ConfigureAwait(false))
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

            foreach (CertificateId id in toLoad)
            {
                if (await LoadOneAsync(id, cancellationToken).ConfigureAwait(false) is { } serving)
                {
                    loaded[id] = serving;
                }
                else
                {
                    failed[id] = _source is null ? "no certificate source is configured" : "the vault has no usable key and chain for it";
                }
            }
        }

        return new CertificateInventory(
            [
                .. entries.Select(e => loaded.TryGetValue(e.Id, out ServerCertificate? serving)
                    ? e with { Serving = serving }
                    : failed.TryGetValue(e.Id, out string? reason) ? e with { UnusableReason = reason } : e),
            ],
            fallbackDefault,
            boundaries.Count == 0 ? null : boundaries.Min());
    }

    private static async Task<List<InventoryCertificate>> ReadTableAsync(
        ConfigDbContext context, DateTimeOffset now, List<DateTimeOffset> boundaries, CancellationToken cancellationToken)
    {
        var rows = await context.Certificates
            .AsNoTracking()
            .Select(c => new { c.Id, c.Status, c.NotBefore, c.NotAfter })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var names = await context.CertificateNames
            .AsNoTracking()
            .Select(n => new { n.CertificateId, n.Name })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        ILookup<CertificateId, string> namesById = names.ToLookup(n => n.CertificateId, n => n.Name);
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
                return new ServerCertificate(context);
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
        DateTimeOffset validFrom = notBefore - NotBeforeTolerance;
        DateTimeOffset expiredFrom = notAfter + TimeSpan.FromSeconds(1);
        string? reason = status switch
        {
            CertificateStatus.Pending => "it is pending: no chain is attached yet",
            CertificateStatus.Failed => "its issuance failed",
            _ when now < validFrom => $"it isn't valid before {notBefore:u}",
            _ when now >= expiredFrom => $"it expired at {notAfter:u}",
            _ => null,
        };
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "The certificate source has no serving context for certificate {Certificate} (no key or no chain): it is left out of this compile.")]
    private partial void LogNotLoaded(CertificateId certificate);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't load certificate {Certificate} for serving ({ExceptionType}); it is left out of this compile.")]
    private partial void LogLoadFailed(CertificateId certificate, string exceptionType);
}
