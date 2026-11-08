using Microsoft.Extensions.Logging;

namespace Paduraksa.Server.Hosting;

/// <summary>Log messages of the admin app's internal listener wiring (PLAN §3.1).</summary>
internal static partial class AdminInternalLogs
{
    [LoggerMessage(Level = LogLevel.Critical, Message = "The admin app's internal listener can't start")]
    public static partial void StartupFailed(ILogger logger, Exception exception);
}
