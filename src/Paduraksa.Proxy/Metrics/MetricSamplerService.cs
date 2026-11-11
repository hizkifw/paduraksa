using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Paduraksa.Proxy.Metrics;

/// <summary>Runs <see cref="MetricSampler.Tick"/> every second for the life of the core host.</summary>
public sealed partial class MetricSamplerService(MetricSampler sampler, TimeProvider time, ILogger<MetricSamplerService> logger) : BackgroundService
{
    /// <summary>How often the registry is read.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(Interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    sampler.Tick();
                }
                catch (Exception ex)
                {
                    // A sampling bug must not take the core host down: one second is lost and the next tick tries again.
                    LogTickFailed(logger, ex);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The metric sampler failed to read the counters; it tries again on the next tick.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);
}
