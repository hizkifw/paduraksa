using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;

using Microsoft.Extensions.DependencyInjection;

using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Yarp;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;

using Yarp.ReverseProxy;
using Yarp.ReverseProxy.Model;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.EndToEnd;

/// <summary>
/// Spike S6, YARP-health part: under steady load, with a publish every 100 ms, (a) no request fails and
/// (b) YARP destination health survives every publish that doesn't touch its cluster: a destination marked unhealthy
/// actively (probe) or passively (transport failures) stays unhealthy, and no healthy destination flaps. Rate-limit and
/// metric continuity are not covered yet: neither a rate-limit table nor metric slots exist to follow.
/// </summary>
public sealed class StateContinuityTests(TestUpstream upstream) : IClassFixture<TestUpstream>
{
    private static readonly TimeSpan LoadDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PublishEvery = TimeSpan.FromMilliseconds(100);
    private static readonly ActiveHealthSpec Probe = new() { Path = "/health", Interval = TimeSpan.FromMilliseconds(250), Timeout = TimeSpan.FromSeconds(1) };

    /// <summary>The destinations whose health must not change during the load, with the verdict they must keep.</summary>
    private static readonly (string Cluster, string Destination, Expected Health)[] Tracked =
    [
        ("stable", "stable-d0", Expected.ActiveHealthy),
        ("stable", "stable-d1", Expected.ActiveHealthy),
        ("probed", "probed-d0", Expected.ActiveHealthy),
        ("probed", "probed-d1", Expected.ActiveUnhealthy),
        ("flaky", "flaky-d0", Expected.PassiveNotUnhealthy),
        ("flaky", "flaky-d1", Expected.PassiveUnhealthy),
    ];

    private enum Expected
    {
        ActiveHealthy,
        ActiveUnhealthy,
        PassiveUnhealthy,
        PassiveNotUnhealthy,
    }

    private static readonly AccessListSpec Loopback = TestAccessLists.Allow("loopback", "127.0.0.0/8");

    private ProxyConfigModel Model(int variant) => new()
    {
        Revision = TestIds.Revision("s6-" + variant.ToString(CultureInfo.InvariantCulture)),
        Upstreams =
        [
            Upstream("stable", upstream.Address("s0"), upstream.Address("s1")) with { ActiveHealth = Probe, PassiveHealth = true },
            // d1's probe hits /bad/health, which answers 500: actively unhealthy.
            Upstream("probed", upstream.Address("p0"), upstream.Address("bad")) with { ActiveHealth = Probe },
            // d1 refuses connections: passively unhealthy, kept out for the whole test.
            Upstream("flaky", upstream.Address("f0"), $"http://127.0.0.1:{upstream.DeadPort}/") with { PassiveHealth = true, ReactivationPeriod = TimeSpan.FromMinutes(10) },
            .. variant % 3 == 1 ? [Upstream("churn", upstream.Address("c0"))] : (UpstreamSpec[])[],
        ],
        AccessLists = [Loopback],
        Hosts =
        [
            // Variant 2 changes only policies (a YARP no-op); variant 1 adds a host, a route and a cluster (a real
            // YARP reload); variant 0 removes them again.
            ProxyHost("stable", "stable.test", Route("stable-root", "stable")) with { Policies = variant % 3 == 2 ? [Loopback.Policy()] : [] },
            ProxyHost("probed", "probed.test", Route("probed-root", "probed")),
            ProxyHost("flaky", "flaky.test", Route("flaky-root", "flaky")),
            .. variant % 3 == 1 ? [ProxyHost("churn", "churn.test", Route("churn-root", "churn"))] : (HostSpec[])[],
        ],
    };

    [Fact]
    public async Task YarpDestinationHealthSurvivesPublishesUnderLoad()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await ProxyTestServer.StartAsync(Model(0));
        var yarp = server.Services.GetRequiredService<IProxyStateLookup>();
        using var client = ProxyTestServer.CreateClient(maxConnections: 8);

        // Set up the verdicts: probes mark probed-d1 unhealthy; failed requests mark flaky-d1 unhealthy.
        await WaitUntilAsync(() => Health(yarp, "stable", "stable-d0").Active == DestinationHealth.Healthy
            && Health(yarp, "stable", "stable-d1").Active == DestinationHealth.Healthy
            && Health(yarp, "probed", "probed-d0").Active == DestinationHealth.Healthy
            && Health(yarp, "probed", "probed-d1").Active == DestinationHealth.Unhealthy, "active probes settle", ct);
        for (var i = 0; i < 200 && Health(yarp, "flaky", "flaky-d1").Passive != DestinationHealth.Unhealthy; i++)
        {
            using var warmup = await client.SendAsync(server.Request("flaky.test", "/warmup"), ct);
        }

