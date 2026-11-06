using Microsoft.Extensions.Logging;

namespace Paduraksa.Server.Hosting;

/// <summary>
/// Drops the generic host's "Hosting failed to start" error once a start has been aborted on request (stop, signal,
/// cancelled start). That error only reports the cancellation, which is the requested outcome and which
/// <see cref="PaduraksaHost"/> logs at Information. Real start failures (a port in use, a host stopping itself) are
/// never requests, so they're still logged, by the generic host and at Critical by <see cref="PaduraksaHost"/>.
/// One instance per process, shared by every <see cref="RoleLoggerFactory"/>.
/// </summary>
internal sealed class StartupLogFilter
{
    /// <summary>Category of <c>Microsoft.Extensions.Hosting.Internal.Host</c>'s logger.</summary>
    internal const string HostCategory = "Microsoft.Extensions.Hosting.Internal.Host";

    /// <summary>Its <c>HostedServiceStartupFaulted</c> event: "Hosting failed to start".</summary>
    internal const int HostingFailedToStartEventId = 11;

    private volatile bool _startAbortRequested;

    /// <summary>Called by <see cref="PaduraksaHost"/> before it cancels the start.</summary>
    public void StartAbortRequested() => _startAbortRequested = true;

    public bool ShouldDrop(string category, EventId eventId) =>
        _startAbortRequested
        && eventId.Id == HostingFailedToStartEventId
        && string.Equals(category, HostCategory, StringComparison.Ordinal);
}
