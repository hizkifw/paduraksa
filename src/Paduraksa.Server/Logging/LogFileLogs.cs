using Microsoft.Extensions.Logging;

namespace Paduraksa.Server.Logging;

/// <summary>
/// The log files' own messages (PLAN §7.4). They are reported through the console logger, never through the error log this writer
/// produces: a failing error log that logged its failures to itself would loop.
/// </summary>
internal static partial class LogFileLogs
{
    /// <summary>An I/O failure the writer survived (it counted it and will try again). Reported at most once a minute per file.</summary>
    // The exception goes to the console's own stack trace; the line stays one line.
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Couldn't {Operation} the log file {Path}; the writer keeps trying.")]
    public static partial void WriteFailed(ILogger logger, string operation, string path, Exception exception);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "The log file {Path} had more than 999 rotations in one second; it was not rotated this time.")]
    public static partial void RotateNameExhausted(ILogger logger, string path);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "{File}: {Count} log lines were dropped because the writer's queue was full.")]
    public static partial void DroppedLines(ILogger logger, string file, long count);
}
