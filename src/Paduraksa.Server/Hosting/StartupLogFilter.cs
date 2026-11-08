using Microsoft.Extensions.Logging;

namespace Paduraksa.Server.Hosting;

/// <summary>
/// Drops the generic host's "start aborted" errors once a start has been aborted on request (stop, signal, cancelled
/// start). Those errors only report the cancellation, which is the requested outcome and which
/// <see cref="PaduraksaHost"/> logs at Information. Real start failures (a port in use, a host stopping itself) are
/// never requests, so they're still logged, by the generic host and at Critical by <see cref="PaduraksaHost"/>.
/// One instance per process, shared by every <see cref="RoleLoggerFactory"/>.
/// <para>
/// It also drops Data Protection's "No XML encryptor configured" warning (event 35): the vault seals every element at rest,
/// so Data Protection's own encryptor is deliberately absent and the warning is a false alarm. The vault logs its own
/// Information line when it seals a new key.
/// </para>
/// </summary>
/// <remarks>
/// <para>Since .NET 10 a <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> runs its
/// <c>ExecuteAsync</c> through <c>Task.Run(..., token)</c> with the token it was started with, so an aborted start can
/// cancel a core background service before its delegate even ran. The host then reports the cancellation twice
/// ("BackgroundService failed", event 9, and "...stopping the host", event 10): its own suppression only applies once
/// <c>ApplicationStopping</c> has fired, which happens later, when the hosts are stopped, not while the start token
/// is cancelled. Only the cancellation is dropped (an <see cref="OperationCanceledException"/>); a background service
/// that really crashes while the start is being aborted still logs.</para>
/// </remarks>
internal sealed class StartupLogFilter
{
    /// <summary>Category of <c>Microsoft.Extensions.Hosting.Internal.Host</c>'s logger.</summary>
    internal const string HostCategory = "Microsoft.Extensions.Hosting.Internal.Host";

    /// <summary>Its <c>HostedServiceStartupFaulted</c> event: "Hosting failed to start".</summary>
    internal const int HostingFailedToStartEventId = 11;

    /// <summary>Its <c>BackgroundServiceFaulted</c> event: "BackgroundService failed".</summary>
    internal const int BackgroundServiceFailedEventId = 9;

    /// <summary>Its <c>BackgroundServiceStoppingHost</c> event: the StopHost behaviour stopping the host for it.</summary>
    internal const int BackgroundServiceStoppingHostEventId = 10;

    /// <summary>Category of <c>Microsoft.AspNetCore.DataProtection.KeyManagement.XmlKeyManager</c>.</summary>
    internal const string XmlKeyManagerCategory = "Microsoft.AspNetCore.DataProtection.KeyManagement.XmlKeyManager";

    /// <summary>Its <c>NoXmlEncryptor</c> event: "No XML encryptor configured. Key ... may be persisted to storage in unencrypted form."</summary>
    internal const int NoXmlEncryptorEventId = 35;

    private volatile bool _startAbortRequested;

    /// <summary>Called by <see cref="PaduraksaHost"/> before it cancels the start.</summary>
    public void StartAbortRequested() => _startAbortRequested = true;

    public bool ShouldDrop(string category, EventId eventId, Exception? exception) =>
        (string.Equals(category, XmlKeyManagerCategory, StringComparison.Ordinal) && eventId.Id == NoXmlEncryptorEventId)
        || (_startAbortRequested
            && string.Equals(category, HostCategory, StringComparison.Ordinal)
            && (eventId.Id == HostingFailedToStartEventId
                || (eventId.Id is BackgroundServiceFailedEventId or BackgroundServiceStoppingHostEventId
                    && exception is OperationCanceledException)));
}
