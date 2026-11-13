using System.Net;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Yarp;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.Yarp;

/// <summary>What <see cref="IUpstreamHealth.ReadAll"/> reports from the live YARP, with real probes against a destination the test switches.</summary>
public sealed class UpstreamHealthReadingTests : IAsyncLifetime
{
    private static readonly ActiveHealthSpec Probe = new() { Path = "/health", Interval = TimeSpan.FromMilliseconds(150), Timeout = TimeSpan.FromSeconds(1) };

    private IHost? _upstream;
    private int _status = 200;
    private string _address = "";

    public async ValueTask InitializeAsync()
    {
        _upstream = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0))
                .Configure(app => app.Run(context =>
                {
                    context.Response.StatusCode = context.Request.Path.Value!.EndsWith("/health", StringComparison.Ordinal) ? Volatile.Read(ref _status) : 200;
                    return Task.CompletedTask;
                })))
            .Build();
        await _upstream.StartAsync(TestContext.Current.CancellationToken);
        var port = new Uri(Assert.Single(_upstream.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses)).Port;
        _address = $"http://127.0.0.1:{port}/";
    }

    public async ValueTask DisposeAsync()
    {
        if (_upstream is not null)
        {
            await _upstream.StopAsync(CancellationToken.None);
            _upstream.Dispose();
        }
    }

    private static int DeadPort()
    {
        using System.Net.Sockets.TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<T> UntilAsync<T>(Func<T?> read, string what)
        where T : class
    {
        var ct = TestContext.Current.CancellationToken;
        var deadline = Environment.TickCount64 + 15_000;
        while (Environment.TickCount64 < deadline)
        {
            if (read() is { } value)
            {
                return value;
            }

            // Poll step of a bounded wait.
            await Task.Delay(50, ct);
        }

        throw new TimeoutException($"Timed out waiting for {what}.");
    }

    [Fact]
    public async Task ReportsEachDestinationsActiveVerdictAndTheLastProbeInWords()
    {
        var dead = $"http://127.0.0.1:{DeadPort()}/";
        ProxyConfigModel model = new()
        {
            Revision = TestIds.Revision("health"),
            Upstreams =
            [
                Upstream("app", _address, dead) with { ActiveHealth = Probe },
                Upstream("plain", _address),
            ],
            Hosts = [ProxyHost("app", "app.test", Route("app-root", "app")), ProxyHost("plain", "plain.test", Route("plain-root", "plain"))],
        };
        await using var server = await ProxyTestServer.StartAsync(model);
        var health = server.Services.GetRequiredService<IUpstreamHealth>();
        var app = TestIds.Upstream("app");
        DestinationReading? Of(string destination) =>
            health.ReadAll().SingleOrDefault(u => u.Id == app)?.Destinations.SingleOrDefault(d => d.Id == TestIds.Destination(destination));

        var working = await UntilAsync(() => Of("app-d0") is { Active: DestinationVerdict.Healthy } d ? d : null, "the first probe to pass");
        var refused = await UntilAsync(() => Of("app-d1") is { Active: DestinationVerdict.Unhealthy } d ? d : null, "two probes to fail");
        Assert.Equal(("connection refused", false), (refused.LastProbe!.Reason, refused.LastProbe.Succeeded));
        Assert.Equal(("HTTP 200", true), (working.LastProbe!.Reason, working.LastProbe.Succeeded));
        Assert.Equal(DestinationVerdict.Unhealthy, refused.Current);
        Assert.False(health.ReadAll().Single(u => u.Id == app).NoneAvailable);

        // The one working destination goes down: the upstream has nothing left, with the status as the reason.
        Volatile.Write(ref _status, 503);
        var down = await UntilAsync(() => Of("app-d0") is { Active: DestinationVerdict.Unhealthy } d ? d : null, "the working destination to fail its probes");
        Assert.Equal("HTTP 503", down.LastProbe!.Reason);
        Assert.True(health.ReadAll().Single(u => u.Id == app).NoneAvailable);

        // YARP's available list falls back to every destination when none is healthy, so Lookup cannot tell an upstream is down.
        var routable = health.Lookup(app)!;
        Assert.Equal((2, 0), (routable.Healthy.Count, routable.Unhealthy.Count));

        Volatile.Write(ref _status, 200);
        await UntilAsync(() => Of("app-d0") is { Active: DestinationVerdict.Healthy } d ? d : null, "the destination to pass again");
        Assert.False(health.ReadAll().Single(u => u.Id == app).NoneAvailable);

        // No check configured: nothing to be down about.
        var plain = health.ReadAll().Single(u => u.Id == TestIds.Upstream("plain"));
        Assert.False(plain.HasChecks);
        Assert.Equal(DestinationVerdict.Unknown, Assert.Single(plain.Destinations).Current);
        Assert.False(plain.NoneAvailable);
    }

    [Fact]
    public void ProbeReasonsAreWordsAndNeverTheExceptionMessage()
    {
        using HttpResponseMessage unavailable = new(HttpStatusCode.ServiceUnavailable);
        using HttpResponseMessage redirect = new(HttpStatusCode.Found);
        Assert.Equal((false, "HTTP 503"), ProbeLog.Describe(unavailable, null));
        Assert.Equal((true, "HTTP 302"), ProbeLog.Describe(redirect, null));
        Assert.Equal((false, "timed out"), ProbeLog.Describe(null, new TaskCanceledException("secret path /x")));
        Assert.Equal((false, "connection refused"), ProbeLog.Describe(null, new HttpRequestException("secret", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused))));
        Assert.Equal((false, "probe failed"), ProbeLog.Describe(null, new InvalidOperationException("secret")));
        Assert.Equal((false, "no answer"), ProbeLog.Describe(null, null));
    }
}
