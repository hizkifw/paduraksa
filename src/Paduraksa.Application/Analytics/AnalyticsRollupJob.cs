using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Paduraksa.Proxy.Analytics;

namespace Paduraksa.Application.Analytics;

/// <summary>
/// Persists the analytics' closed buckets every <see cref="AnalyticsOptions.WriteInterval"/>, rolls finished hours up, and deletes what is
/// past <see cref="AnalyticsOptions.Retention"/> every <see cref="AnalyticsOptions.PruneInterval"/> (PLAN §7.4). A hosted service of the core
/// host, registered before the access-log drain so it stops after it: the bucket the drain closes as it stops is in the backlog when the
/// job's last write runs. A write that fails puts the batch back and tries again; the backlog is bounded, so a database that stays down loses
/// the oldest buckets and never memory.
/// </summary>
public sealed partial class AnalyticsRollupJob(AnalyticsRollupStore store, AnalyticsOptions options, TimeProvider time, ILogger<AnalyticsRollupJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(options.WriteInterval, time);
        var nextPrune = time.GetUtcNow();
        try
        {
            while (true)
            {
                await WriteAsync(stoppingToken).ConfigureAwait(false);
                await RollUpAsync(stoppingToken).ConfigureAwait(false);
                if (time.GetUtcNow() >= nextPrune)
                {
                    await PruneAsync(stoppingToken).ConfigureAwait(false);
                    nextPrune = time.GetUtcNow() + options.PruneInterval;
                }

                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping: what the drain closed on its way out is written below.
        }

        await FlushAsync().ConfigureAwait(false);
    }

    /// <summary>One write pass; public for tests, which drive the job without its timer.</summary>
    public async Task WriteAsync(CancellationToken cancellationToken)
    {
        try
        {
            await store.WritePendingAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogWriteFailed(logger, ex);
        }
    }

    /// <summary>One roll-up pass; public for tests.</summary>
    public async Task RollUpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await store.RollUpAsync(time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRollUpFailed(logger, ex);
        }
    }

    /// <summary>One retention pass; public for tests.</summary>
    public async Task PruneAsync(CancellationToken cancellationToken)
    {
        try
        {
            var now = time.GetUtcNow();
            var removed = await store.PruneAsync(now - options.Retention, now - TimeSpan.FromTicks(Math.Min(options.FineRetention.Ticks, options.Retention.Ticks)), cancellationToken).ConfigureAwait(false);
            if (removed > 0)
            {
                LogPruned(logger, removed, options.Retention);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPruneFailed(logger, ex);
        }
    }

    private async Task FlushAsync()
    {
        // The token is gone by now; the write is bounded by the database, and a host that is stopping waits for it.
        try
        {
            await store.WritePendingAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogWriteFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Writing the traffic analytics failed; the closed buckets are kept in memory and written at the next interval.")]
    private static partial void LogWriteFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rolling the traffic analytics up into hours failed; it is tried again at the next interval.")]
    private static partial void LogRollUpFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Count} traffic analytics row(s) past retention ({Retention}) or already rolled up.")]
    private static partial void LogPruned(ILogger logger, int count, TimeSpan retention);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deleting old traffic analytics failed; it is tried again at the next interval.")]
    private static partial void LogPruneFailed(ILogger logger, Exception exception);
}
