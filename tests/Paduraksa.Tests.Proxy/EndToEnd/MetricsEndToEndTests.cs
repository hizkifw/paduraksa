using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Metrics;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.EndToEnd;

/// <summary>
/// The request counters on a real Kestrel and YARP: what each kind of answer counts as (status class, gate outcome, bytes,
/// the upstream and destination counters), the access-log switch not mattering, and (spike S6, metric part) the counters staying
/// monotonic, and on the same slots, while a publish every 100 ms replaces the config under load.
/// </summary>
public sealed class MetricsEndToEndTests(TestUpstream upstream) : IClassFixture<TestUpstream>
{
    private static readonly TimeSpan PublishEvery = TimeSpan.FromMilliseconds(100);

    private ProxyConfigModel Model(int variant = 0, string? deadAddress = null) => new()
    {
        Revision = TestIds.Revision("metrics-" + variant),
        Upstreams =
        [
            Upstream("web", upstream.Address("a")),
            Upstream("dead", deadAddress ?? $"http://127.0.0.1:{upstream.DeadPort}/"),
            .. variant % 2 == 1 ? [Upstream("extra", upstream.Address("x"))] : (UpstreamSpec[])[],
        ],
        Hosts =
        [
            ProxyHost("app", "app.test", Route("app-blocked", "web", "/blocked") with { Policies = [new DenyPolicySpec()] }, Route("app-down", "dead", "/down"), Route("app-api", "web", "/api")),
            ProxyHost("quiet", "quiet.test", Route("quiet-root", "web")) with { AccessLog = false },
            .. variant % 2 == 1 ? [ProxyHost("extra", "extra.test", Route("extra-root", "extra"))] : (HostSpec[])[],
        ],
    };

