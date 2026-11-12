using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Paduraksa.Application.Drafts;

/// <summary>
/// Drops the drafts nobody has touched for <see cref="DraftService.Lifetime"/> (and their vault values) once at start and then every hour, so a
/// draft that its owner never comes back to does not keep a password in the vault. A hosted service of the core host. A failed pass is logged and tried
/// again at the next tick.
/// </summary>
public sealed partial class DraftExpiryJob(DraftService drafts, TimeProvider time, ILogger<DraftExpiryJob> logger) : BackgroundService
{
    /// <summary>How often drafts are looked at; a draft lives 30 days, so an hour is exact enough.</summary>
    public static TimeSpan Interval { get; } = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(Interval, time);
        try
        {
            do
            {
                await PassAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
    }

    /// <summary>One pass; public for tests, which drive the job without its timer.</summary>
    public async Task PassAsync(CancellationToken cancellationToken)
    {
        try
        {
            await drafts.DropExpiredAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPassFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dropping expired drafts failed; trying again at the next pass.")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);
}
