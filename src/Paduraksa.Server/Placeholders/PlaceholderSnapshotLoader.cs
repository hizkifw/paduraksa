using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Yarp;
using Paduraksa.Server.Hosting;

namespace Paduraksa.Server.Placeholders;

/// <summary>PLACEHOLDER (M2): the config the startup publish compiles. Absent = the empty config.</summary>
internal sealed record StartupProxyConfig(ProxyConfigModel Model);

/// <summary>
/// PLACEHOLDER (M2): stands in for the startup recompile (load the latest revision or last-known-good, compile,
/// publish). M1 publishes the <b>empty</b> config unless a <see cref="StartupProxyConfig"/> is registered (tests).
/// Runs in the core host, which starts before the web apps.
/// <para>Until M3, a config that names no default certificate gets the placeholder one, so HTTPS answers for every
/// host (as it did before per-SNI selection).</para>
/// <para><b>Never awaits YARP.</b> A config with routes can only go live once the public app attaches YARP, and the
/// public app starts after this host's start has returned, so awaiting it here would deadlock startup. The publish is
/// queued and this returns; it completes while the public app starts (before its listeners accept connections), and
/// <c>/readyz</c> (<see cref="SnapshotStore.HasPublished"/>) reports it. An empty config needs no YARP and is live at
/// once.</para>
/// </summary>
internal sealed partial class PlaceholderSnapshotLoader(
    SnapshotPublisher publisher,
    PublicListenerSetup listeners,
    ILogger<PlaceholderSnapshotLoader> logger,
    StartupProxyConfig? startupConfig = null) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (publisher.Store.HasPublished)
        {
            return Task.CompletedTask;
        }

        ProxyConfigModel model = startupConfig?.Model ?? ProxyConfigModel.Empty;
        DefaultsSpec defaults = model.Defaults ?? new DefaultsSpec();
        if (defaults.Certificate is null)
        {
            model = model with { Defaults = defaults with { Certificate = PlaceholderCertificates.Reference } };
        }

        CompileResult compiled = SnapshotCompiler.Compile(model, listeners.CompilerOptions);
        RuntimeSnapshot candidate = compiled.Snapshot
            ?? throw new InvalidOperationException("The startup config failed to compile: " + string.Join("; ", compiled.Errors));

        // Deliberately not awaited (see the class remarks); the outcome is logged when it arrives.
        _ = LogOutcomeAsync(publisher.PublishAsync(candidate, CancellationToken.None), model.Revision);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task LogOutcomeAsync(Task<PublishResult> publish, string revision)
    {
        PublishResult result = await publish.ConfigureAwait(false);
        if (result.Succeeded)
        {
            LogPublished(logger, revision, result.Generation);
        }
        else
        {
            string errors = string.Join("; ", result.Errors);
            LogFailed(logger, revision, result.Status, errors);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Published the startup snapshot (revision {Revision}, generation {Generation})")]
    private static partial void LogPublished(ILogger logger, string revision, long generation);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Publishing the startup snapshot (revision {Revision}) failed ({Status}): {Errors}")]
    private static partial void LogFailed(ILogger logger, string revision, PublishStatus status, string errors);
}
