using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Paduraksa.Core.Options;
using Paduraksa.Proxy.Metrics;

namespace Paduraksa.Application.Metrics;

/// <summary>
/// Persists the sampler's finished minutes to <see cref="MetricRollupStore"/> every <see cref="MetricRollupOptions.WriteInterval"/>
/// and deletes what is past <see cref="MetricRollupOptions.Retention"/> every <see cref="MetricRollupOptions.PruneInterval"/> (docs/design/observability.md "Metrics (M9)").
/// A hosted service of the core host. A write that fails puts the minutes back and tries again at the next interval; the
/// queue is bounded, so a database that stays down loses the oldest minutes, not memory. Before it starts it seeds the sampler with
/// the last hour of rollups, and it is registered before the sampler's service so that it stops after it: the minute the sampler
/// closes as it stops is still in the queue when the job's last write runs.
/// </summary>
/// <remarks>The security series' minutes go to <paramref name="security"/> the same way, on the same schedule and retention.</remarks>
public sealed partial class MetricRollupJob(
    MetricSampler sampler, MetricRollupStore store, SecurityRollupStore security, LiveOptions<MetricRollupOptions> options, TimeProvider time, ILogger<MetricRollupJob> logger) : BackgroundService
{
    private readonly MetricRollupBacklog _queue = sampler.Rollups;
    private readonly SecurityRollupBacklog _securityQueue = sampler.SecurityRollups;

    /// <summary>
    /// The sampler starts from the hour before it. A database that can't be read leaves the one-hour view starting at boot, as it
    /// did before the history existed; it never keeps the host from starting.
    /// </summary>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await SeedAsync(cancellationToken).ConfigureAwait(false);
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The last hour of minutes (and the one before it, which the first minutes of the window may still need), read once; public for tests.</summary>
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var from = now - TimeSpan.FromSeconds(MetricSampler.RingSeconds + 60);
        try
        {
            sampler.Seed(await store.ReadMinutesAsync(from, now, cancellationToken).ConfigureAwait(false));
            sampler.SeedSecurity(await security.ReadMinutesAsync(from, now, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSeedFailed(logger, ex);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(options.Current.WriteInterval, time);
        var nextPrune = time.GetUtcNow();
        try
        {
            while (true)
            {
                await WriteAsync(stoppingToken).ConfigureAwait(false);
                if (time.GetUtcNow() >= nextPrune)
                {
                    await PruneAsync(stoppingToken).ConfigureAwait(false);
                    nextPrune = time.GetUtcNow() + options.Current.PruneInterval;
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
        await WriteAsync(_queue, store.WriteAsync, cancellationToken).ConfigureAwait(false);
        await WriteAsync(_securityQueue, security.WriteAsync, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteAsync<T>(RollupBacklog<T> queue, Func<IReadOnlyList<T>, CancellationToken, Task> write, CancellationToken cancellationToken)
    {
        var minutes = queue.Drain();
        if (minutes.Count == 0)
        {
            return;
        }

        try
        {
            await write(minutes, cancellationToken).ConfigureAwait(false);
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
            // Read on every pass: a retention changed from the dashboard applies at the next one.
            var retention = options.Current.Retention;
            var cutoff = time.GetUtcNow() - retention;
            var removed = await store.PruneAsync(cutoff, cancellationToken).ConfigureAwait(false);
            removed += await security.PruneAsync(cutoff, cancellationToken).ConfigureAwait(false);

            if (removed > 0)
            {
                LogPruned(logger, removed, retention);
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
            LogWriteFailed(logger, ex, _queue.Count + _securityQueue.Count);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reading the last hour of metric rollups failed; the one-hour view starts at this boot.")]
    private static partial void LogSeedFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Writing {Count} metric rollup minute(s) failed; they are kept in memory and written at the next interval.")]
    private static partial void LogWriteFailed(ILogger logger, Exception exception, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Count} metric rollup row(s) older than {Retention}.")]
    private static partial void LogPruned(ILogger logger, int count, TimeSpan retention);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deleting old metric rollups failed; it is tried again at the next interval.")]
    private static partial void LogPruneFailed(ILogger logger, Exception exception);
}
