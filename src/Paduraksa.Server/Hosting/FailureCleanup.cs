using Microsoft.Extensions.Logging;

namespace Paduraksa.Server.Hosting;

/// <summary>
/// Cleanup that runs because something already failed. If such a cleanup throws inside a <c>catch</c> block, the
/// cleanup error escapes and the original failure is lost. These helpers log the cleanup error instead and return
/// normally, so the caller's <c>throw;</c> always surfaces the original exception.
/// </summary>
internal static partial class FailureCleanup
{
    /// <summary>Runs <paramref name="cleanup"/>; a failure is logged as <paramref name="step"/> and swallowed.</summary>
    public static void Run(Action cleanup, ILogger logger, string step)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            cleanup();
        }
#pragma warning disable CA1031 // The original failure is what the caller rethrows; this one is only logged.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogCleanupFailed(logger, step, ex);
        }
    }

    /// <inheritdoc cref="Run"/>
    public static async Task RunAsync(Func<Task> cleanup, ILogger logger, string step)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            await cleanup().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The original failure is what the caller rethrows; this one is only logged.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogCleanupFailed(logger, step, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Cleanup after a failure failed too ({Step}); the original error is rethrown")]
    private static partial void LogCleanupFailed(ILogger logger, string step, Exception exception);
}