    private static long[] Counters(MetricsRegistry registry, MetricSubject subject, MetricSlot slot)
    {
        var totals = new long[32];
        registry.ReadCounters(subject, slot.Index, totals);
        return totals;
    }

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition() && timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), what);
    }

    [Fact]
    public async Task EachKindOfAnswerCountsAsItsStatusClassAndGateOutcome()
    {
        var ct = TestContext.Current.CancellationToken;
        MetricsRegistry registry = new(processors: 2);
        await using var server = await ProxyTestServer.StartAsync(Model(), new() { Metrics = registry });
        using var client = ProxyTestServer.CreateClient(maxConnections: 2);
        var snapshot = server.Store.Current;
        var app = snapshot.Hosts.Find("app.test", out _)!;

        async Task<HttpStatusCode> GetAsync(string host, string path)
        {
            using var response = await client.SendAsync(server.Request(host, path), ct);
            await response.Content.LoadIntoBufferAsync(ct);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await GetAsync("app.test", "/api/x"));
        Assert.Equal(HttpStatusCode.OK, await GetAsync("app.test", "/api/y"));
        Assert.Equal(HttpStatusCode.Forbidden, await GetAsync("app.test", "/blocked"));
        Assert.Equal(HttpStatusCode.NotFound, await GetAsync("app.test", "/nowhere"));
        Assert.Equal(HttpStatusCode.NotFound, await GetAsync("unknown.test", "/"));
        Assert.Equal(HttpStatusCode.BadGateway, await GetAsync("app.test", "/down"));

        await WaitForAsync(() => Counters(registry, MetricSubject.Host, app.MetricSlot).Take(RequestMetric.StatusClasses).Sum() >= 5, "the host's requests are counted");
        var host = Counters(registry, MetricSubject.Host, app.MetricSlot);
        Assert.Equal(2, host[RequestMetric.Status + 1]);
        Assert.Equal(2, host[RequestMetric.Status + 3]);
        Assert.Equal(1, host[RequestMetric.Status + 4]);
        Assert.Equal(3, host[RequestMetric.Outcome + (int)GateOutcome.Forwarded]);
        Assert.Equal(1, host[RequestMetric.Outcome + (int)GateOutcome.RefusedOther]);
        Assert.Equal(1, host[RequestMetric.Outcome + (int)GateOutcome.NoRoute]);
        Assert.True(host[RequestMetric.BytesOut] > 0, "the bodies written are counted");
        Assert.True(host[RequestMetric.TotalMicros] > 0);

        await WaitForAsync(() => Counters(registry, MetricSubject.Host, MetricsRegistry.UnmatchedHost)[RequestMetric.Status + 3] >= 1, "the unmatched host's request is counted");
        var unmatched = Counters(registry, MetricSubject.Host, MetricsRegistry.UnmatchedHost);
        Assert.Equal(1, unmatched[RequestMetric.Outcome + (int)GateOutcome.UnknownHost]);

        var api = app.Routes.Single(r => r.Id == TestIds.Route("app-api"));
        var apiCounters = Counters(registry, MetricSubject.Route, api.MetricSlot);
        Assert.Equal(2, apiCounters[RequestMetric.Status + 1]);
        Assert.Equal(2, apiCounters[RequestMetric.Outcome + (int)GateOutcome.Forwarded]);

        var web = Counters(registry, MetricSubject.Upstream, api.Upstream!.Slot);
        Assert.Equal(2, web[UpstreamMetric.Requests]);
        Assert.True(web[UpstreamMetric.UpstreamMicros] > 0);
        var destination = Counters(registry, MetricSubject.Destination, api.Upstream.DestinationSlot(TestIds.Destination("web-d0").ToString()));
        Assert.Equal(2, destination[DestinationMetric.Requests]);
        Assert.Equal(0, destination[DestinationMetric.Errors]);

        var down = app.Routes.Single(r => r.Id == TestIds.Route("app-down"));
        var dead = Counters(registry, MetricSubject.Upstream, down.Upstream!.Slot);
        Assert.Equal(1, dead[UpstreamMetric.Requests]);
        Assert.Equal(1, dead.Skip(UpstreamMetric.Errors).Take(UpstreamMetric.ErrorKinds).Sum());
        var deadDestination = Counters(registry, MetricSubject.Destination, down.Upstream.DestinationSlot(TestIds.Destination("dead-d0").ToString()));
        Assert.Equal(1, deadDestination[DestinationMetric.Errors]);
    }

    [Fact]
    public async Task AHostThatDoesNotLogItsRequestsStillCountsThem()
    {
        var ct = TestContext.Current.CancellationToken;
        MetricsRegistry registry = new(processors: 2);
        await using var server = await ProxyTestServer.StartAsync(Model(), new() { Metrics = registry });
        using var client = ProxyTestServer.CreateClient(maxConnections: 1);
        var quiet = server.Store.Current.Hosts.Find("quiet.test", out _)!;

        for (var i = 0; i < 3; i++)
        {
            using var response = await client.SendAsync(server.Request("quiet.test", "/x"), ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await response.Content.LoadIntoBufferAsync(ct);
        }

        await WaitForAsync(() => Counters(registry, MetricSubject.Host, quiet.MetricSlot)[RequestMetric.Status + 1] >= 3, "the three requests are counted");
        Assert.Equal(0, server.AccessLog.EntriesWritten);
        var latency = new long[LatencyBuckets.Stride];
        registry.ReadLatency(MetricSubject.Host, quiet.MetricSlot.Index, latency, new long[LatencyBuckets.Stride]);
        Assert.Equal(3, latency.Sum());
    }

    [Fact]
    public async Task CountersStayMonotonicAndOnTheSameSlotsWhileAPublishEvery100MsReplacesTheConfigUnderLoad()
    {
        var ct = TestContext.Current.CancellationToken;
        MetricsRegistry registry = new(processors: 2);
        await using var server = await ProxyTestServer.StartAsync(Model(0), new() { Metrics = registry });
        using var client = ProxyTestServer.CreateClient(maxConnections: 4);
        var app = server.Store.Current.Hosts.Find("app.test", out _)!;
        var appSlot = app.MetricSlot;
        var apiSlot = app.Routes.Single(r => r.Id == TestIds.Route("app-api")).MetricSlot;
        var upstreamSlot = app.Routes.Single(r => r.Id == TestIds.Route("app-api")).Upstream!.Slot;

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        long sent = 0;
        var load = Enumerable.Range(0, 4).Select(_ => Task.Run(
            async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    using var response = await client.SendAsync(server.Request("app.test", "/api/load"), ct);
                    await response.Content.LoadIntoBufferAsync(ct);
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    Interlocked.Increment(ref sent);
                }
            },
            ct)).ToArray();

        ConcurrentQueue<string> regressions = new();
        var publishes = 0;
        var monitor = Task.Run(
            async () =>
            {
                long lastHost = 0, lastRoute = 0, lastUpstream = 0;
                while (!stop.IsCancellationRequested)
                {
                    var host = Counters(registry, MetricSubject.Host, appSlot)[RequestMetric.Status + 1];
                    var route = Counters(registry, MetricSubject.Route, apiSlot)[RequestMetric.Status + 1];
                    var up = Counters(registry, MetricSubject.Upstream, upstreamSlot)[UpstreamMetric.Requests];
                    if (host < lastHost || route < lastRoute || up < lastUpstream)
                    {
                        regressions.Enqueue($"host {lastHost}->{host}, route {lastRoute}->{route}, upstream {lastUpstream}->{up} at publish {publishes}");
                    }

                    (lastHost, lastRoute, lastUpstream) = (host, route, up);
                    await Task.Delay(2, CancellationToken.None);
                }
            },
            ct);

        var publisher = Task.Run(
            async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    var started = Stopwatch.StartNew();
                    // Variant 1 adds an upstream and a host, variant 0 drops them again: a real YARP reload, and the app's
                    // position in the snapshot changes with it.
                    var result = await server.PublishAsync(Model(++publishes));
                    Assert.True(result.Succeeded, string.Join("; ", result.Errors));
                    var rest = PublishEvery - started.Elapsed;
                    if (rest > TimeSpan.Zero && !stop.IsCancellationRequested)
                    {
                        await Task.Delay(rest, CancellationToken.None);
                    }
                }
            },
            ct);

        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        await stop.CancelAsync();
        await Task.WhenAll([.. load, monitor, publisher]);

        Assert.True(publishes >= 8, $"only {publishes} publishes");
        Assert.Empty(regressions);
        var current = server.Store.Current.Hosts.Find("app.test", out _)!;
        Assert.Equal(appSlot, current.MetricSlot);
        Assert.Equal(apiSlot, current.Routes.Single(r => r.Id == TestIds.Route("app-api")).MetricSlot);
        // Every request that was answered is counted, once, on the slot it started with.
        var total = Interlocked.Read(ref sent);
        await WaitForAsync(() => Counters(registry, MetricSubject.Host, appSlot)[RequestMetric.Status + 1] >= total, "every answered request is counted");
        Assert.Equal(total, Counters(registry, MetricSubject.Host, appSlot)[RequestMetric.Status + 1]);
        Assert.Equal(total, Counters(registry, MetricSubject.Route, apiSlot)[RequestMetric.Status + 1]);
        Assert.Equal(total, Counters(registry, MetricSubject.Upstream, upstreamSlot)[UpstreamMetric.Requests]);
    }
}
