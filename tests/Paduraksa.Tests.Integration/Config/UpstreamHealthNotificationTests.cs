using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Issuance;
using Paduraksa.Contracts.Notifications;
using Paduraksa.Core.Model;
using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Admin;
using Paduraksa.Tests.Integration.Hosting;
using Paduraksa.Tests.Shared;
using static Paduraksa.Tests.Integration.Config.ConfigTestSupport;

namespace Paduraksa.Tests.Integration.Config;

/// <summary>
/// The whole path on a real host: a real YARP active probe against a listener the test switches, the health watcher, the audit log, the
/// dispatcher and a fake webhook. A destination going down and coming back is exactly one audit row and one notification each, and a config publish
/// that keeps the IDs (which keeps YARP's state) reports nothing.
/// </summary>
public sealed class UpstreamHealthNotificationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<long> CountAsync(string dataDirectory, string action) =>
        (long)(await ScalarAsync(dataDirectory, $"SELECT count(*) FROM AuditEvents WHERE Action = '{action}'", Ct))!;

    private static async Task UntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = Environment.TickCount64 + 40_000;
        while (Environment.TickCount64 < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(100, Ct);
        }

        throw new TimeoutException($"Timed out waiting for {what}.");
    }

    [Fact]
    public async Task ARealProbeFlippingADestinationDownAndUpIsOneAuditRowAndOneNotificationEachAndAPublishIsSilent()
    {
        var dataDirectory = TestDataDirectories.NewPath();
        await using var app = await SwitchableUpstream.StartAsync();
        await using var hook = await RecordingHook.StartAsync();
        await using var host = TestHost.Create(
            dataDirectory: dataDirectory,
            settings: new Dictionary<string, string?>
            {
                ["Paduraksa:Notifications:HealthHold"] = "00:00:01",
                ["Paduraksa:Notifications:CoalesceWindow"] = "00:00:00.2",
            });
        await host.StartAsync(Ct);
        var channel = await host.Shared.Notifications.CreateAsync(
            new NotificationChannelBody(
                NotificationChannelKind.Webhook, "ops", [NotificationEventKind.UpstreamDown, NotificationEventKind.UpstreamUp, NotificationEventKind.DestinationDown, NotificationEventKind.DestinationUp],
                Secret: hook.Url + "/hook"),
            Origin,
            Ct);
        Assert.Equal(IssuanceChangeStatus.Committed, channel.Status);
        var upstream = new UpstreamConfig(
            TestIds.Upstream("app"), "media", Destinations: [new DestinationConfig(TestIds.Destination("app-1"), app.Address)],
            ActiveHealth: new ActiveHealthConfig("/health", TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(2)));
        await CommitAsync(host, new UpsertUpstreamCommand(upstream), Ct);
        await CommitAsync(host, new UpsertHostCommand(ProxyHost("app", "app.test", "app")), Ct);
        await UntilAsync(() => Task.FromResult(host.Shared.HealthBoard.Find(TestIds.Upstream("app"))?.State == UpstreamHealthState.Healthy), "the first probe to pass");

        // Publishes that keep the IDs, while it is healthy and settled: nothing is reported.
        await CommitAsync(host, new UpsertHostCommand(ProxyHost("other", "other.test", "app")), Ct);
        await CommitAsync(host, new UpsertHostCommand(RespondHost("page", "page.test", "hi")), Ct);
        await Task.Delay(3500, Ct);
        Assert.Equal(0, await CountAsync(dataDirectory, "upstream.down") + await CountAsync(dataDirectory, "upstream.up"));
        Assert.Empty(hook.Received);

        app.Status = 503;
        await UntilAsync(async () => await CountAsync(dataDirectory, "upstream.down") == 1 && hook.Received.Count == 1, "the upstream to be reported down");
        app.Status = 200;
        await UntilAsync(async () => await CountAsync(dataDirectory, "upstream.up") == 1 && hook.Received.Count == 2, "the upstream to be reported up");

        // Another publish after the flips: still nothing new, and the single destination's own changes were absorbed into the upstream's.
        await CommitAsync(host, new UpsertHostCommand(RespondHost("page2", "page2.test", "hello again")), Ct);
        await Task.Delay(3500, Ct);
        var rows = new List<long>();
        foreach (var action in new[] { "upstream.down", "upstream.up", "upstream.destination.down", "upstream.destination.up" })
        {
            rows.Add(await CountAsync(dataDirectory, action));
        }

        Assert.Equal([1L, 1L, 0L, 0L], rows);
        Assert.Equal(2, hook.Received.Count);

        var down = JsonDocument.Parse(hook.Received[0].Body).RootElement;
        Assert.Equal(("upstream.down", "critical", "Upstream media is down"), (down.GetProperty("event").GetString(), down.GetProperty("severity").GetString(), down.GetProperty("title").GetString()));
        Assert.Contains("HTTP 503", down.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(TestIds.Upstream("app").ToString(), down.GetProperty("data").GetProperty("upstreamId").GetString());
        var up = JsonDocument.Parse(hook.Received[1].Body).RootElement;
        Assert.Equal(("upstream.up", "good", "Upstream media is back up"), (up.GetProperty("event").GetString(), up.GetProperty("severity").GetString(), up.GetProperty("title").GetString()));
        Assert.Equal(UpstreamHealthState.Healthy, host.Shared.HealthBoard.Find(TestIds.Upstream("app"))!.State);
        Assert.Equal("Back up", host.Shared.HealthBoard.Find(TestIds.Upstream("app"))!.LastChange!.Summary);
        var summary = (await host.Shared.Notifications.GetAsync(channel.Channel!.Id, Ct))!;
        Assert.NotNull(summary.LastSentAt);
    }

    /// <summary>A loopback server whose <c>/health</c> answers the status the test sets.</summary>
    private sealed class SwitchableUpstream : IAsyncDisposable
    {
        private readonly IHost _host;
        private int _status = 200;

        private SwitchableUpstream(IHost host, string address)
        {
            _host = host;
            Address = address;
        }

        public string Address { get; }

        public int Status
        {
            set => Volatile.Write(ref _status, value);
        }

        public static async Task<SwitchableUpstream> StartAsync()
        {
            SwitchableUpstream? upstream = null;
            var host = new HostBuilder()
                .ConfigureWebHost(web => web
                    .UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0))
                    .Configure(app => app.Run(context =>
                    {
                        context.Response.StatusCode = context.Request.Path.Value!.EndsWith("/health", StringComparison.Ordinal) ? Volatile.Read(ref upstream!._status) : 200;
                        return Task.CompletedTask;
                    })))
                .Build();
            await host.StartAsync(Ct);
            var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            upstream = new SwitchableUpstream(host, address.TrimEnd('/') + "/");
            return upstream;
        }

        public async ValueTask DisposeAsync()
        {
            await _host.StopAsync(CancellationToken.None);
            _host.Dispose();
        }
    }
}
