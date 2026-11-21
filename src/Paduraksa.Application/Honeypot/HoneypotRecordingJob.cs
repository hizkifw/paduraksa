using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Paduraksa.Application.Honeypot;

/// <summary>
/// Folds what the trap's sink queued into sessions and writes them every <see cref="WriteInterval"/>, and deletes what is past retention every
/// <see cref="PruneInterval"/> (docs/design/waf.md "Honeypot", "Recording"). A hosted service of the core host, registered before the access-log drain like the analytics job, so its last
/// write on shutdown follows the requests the public host answered while it drained. A write that fails puts its batch back with the recorder and is
/// tried again; what waits is bounded there, so a database that stays down loses the oldest rows and never memory.
/// </summary>
public sealed partial class HoneypotRecordingJob(HoneypotStore store, HoneypotRetention retention, TimeProvider time, ILogger<HoneypotRecordingJob> logger) : BackgroundService
{
    /// <summary>How often the queue is folded into sessions and written.</summary>
    public static readonly TimeSpan WriteInterval = TimeSpan.FromSeconds(5);

    /// <summary>How often rows past retention are deleted.</summary>
    public static readonly TimeSpan PruneInterval = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(WriteInterval, time);
        var nextPrune = time.GetUtcNow();
        try
        {
            while (true)
            {
                await WriteAsync(stoppingToken).ConfigureAwait(false);
                if (time.GetUtcNow() >= nextPrune)
                {
                    await PruneAsync(stoppingToken).ConfigureAwait(false);
                    nextPrune = time.GetUtcNow() + PruneInterval;
                }

                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping: what the public host answered on its way out is written below.
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

    /// <summary>One retention pass; public for tests.</summary>
    public async Task PruneAsync(CancellationToken cancellationToken)
    {
        try
        {
            var removed = await store.PruneAsync(time.GetUtcNow(), retention, cancellationToken).ConfigureAwait(false);
            if (removed.Total > 0)
            {
                LogPruned(logger, removed.Sessions, removed.Requests);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Writing the honeypot's sessions and requests failed; they are kept in memory and written at the next interval.")]
    private static partial void LogWriteFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Sessions} honeypot session(s) and {Requests} request(s) past retention or over the caps.")]
    private static partial void LogPruned(ILogger logger, int sessions, int requests);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deleting old honeypot sessions failed; it is tried again at the next interval.")]
    private static partial void LogPruneFailed(ILogger logger, Exception exception);
}
