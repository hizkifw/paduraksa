using Paduraksa.Application.Commands;
using Paduraksa.Application.Settings;
using Paduraksa.Server.Configuration;

namespace Paduraksa.Server.Hosting;

/// <summary>
/// The startup recompile, in the core host, which starts before the web apps: creates the data directory and runs
/// <see cref="ConfigService.StartAsync"/> (migrate, load the latest revision or fall back to the last-known-good config,
/// compile, queue the publish).
/// <para><b>Never awaits YARP.</b> A config with routes can only go live once the public app attaches YARP, and the public
/// app starts after this host's start has returned, so awaiting it here would deadlock startup. The publish is queued and
/// this returns; it completes while the public app starts, before its listeners accept connections, and <c>/readyz</c>
/// reports it. An empty config needs no YARP and is live at once. With nothing to serve at all (neither the database nor the
/// last-known-good file usable) the start fails and the process exits with <see cref="ExitCodes.StartupFailed"/>.</para>
/// </summary>
internal sealed partial class ConfigStartup(ConfigService config, ServerSettingsState serverSettings, PaduraksaOptions options, ILogger<ConfigStartup> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(options.DataDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Opening the database fails next and says what that means; this says why.
            LogDataDirectoryFailed(logger, options.DataDirectory, ex);
        }

        await config.StartAsync(cancellationToken).ConfigureAwait(false);

        // The startup snapshot is queued, so its server settings are in force: what the listeners bind with next is what they run with.
        serverSettings.Seal();
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Error, Message = "Couldn't create the data directory {DataDirectory}")]
    private static partial void LogDataDirectoryFailed(ILogger logger, string dataDirectory, Exception exception);
}