        Assert.Equal(DestinationHealth.Unhealthy, Health(yarp, "flaky", "flaky-d1").Passive);
        foreach (var (cluster, destination, expected) in Tracked)
        {
            Assert.True(Holds(Health(yarp, cluster, destination), expected), $"{destination} before the load: {Describe(Health(yarp, cluster, destination))}");
        }

        // Load + publishes + a health monitor, all for LoadDuration.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ConcurrentQueue<string> failures = new();
        ConcurrentQueue<string> flaps = new();
        long requests = 0;
        var published = 0;
        var generationBefore = server.Store.Current.Generation;

        var load = new Task[8];
        string[] hosts = ["stable.test", "probed.test", "flaky.test"];
        for (var w = 0; w < load.Length; w++)
        {
            var host = hosts[w % hosts.Length];
            load[w] = Task.Run(
                async () =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        try
                        {
                            using var response = await client.SendAsync(server.Request(host, "/load"), ct);
                            await response.Content.LoadIntoBufferAsync(ct);
                            if (response.StatusCode != HttpStatusCode.OK)
                            {
                                failures.Enqueue($"{host}: {(int)response.StatusCode}");
                            }
                        }
                        catch (HttpRequestException ex)
                        {
                            failures.Enqueue($"{host}: {ex.Message}");
                        }

                        Interlocked.Increment(ref requests);
                    }
                },
                ct);
        }

        var publisher = Task.Run(
            async () =>
            {
                var variant = 0;
                while (!stop.IsCancellationRequested)
                {
                    var started = Stopwatch.StartNew();
                    var result = await server.PublishAsync(Model(++variant));
                    if (!result.Succeeded)
                    {
                        failures.Enqueue($"publish {variant}: {result.Status} {string.Join("; ", result.Errors)}");
                    }
                    else
                    {
                        published++;
                    }

                    var rest = PublishEvery - started.Elapsed;
                    if (rest > TimeSpan.Zero && !stop.IsCancellationRequested)
                    {
                        await Task.Delay(rest, CancellationToken.None);
                    }
                }
            },
            ct);

        var monitor = Task.Run(
            async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    foreach (var (cluster, destination, expected) in Tracked)
                    {
                        var health = Health(yarp, cluster, destination);
                        if (!Holds(health, expected))
                        {
                            flaps.Enqueue($"{destination} (generation {server.Store.Current.Generation}): {Describe(health)}, expected {expected}");
                        }
                    }

                    await Task.Delay(5, CancellationToken.None);
                }
            },
            ct);

        await Task.Delay(LoadDuration, ct);
        await stop.CancelAsync();
        await Task.WhenAll([.. load, publisher, monitor]);

        Assert.Empty(failures);
        Assert.Empty(flaps);
        Assert.True(published >= 15, $"only {published} publishes in {LoadDuration}");
        Assert.True(server.Store.Current.Generation - generationBefore == published);
        Assert.True(requests >= 300, $"only {requests} requests");
    }

    private static DestinationHealthState Health(IProxyStateLookup yarp, string cluster, string destination)
    {
        Assert.True(yarp.TryGetCluster(TestIds.Upstream(cluster).ToString(), out var state), $"cluster {cluster} is gone");
        Assert.True(state.Destinations.TryGetValue(TestIds.Destination(destination).ToString(), out var found), $"destination {destination} is gone");
        return found.Health;
    }

    private static bool Holds(DestinationHealthState health, Expected expected) => expected switch
    {
        Expected.ActiveHealthy => health.Active == DestinationHealth.Healthy && health.Passive != DestinationHealth.Unhealthy,
        Expected.ActiveUnhealthy => health.Active == DestinationHealth.Unhealthy,
        Expected.PassiveUnhealthy => health.Passive == DestinationHealth.Unhealthy,
        _ => health.Passive != DestinationHealth.Unhealthy,
    };

    private static string Describe(DestinationHealthState health) => $"active {health.Active}, passive {health.Passive}";

    private static async Task WaitUntilAsync(Func<bool> condition, string what, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting until {what}");
            await Task.Delay(25, ct);
        }
    }
}
