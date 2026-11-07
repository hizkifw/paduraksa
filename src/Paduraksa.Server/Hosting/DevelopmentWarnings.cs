using Microsoft.Extensions.Logging;

using Paduraksa.Server.Configuration;

namespace Paduraksa.Server.Hosting;

/// <summary>What the process says at startup about development switches that are on where they should not be.</summary>
internal static partial class DevelopmentWarnings
{
    /// <summary>Warns when a development switch is on where it should not be: the placeholder certificate in a release build (it is on by default only in a Debug build), and ACME over plain http (never on by default).</summary>
    public static void Check(ILoggerFactory loggers, PaduraksaOptions options)
    {
        if (options.Development.PlaceholderCertificate && !PaduraksaDevelopmentOptions.IsDebugBuild)
        {
            LogPlaceholderEnabled(loggers.CreateLogger(typeof(DevelopmentWarnings).FullName!));
        }

        if (options.Development.AcmeInsecureHttp)
        {
            LogAcmeInsecureHttp(loggers.CreateLogger(typeof(DevelopmentWarnings).FullName!));
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The development placeholder certificate is enabled (Paduraksa:Development:PlaceholderCertificate) in a release build: hosts without a certificate of their own are served with an in-memory self-signed one.")]
    private static partial void LogPlaceholderEnabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ACME over plain http is enabled (Paduraksa:Development:AcmeInsecureHttp): a CA directory on a loopback host (and every URL it hands out there) may use http://, so a local proxy or process can read and alter account requests.")]
    private static partial void LogAcmeInsecureHttp(ILogger logger);
}
