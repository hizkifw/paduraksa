using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Snapshot;

namespace Paduraksa.Application.Cloudflare;

/// <summary>What one refresh did.</summary>
public enum RangeRefreshResult : byte
{
    /// <summary>Cloudflare's list is the one already in force (its age moved).</summary>
    Unchanged,

    /// <summary>A new list was accepted and stored.</summary>
    Replaced,

    /// <summary>No list was accepted; the last good one stays.</summary>
    Failed,
}

/// <summary>
/// Keeps <see cref="CloudflareEdgeRangeSource"/> current: once shortly after startup, then daily with jitter, and with a growing
/// backoff after a failure. It stores an accepted list (<see cref="CloudflareRangeFile"/>) so a restart keeps it, and when the
/// list changed (or its warning did) it has the live config compiled and published again, with no revision: the ranges are
/// state, not config, and a snapshot only holds the list it was compiled with.
/// </summary>
/// <remarks>
/// <para>Proxy never touches the network or the disk for this; this job is the one place that does. A failed fetch, a refused
/// list and a failed write are logged and counted on the source (which raises the warning after a few in a row); none stops the
/// loop or replaces the list in force.</para>
/// <para>A publish is only worth it while the live snapshot trusts the edge ranges (the mode is <c>CloudflareProxy</c>);
/// otherwise the next compile reads the new list anyway.</para>
/// </remarks>
public sealed partial class CloudflareRangeRefresher : BackgroundService
{
    private readonly CloudflareEdgeRangeSource _source;
    private readonly CloudflareRangeFile _file;
    private readonly CloudflareRangeFetcher _fetcher;
    private readonly CloudflareRangeRefreshOptions _options;
    private readonly SnapshotStore _snapshots;
    private readonly Func<Task<bool>> _republishLive;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private string? _publishedWarning;
    private bool _pending;
    private int _publishFailures;

    public CloudflareRangeRefresher(
        CloudflareEdgeRangeSource source,
        CloudflareRangeFile file,
        HttpClient http,
        CloudflareRangeRefreshOptions options,
        SnapshotStore snapshots,
        Func<Task<bool>> republishLive,
        TimeProvider time,
        ILogger<CloudflareRangeRefresher> logger)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(republishLive);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _source = source;
        _file = file;
        _fetcher = new CloudflareRangeFetcher(http, options, time);
        _options = options;
        _snapshots = snapshots;
        _republishLive = republishLive;
        _time = time;
        _logger = logger;

        // What the startup compile saw: the warning only matters again once it differs from it.
        _publishedWarning = source.Status().Warning;
    }

    /// <summary>One fetch, with what follows from it: store, count, and a publish when the live config should change.</summary>
    public async Task<RangeRefreshResult> RefreshOnceAsync(CancellationToken cancellationToken)
    {
        var (list, failure) = await _fetcher.FetchAsync(cancellationToken).ConfigureAwait(false);
        var result = RangeRefreshResult.Failed;
        var changed = false;
        if (list is null)
        {
            _source.RecordFailure(failure ?? "no reason");
            LogFailed(_logger, failure ?? "no reason", _source.ConsecutiveFailures);
        }
        else
        {
            changed = _source.Replace(list);
            result = changed ? RangeRefreshResult.Replaced : RangeRefreshResult.Unchanged;
            try
            {
                _file.Save(list);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogNotStored(_logger, ex, _file.Path);
            }

            if (changed)
            {
                LogReplaced(_logger, list.Count);
            }
        }

        // Level-triggered: what has to be published stays pending until a publish is queued, so a change whose publish
        // failed or was skipped is tried again, and a revoked range can't stay trusted because one attempt went wrong.
        // Besides the edge (a new list, a new warning), the live snapshot is compared with the list in force.
        var warningChanged = !string.Equals(_source.Status().Warning, _publishedWarning, StringComparison.Ordinal);
        if (changed || warningChanged || LiveIsBehind())
        {
            _pending = true;
        }

        await PublishPendingAsync().ConfigureAwait(false);
        return result;
    }

    /// <summary>The live snapshot trusts the edge ranges, but not the ones in force now.</summary>
    private bool LiveIsBehind() =>
        _snapshots.Current.EdgeRanges is { Origin: not EdgeRangeOrigin.Configured } live && !live.SameBlocksAs(_source.Current);

    /// <summary>Asks for the publish a change is waiting on; it stays pending when none was queued.</summary>
    private async Task PublishPendingAsync()
    {
        if (!_pending)
        {
            return;
        }

        if (_snapshots.Current.EdgeRanges is null)
        {
            // A live config that doesn't trust the edge ranges has nothing to publish: the next compile reads the list.
            _pending = false;
            _publishedWarning = _source.Status().Warning;
            return;
        }

        var warning = _source.Status().Warning;
        var queued = false;
        try
        {
            queued = await _republishLive().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            LogPublishFailed(_logger, ex);
        }

        if (queued)
        {
            _pending = false;
            _publishedWarning = warning;
            _publishFailures = 0;
        }
        else
        {
            _publishFailures++;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(_options.StartDelay, _time, stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                RangeRefreshResult result;
                try
                {
                    result = await RefreshOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
#pragma warning disable CA1031 // The loop must outlive anything one refresh throws.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    var reason = $"{ex.GetType().Name}: {Cut(ex.Message)}";
                    _source.RecordFailure(reason);
                    LogFailed(_logger, reason, _source.ConsecutiveFailures);
                    result = RangeRefreshResult.Failed;
                }

                // A publish that wasn't queued is tried again on the failure backoff, not left to the next daily fetch.
                while (_pending && !stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(Pause(RangeRefreshResult.Failed, _publishFailures), _time, stoppingToken).ConfigureAwait(false);
                    await PublishPendingAsync().ConfigureAwait(false);
                }

                await Task.Delay(Pause(result, _source.ConsecutiveFailures), _time, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <summary>The wait before the next fetch: the interval after a good one, else a doubling retry capped at
    /// <see cref="CloudflareRangeRefreshOptions.MaxRetry"/>; a share of <see cref="CloudflareRangeRefreshOptions.Jitter"/> on top.</summary>
    internal TimeSpan Pause(RangeRefreshResult result, int failures)
    {
        TimeSpan baseline;
        TimeSpan jitter;
        if (result == RangeRefreshResult.Failed)
        {
            var doublings = Math.Min(Math.Max(failures - 1, 0), 20);
            baseline = TimeSpan.FromTicks(Math.Min(_options.MinRetry.Ticks << doublings, _options.MaxRetry.Ticks));

            // A retry's jitter is a tenth of its pause, so the first retries stay close to what was asked.
            jitter = baseline / 10;
        }
        else
        {
            baseline = _options.Interval;
            jitter = _options.Jitter;
        }

        return baseline + TimeSpan.FromTicks((long)(jitter.Ticks * Random.Shared.NextDouble()));
    }

    private static string Cut(string text) => text.Length <= 200 ? text : string.Concat(text.AsSpan(0, 200), "...");

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refreshing Cloudflare's edge ranges failed ({Reason}); {Failures} in a row. The last good list stays.")]
    private static partial void LogFailed(ILogger logger, string reason, int failures);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cloudflare's edge ranges changed: {Count} ranges now.")]
    private static partial void LogReplaced(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The Cloudflare edge ranges couldn't be stored in {Path}; they stay in memory and are tried again at the next refresh.")]
    private static partial void LogNotStored(ILogger logger, Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The live config couldn't be compiled again after the Cloudflare edge ranges changed; the next compile reads them.")]
    private static partial void LogPublishFailed(ILogger logger, Exception exception);
}
