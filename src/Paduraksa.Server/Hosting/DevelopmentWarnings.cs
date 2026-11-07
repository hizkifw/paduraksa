using Microsoft.Extensions.Logging;

using Paduraksa.Server.Configuration;

namespace Paduraksa.Server.Hosting;

/// <summary>What the process says at startup about development switches that are on where they should not be.</summary>
internal static partial class DevelopmentWarnings
{
    /// <summary>Warns when the placeholder certificate is on in a release build (it is on by default only in a Debug build).</summary>
    public static void Check(ILoggerFactory loggers, PaduraksaOptions options)
    {
        if (options.Development.PlaceholderCertificate && !PaduraksaDevelopmentOptions.IsDebugBuild)
        {
            LogPlaceholderEnabled(loggers.CreateLogger(typeof(DevelopmentWarnings).FullName!));
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The development placeholder certificate is enabled (Paduraksa:Development:PlaceholderCertificate) in a release build: hosts without a certificate of their own are served with an in-memory self-signed one.")]
    private static partial void LogPlaceholderEnabled(ILogger logger);
}
