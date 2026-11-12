namespace Paduraksa.Server.Hosting;

internal static partial class DataDirectoryLogs
{
    [LoggerMessage(Level = LogLevel.Critical, Message = "The data directory is in use by another Paduraksa process")]
    public static partial void InUse(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The file system of {DataDirectory} does not support file locks, so nothing keeps a second Paduraksa process or a restore out of the directory while this one runs: make sure only one uses it")]
    public static partial void NotEnforced(ILogger logger, string dataDirectory);
}
