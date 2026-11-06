using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Paduraksa.Server.Placeholders;

/// <summary>
/// PLACEHOLDER (M1/M2): stands in for the startup recompile (load last-known-good config, compile,
/// publish). Runs in the core host, which starts before the web apps, so the public app never accepts
/// a connection without a snapshot.
/// </summary>
internal sealed partial class PlaceholderSnapshotLoader(SnapshotStore store, ILogger<PlaceholderSnapshotLoader> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (store.Current is null)
        {
            PlaceholderSnapshot snapshot = store.Publish();
            LogPublished(logger, snapshot.Generation);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Published placeholder snapshot (generation {Generation})")]
    private static partial void LogPublished(ILogger logger, long generation);
}
