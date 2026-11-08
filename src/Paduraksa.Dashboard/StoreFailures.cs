using Microsoft.Extensions.Logging;

using MudBlazor;

namespace Paduraksa.Dashboard;

/// <summary>
/// How a page survives a store call that fails (the database busy, a brief I/O error). An exception out of an event handler or a
/// lifecycle method ends the circuit, so a handler that calls the Application services catches what it throws, logs it at Warning and
/// says so on the page, and the person tries again. Cancellation passes through: the circuit is going away.
/// </summary>
/// <remarks>
/// A page that already shows its own failure (an alert with <b>Try again</b> beside what failed to load) catches with
/// <see cref="Survivable"/> and logs with <see cref="LogStoreFailure"/>; a one-off action uses <see cref="TryAsync"/>, which says it in a toast.
/// </remarks>
public static partial class StoreFailures
{
    /// <summary>Whether a page reports <paramref name="exception"/> and carries on: anything but cancellation.</summary>
    public static bool Survivable(Exception exception) => exception is not OperationCanceledException;

    /// <summary>The toast's text for <paramref name="failure"/> ("Couldn't load the older events.").</summary>
    public static string Message(string failure) => $"{failure} Try again; the details are in the server log.";

    /// <summary>
    /// Runs <paramref name="action"/>. When it throws, logs <paramref name="failure"/> at Warning and shows it as an error toast that asks
    /// to try again. True when it completed.
    /// </summary>
    public static async Task<bool> TryAsync(this ILogger logger, ISnackbar snackbar, string failure, Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(snackbar);
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            await action();
            return true;
        }
        catch (Exception ex) when (Survivable(ex))
        {
            LogFailed(logger, ex, failure);
            snackbar.Add(Message(failure), Severity.Error);
            return false;
        }
    }

    /// <summary>Logs <paramref name="failure"/> at Warning, for a page that shows it its own way.</summary>
    public static void LogStoreFailure(this ILogger logger, Exception exception, string failure)
    {
        ArgumentNullException.ThrowIfNull(logger);
        LogFailed(logger, exception, failure);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A dashboard page's store call failed: {Failure}")]
    private static partial void LogFailed(ILogger logger, Exception exception, string failure);
}
