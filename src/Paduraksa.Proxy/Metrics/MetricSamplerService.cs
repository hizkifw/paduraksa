using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Paduraksa.Proxy.Metrics;

/// <summary>
/// Runs <see cref="MetricSampler.Tick"/> on every whole second of the clock for the life of the core host, and takes a last reading
/// and flushes the sampler's open minute when it stops (PLAN §7.4).
/// </summary>
public sealed partial class MetricSamplerService(MetricSampler sampler, TimeProvider time, ILogger<MetricSamplerService> logger) : BackgroundService
{
    /// <summary>How often the registry is read.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long until the clock next reaches a whole second: ticks on the seconds keep an interval inside one ten-second bucket and one
    /// minute, so a boundary is crossed only by a tick that is late. A clock exactly on a second waits a whole one.
    /// </summary>
    public static TimeSpan UntilNextSecond(DateTimeOffset now) => TimeSpan.FromMilliseconds(1000 - (now.ToUnixTimeMilliseconds() % 1000));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(UntilNextSecond(time.GetUtcNow()), time, stoppingToken).ConfigureAwait(false);
                Run(sampler.Tick, LogTickFailed);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping: the minute in progress is written by the rollup job, which stops after this.
        }

        Run(sampler.Flush, LogFlushFailed);
    }

    // A sampling bug must not take the core host down: one second is lost and the next tick tries again.
    private void Run(Action work, Action<ILogger, Exception> failed)
    {
        try
        {
            work();
        }
        catch (Exception ex)
        {
            failed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The metric sampler failed to read the counters; it tries again on the next tick.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "The metric sampler failed to flush its open minute as the host stopped; the minute is lost.")]
    private static partial void LogFlushFailed(ILogger logger, Exception exception);
}
