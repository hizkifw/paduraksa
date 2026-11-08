using Microsoft.Extensions.Logging;

using Paduraksa.Server.Hosting;

namespace Paduraksa.Tests.Integration.Hosting;

/// <summary>
/// The generic host's own "start aborted" errors are dropped during a requested abort (<see cref="StartupLogFilter"/>):
/// "Hosting failed to start" always (it only reports the cancellation), and the .NET 10 background-service entries
/// ("BackgroundService failed" and the StopHost one) for a cancellation only - a service that really crashes while the
/// start is being aborted must still log.
/// </summary>
public sealed class StartupLogFilterTests
{
    private const string Host = StartupLogFilter.HostCategory;
    private static readonly EventId HostingFailed = new(StartupLogFilter.HostingFailedToStartEventId);
    private static readonly EventId BackgroundServiceFailed = new(StartupLogFilter.BackgroundServiceFailedEventId);
    private static readonly EventId BackgroundServiceStoppingHost = new(StartupLogFilter.BackgroundServiceStoppingHostEventId);

    private readonly StartupLogFilter _filter = new();

    [Fact]
    public void NothingIsDroppedBeforeAnAbortIsRequested()
    {
        Assert.False(_filter.ShouldDrop(Host, HostingFailed, new OperationCanceledException()));
        Assert.False(_filter.ShouldDrop(Host, BackgroundServiceFailed, new TaskCanceledException()));
        Assert.False(_filter.ShouldDrop(Host, BackgroundServiceStoppingHost, new OperationCanceledException()));
    }

    [Fact]
    public void AnAbortedStartDropsTheGenericHostsCancelledStartError()
    {
        _filter.StartAbortRequested();

        Assert.True(_filter.ShouldDrop(Host, HostingFailed, new OperationCanceledException("Shutdown was requested during startup.")));
        Assert.True(_filter.ShouldDrop(Host, HostingFailed, exception: null));
    }

    [Fact]
    public void AnAbortedStartDropsTheBackgroundServiceCancellationEntries()
    {
        _filter.StartAbortRequested();

        // A cancelled ExecuteTask (the Task.Run delegate never ran) and an ExecuteAsync that threw the cancellation:
        // both are the abort, not a failure.
        Assert.True(_filter.ShouldDrop(Host, BackgroundServiceFailed, new TaskCanceledException()));
        Assert.True(_filter.ShouldDrop(Host, BackgroundServiceFailed, new OperationCanceledException()));
        Assert.True(_filter.ShouldDrop(Host, BackgroundServiceStoppingHost, new TaskCanceledException()));
        Assert.True(_filter.ShouldDrop(Host, BackgroundServiceStoppingHost, new OperationCanceledException()));
    }

    [Fact]
    public void ABackgroundServiceThatReallyCrashesDuringAnAbortedStartIsStillLogged()
    {
        _filter.StartAbortRequested();

        Assert.False(_filter.ShouldDrop(Host, BackgroundServiceFailed, new InvalidOperationException("Simulated crash.")));
        Assert.False(_filter.ShouldDrop(Host, BackgroundServiceStoppingHost, new InvalidOperationException("Simulated crash.")));
    }

    [Fact]
    public void EverythingElseIsNeverDropped()
    {
        _filter.StartAbortRequested();

        Assert.False(_filter.ShouldDrop("Paduraksa.Application.Commands.ConfigService", BackgroundServiceFailed, new TaskCanceledException()));
        Assert.False(_filter.ShouldDrop(Host, new EventId(12), new OperationCanceledException()));
        Assert.False(_filter.ShouldDrop(Host, new EventId(StartupLogFilter.HostingFailedToStartEventId + 1), new OperationCanceledException()));
    }

    [Fact]
    public void TheDataProtectionNoEncryptorWarningIsAlwaysDropped()
    {
        // Pinned to the framework's event: the vault seals the elements, so Data Protection's own encryptor is absent by design.
        EventId noEncryptor = new(35);
        Assert.True(_filter.ShouldDrop("Microsoft.AspNetCore.DataProtection.KeyManagement.XmlKeyManager", noEncryptor, exception: null));

        // Only that event in that category: other Data Protection failures must still be seen.
        Assert.False(_filter.ShouldDrop("Microsoft.AspNetCore.DataProtection.KeyManagement.XmlKeyManager", new EventId(36), exception: null));
        Assert.False(_filter.ShouldDrop("Paduraksa.Vault.DataProtection.DataProtectionKeyStore", noEncryptor, exception: null));
    }
}
