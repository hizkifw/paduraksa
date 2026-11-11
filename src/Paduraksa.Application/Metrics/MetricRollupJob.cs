using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Paduraksa.Proxy.Metrics;

namespace Paduraksa.Application.Metrics;

/// <summary>
/// Persists the sampler's finished minutes to <see cref="MetricRollupStore"/> every <see cref="MetricRollupOptions.WriteInterval"/>
/// and deletes what is past <see cref="MetricRollupOptions.Retention"/> every <see cref="MetricRollupOptions.PruneInterval"/> (PLAN §7.4).
/// A hosted service of the core host. A write that fails puts the minutes back and tries again at the next interval; the
/// queue is bounded, so a database that stays down loses the oldest minutes, not memory.
/// </summary>
public sealed partial class MetricRollupJob(
    MetricRollupBacklog queue, MetricRollupStore store, MetricRollupOptions options, TimeProvider time, ILogger<MetricRollupJob> logger) : BackgroundService
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
            // Stopping: the minutes still queued are written by the final flush below.
        }

        await FlushAsync().ConfigureAwait(false);
    }

    /// <summary>One write pass; public for tests, which drive the job without its timer.</summary>
    public async Task WriteAsync(CancellationToken cancellationToken)
    {
        var minutes = queue.Drain();
        if (minutes.Count == 0)
        {
            return;
        }

        try
        {
            await store.WriteAsync(minutes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            queue.Requeue(minutes);
            if (ex is OperationCanceledException)
            {
                throw;
            }

            LogWriteFailed(logger, ex, minutes.Count);
        }
    }

    /// <summary>One retention pass; public for tests.</summary>
    public async Task PruneAsync(CancellationToken cancellationToken)
    {
        try
        {
            var removed = await store.PruneAsync(time.GetUtcNow() - options.Retention, cancellationToken).ConfigureAwait(false);
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
            await WriteAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogWriteFailed(logger, ex, queue.Count);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Writing {Count} metric rollup minute(s) failed; they are kept in memory and written at the next interval.")]
    private static partial void LogWriteFailed(ILogger logger, Exception exception, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Count} metric rollup row(s) older than {Retention}.")]
    private static partial void LogPruned(ILogger logger, int count, TimeSpan retention);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deleting old metric rollups failed; it is tried again at the next interval.")]
    private static partial void LogPruneFailed(ILogger logger, Exception exception);
}
