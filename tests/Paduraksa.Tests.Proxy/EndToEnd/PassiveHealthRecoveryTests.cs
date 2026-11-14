using System.Diagnostics;
using System.Globalization;
using System.Net;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Paduraksa.Proxy.Config;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;

using Yarp.ReverseProxy;
using Yarp.ReverseProxy.Model;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.EndToEnd;

/// <summary>
/// A destination that fails and comes back, through the real proxy (Kestrel, YARP's health monitor and updater): the soak's flap
/// (bench/soak/README.md), which stayed out about a minute after it recovered. The upstream has a steady destination and one the test
/// switches to aborting every request, probes included.
/// </summary>
public sealed class PassiveHealthRecoveryTests : IAsyncLifetime
{
    private static readonly ActiveHealthSpec Probe = new() { Path = "/health", Interval = TimeSpan.FromMilliseconds(250), Timeout = TimeSpan.FromSeconds(1) };
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IHost _upstream = null!;
    private int _port;
    private volatile bool _down;

    public async ValueTask InitializeAsync()
    {
        _upstream = new HostBuilder()
            .ConfigureLogging(static logging => logging.ClearProviders())
            .ConfigureWebHost(web => web
                .UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0))
                .Configure(app => app.Run(context =>
                {
                    var path = context.Request.Path.Value!;
                    if (_down && path.StartsWith("/flap/", StringComparison.Ordinal))
                    {
                        context.Abort();
                        return Task.CompletedTask;
                    }

                    return context.Response.WriteAsync(path);
                })))
            .Build();
        await _upstream.StartAsync(Ct);
        _port = new Uri(_upstream.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()).Port;
    }

    public async ValueTask DisposeAsync()
    {
        await _upstream.StopAsync(CancellationToken.None);
        _upstream.Dispose();
    }

    private ProxyConfigModel Model(TimeSpan reactivation, ActiveHealthSpec? active) => new()
    {
        Upstreams =
        [
            Upstream("up", Address("steady"), Address("flap")) with { PassiveHealth = true, ReactivationPeriod = reactivation, ActiveHealth = active },
        ],
        Hosts = [ProxyHost("app", "app.test", Route("app-root", "up"))],
    };

    private string Address(string prefix) => string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{_port}/{prefix}/");

    [Fact]
    public async Task APassivelyEjectedDestinationIsBackAfterTheUpstreamsReactivationPeriod()
    {
        await using var server = await ProxyTestServer.StartAsync(Model(TimeSpan.FromSeconds(2), active: null));
        using var client = ProxyTestServer.CreateClient();
        var yarp = server.Services.GetRequiredService<IProxyStateLookup>();

        _down = true;
        await DriveUntilAsync(server, client, () => Flap(yarp).Passive == DestinationHealth.Unhealthy, "the flap destination is taken out");
        var ejected = Stopwatch.StartNew();
        _down = false;

        // YARP's own policy kept it out for its 60 s detection window whatever the period said.
        await WaitUntilAsync(() => Flap(yarp).Passive != DestinationHealth.Unhealthy, TimeSpan.FromSeconds(10), "the reactivation");
        Assert.InRange(ejected.Elapsed, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(6));
        await DriveUntilFlapServesAsync(server, client, TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task AFlapFollowedByRecoveryIsBackInRotationWithinSeconds()
    {
        // A passive verdict that would last ten minutes: only the active check seeing it back can explain a quick return.
        await using var server = await ProxyTestServer.StartAsync(Model(TimeSpan.FromMinutes(10), Probe));
        using var client = ProxyTestServer.CreateClient();
        var yarp = server.Services.GetRequiredService<IProxyStateLookup>();
        await WaitUntilAsync(() => Flap(yarp).Active == DestinationHealth.Healthy, TimeSpan.FromSeconds(10), "the first probes");

        _down = true;
        await DriveUntilAsync(
            server,
            client,
            () => Flap(yarp) is { Active: DestinationHealth.Unhealthy, Passive: DestinationHealth.Unhealthy },
            "both checks have the flap destination down");

        _down = false;
        var recovered = Stopwatch.StartNew();
        await DriveUntilFlapServesAsync(server, client, TimeSpan.FromSeconds(5));
        Assert.True(recovered.Elapsed < TimeSpan.FromSeconds(5), $"back after {recovered.Elapsed}");

        // And it stays in: what failed during the outage doesn't take it out again on its first requests back.
        for (var i = 0; i < 20; i++)
        {
            using var response = await client.SendAsync(server.Request("app.test", "/after"), Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.NotEqual(DestinationHealth.Unhealthy, Flap(yarp).Passive);
    }

    private static DestinationHealthState Flap(IProxyStateLookup yarp)
    {
        Assert.True(yarp.TryGetCluster(TestIds.Upstream("up").ToString(), out var cluster));
        Assert.True(cluster.Destinations.TryGetValue(TestIds.Destination("up-d1").ToString(), out var destination));
        return destination.Health;
    }

    private static async Task DriveUntilAsync(ProxyTestServer server, HttpClient client, Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting until {what}");
            using var response = await client.SendAsync(server.Request("app.test", "/drive"), Ct);
            // Paces the traffic so the probes run between requests.
            await Task.Delay(10, Ct);
        }
    }

    /// <summary>Sends requests until one is answered by the flap destination.</summary>
    private static async Task DriveUntilFlapServesAsync(ProxyTestServer server, HttpClient client, TimeSpan limit)
    {
        var deadline = DateTime.UtcNow + limit;
        while (true)
        {
            using var response = await client.SendAsync(server.Request("app.test", "/back"), Ct);
            if (response.StatusCode == HttpStatusCode.OK && (await response.Content.ReadAsStringAsync(Ct)).StartsWith("/flap/", StringComparison.Ordinal))
            {
                return;
            }

            Assert.True(DateTime.UtcNow < deadline, $"the flap destination served nothing within {limit}");
            await Task.Delay(10, Ct);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan limit, string what)
    {
        var deadline = DateTime.UtcNow + limit;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for {what}");
            // Poll step of a bounded wait.
            await Task.Delay(25, Ct);
        }
    }
}
