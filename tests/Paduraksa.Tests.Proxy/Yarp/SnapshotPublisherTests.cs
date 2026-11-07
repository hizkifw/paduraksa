using System.Collections.Concurrent;
using System.Globalization;
using System.Net;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Yarp;
using Paduraksa.Tests.Proxy.Testing;

using Yarp.ReverseProxy;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Model;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.Yarp;

/// <summary>PLAN §6.1 step 6 against real YARP: validate, apply, capture endpoints, swap.</summary>
public sealed class SnapshotPublisherTests(TestUpstream upstream) : IClassFixture<TestUpstream>
{
    private ProxyConfigModel FlipTo(string target, string revision) => new()
    {
        Revision = revision,
        Upstreams = [Upstream("ua", upstream.Address("a")), Upstream("ub", upstream.Address("b"))],
        Hosts = [ProxyHost("flip", "flip.test", Route("flip-root", target)), ProxyHost("steady", "steady.test", Route("steady-root", "ua"))],
    };

    [Fact]
    public async Task PublishUnderLoadHasNoErrorsAndNeverMixesGenerations()
    {
        // The gate stamps the generation it read; the upstream echoes which cluster served the request. Each
        // generation routes flip.test to a known cluster, so a mismatch means a request mixed two snapshots.
        await using ProxyTestServer server = await ProxyTestServer.StartAsync(
            FlipTo("ua", "g-a"),
            new()
            {
                AfterGate = static (context, next) =>
                {
                    if (context.Features.Get<IGateFeature>()?.State.Snapshot is { } snapshot)
                    {
                        context.Response.Headers["x-gate-generation"] = snapshot.Generation.ToString(CultureInfo.InvariantCulture);
                    }

                    return next(context);
                },
            });
        ConcurrentDictionary<long, string> clusterByGeneration = new() { [server.Store.Current.Generation] = "/a/" };
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = ProxyTestServer.CreateClient(maxConnections: 32);
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ConcurrentBag<string> failures = [];
        ConcurrentBag<(long Generation, string Body)> seen = [];
        long requests = 0;

        async Task LoadAsync()
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using HttpResponseMessage response = await client.SendAsync(server.Request("flip.test", "/x"), CancellationToken.None);
                    string body = await response.Content.ReadAsStringAsync(CancellationToken.None);
                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        failures.Add($"{(int)response.StatusCode}: {body}");
                        continue;
                    }

                    long generation = long.Parse(response.Headers.GetValues("x-gate-generation").Single(), CultureInfo.InvariantCulture);
                    seen.Add((generation, body));
                    Interlocked.Increment(ref requests);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException)
                {
                    failures.Add(ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        Task[] load = [.. Enumerable.Range(0, 16).Select(_ => Task.Run(LoadAsync, CancellationToken.None))];
        int publishes = 0;
        DateTime until = DateTime.UtcNow.AddSeconds(2.5);
        while (DateTime.UtcNow < until)
        {
            bool toB = publishes % 2 == 0;
            PublishResult result = await server.PublishAsync(FlipTo(toB ? "ub" : "ua", "g" + publishes));
            Assert.True(result.Succeeded, string.Join("; ", result.Errors));
            clusterByGeneration[result.Generation] = toB ? "/b/" : "/a/";
            publishes++;
            await Task.Delay(5, ct);
        }

        await stop.CancelAsync();
        await Task.WhenAll(load);

        Assert.Empty(failures);
        Assert.True(publishes >= 20, $"only {publishes} publishes");
        Assert.True(requests >= 500, $"only {requests} requests");
        List<string> mixed = [.. seen.Where(s => s.Body != clusterByGeneration[s.Generation] + "x").Select(s => $"gen {s.Generation}: {s.Body}").Distinct()];
        Assert.Empty(mixed);
        Assert.True(seen.Select(s => s.Generation).Distinct().Count() > 2, "requests only ever saw one or two generations");
    }

    [Fact]
    public async Task YarpValidationFailureKeepsTheOldSnapshotLive()
    {
        await using ProxyTestServer server = await ProxyTestServer.StartAsync(FlipTo("ua", "good"));
        RuntimeSnapshot before = server.Store.Current;
        ProxyConfigModel invalid = FlipTo("ub", "bad") with
        {
            Upstreams = [Upstream("ua", upstream.Address("a")), Upstream("ub", upstream.Address("b")) with { LoadBalancing = "NoSuchPolicy" }],
        };

        PublishResult result = await server.PublishAsync(invalid);

        Assert.Equal(PublishStatus.Invalid, result.Status);
        Assert.Contains(result.Errors, e => e.Contains("cluster 'ub'", StringComparison.Ordinal) && e.Contains("NoSuchPolicy", StringComparison.Ordinal));
        Assert.Same(before, server.Store.Current);
        Assert.Equal("/a/x", await GetAsync(server, "flip.test"));
        Assert.Equal(["ua", "ub"], server.Services.GetRequiredService<IProxyStateLookup>().GetClusters().Select(c => c.ClusterId).Order(StringComparer.Ordinal));
        Assert.Empty(await server.Publisher.ValidateAsync(TestSnapshots.Compile(FlipTo("ua", "good"))));
        Assert.NotEmpty(await server.Publisher.ValidateAsync(TestSnapshots.Compile(invalid)));
    }

    [Fact]
    public async Task ARejectedApplyKeepsTheOldSnapshotAndRestoresYarp()
    {
        ThrowingFilter filter = new("boom");
        await using ProxyTestServer server = await ProxyTestServer.StartAsync(
            FlipTo("ua", "good"),
            new() { Services = s => s.AddSingleton<IProxyConfigFilter>(filter) });
        RuntimeSnapshot before = server.Store.Current;
        ProxyConfigModel exploding = FlipTo("ub", "bad") with
        {
            Hosts = [ProxyHost("flip", "flip.test", Route("flip-root", "ub")), ProxyHost("boom", "boom.test", Route("boom", "ua"))],
        };

        PublishResult result = await server.PublishAsync(exploding);

        Assert.Equal(PublishStatus.Rejected, result.Status);
        Assert.Same(before, server.Store.Current);
        Assert.Equal("/a/x", await GetAsync(server, "flip.test"));
        Assert.True(before.Yarp.ContentEquals(server.Publisher.ConfigProvider.CurrentContent));

        PublishResult next = await server.PublishAsync(FlipTo("ub", "after"));
        Assert.True(next.Succeeded, string.Join("; ", next.Errors));
        Assert.Equal("/b/x", await GetAsync(server, "flip.test"));
    }

    [Fact]
    public async Task ATimedOutApplyKeepsTheOldSnapshot()
    {
        ThrowingFilter filter = new("never") { Block = new TaskCompletionSource() };
        await using ProxyTestServer server = await ProxyTestServer.StartAsync(
            FlipTo("ua", "good"),
            new()
            {
                Services = s => s.AddSingleton<IProxyConfigFilter>(filter),
                PublisherOptions = new SnapshotPublisherOptions { ApplyTimeout = TimeSpan.FromMilliseconds(200) },
            });
        RuntimeSnapshot before = server.Store.Current;

        PublishResult result = await server.PublishAsync(FlipTo("ub", "slow") with
        {
            Hosts = [ProxyHost("flip", "flip.test", Route("flip-root", "ub")), ProxyHost("never", "never.test", Route("never", "ua"))],
        });

        Assert.Equal(PublishStatus.TimedOut, result.Status);
        Assert.Same(before, server.Store.Current);
        Assert.Equal("/a/x", await GetAsync(server, "flip.test"));
        filter.Block.SetResult();
    }

    [Fact]
    public async Task DisposingStopsTheWriterAndFailsPendingPublishes()
    {
        ThrowingFilter filter = new("never") { Block = new TaskCompletionSource() };
        await using ProxyTestServer server = await ProxyTestServer.StartAsync(
            FlipTo("ua", "good"),
            new() { Services = s => s.AddSingleton<IProxyConfigFilter>(filter) });
        RuntimeSnapshot before = server.Store.Current;
        Task<PublishResult> stuck = server.PublishAsync(FlipTo("ub", "stuck") with
        {
            Hosts = [ProxyHost("flip", "flip.test", Route("flip-root", "ub")), ProxyHost("never", "never.test", Route("never", "ua"))],
        });

        await server.Publisher.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        PublishResult result = await stuck;
        Assert.Equal(PublishStatus.Failed, result.Status);
        Assert.Contains("stopped", result.Errors[0], StringComparison.Ordinal);
        Assert.Same(before, server.Store.Current);
        Assert.Equal(PublishStatus.Failed, (await server.PublishAsync(FlipTo("ub", "late"))).Status);
        filter.Block.SetResult();
    }

    [Fact]
    public async Task PublishesBeforeYarpStartsAreAppliedByItsInitialLoad()
    {
        // ProxyTestServer publishes the initial config before the host starts; it must already be live.
        await using ProxyTestServer server = await ProxyTestServer.StartAsync(FlipTo("ub", "early"));

        Assert.True(server.Publisher.IsAttached);
        Assert.Equal("early", server.Store.Current.Revision);
        Assert.Equal(1, server.Store.Current.Generation);
        Assert.Equal("/b/x", await GetAsync(server, "flip.test"));
    }

    [Fact]
    public async Task BeforeYarpStartsEmptyConfigsPublishAtOnceAndOthersWait()
    {
        using SnapshotStore store = new();
        SnapshotPublisher publisher = new(store);

        CancellationToken ct = TestContext.Current.CancellationToken;
        Task<PublishResult> empty = publisher.PublishAsync(TestSnapshots.Compile(ProxyConfigModel.Empty), ct);
        Assert.True(empty.IsCompletedSuccessfully);
        Assert.Equal(1, (await empty).Generation);
        Assert.Equal(["YARP isn't running yet, so the YARP config can't be validated."], await publisher.ValidateAsync(TestSnapshots.Compile(ProxyConfigModel.Empty)));

        Task<PublishResult> invalid = publisher.PublishAsync(TestSnapshots.Compile(FlipTo("ua", "bad-early") with
        {
            Upstreams = [Upstream("ua", upstream.Address("a")) with { LoadBalancing = "Nope" }, Upstream("ub", upstream.Address("b"))],
        }), ct);
        Assert.False(invalid.IsCompleted);

        // Attaching validates the queued candidate; an invalid one is refused and YARP starts on the live config.
        await using ProxyTestServer server = await ProxyTestServer.StartAsync(options: new() { Publisher = publisher });
        Assert.Equal(PublishStatus.Invalid, (await invalid).Status);
        Assert.Equal(1, store.Current.Generation);

        Assert.True((await server.PublishAsync(FlipTo("ua", "later"))).Succeeded);
        Assert.Equal("/a/x", await GetAsync(server, "flip.test"));
    }

    [Fact]
    public async Task PolicyOnlyEditsReuseYarpsEndpoints()
    {
        await using ProxyTestServer server = await ProxyTestServer.StartAsync(FlipTo("ua", "one"));
        RuntimeSnapshot before = server.Store.Current;
        ProxyConfigModel policyOnly = FlipTo("ua", "two") with
        {
            Hosts = [ProxyHost("flip", "flip.test", Route("flip-root", "ua") with { Policies = [new DenyPolicySpec(403, "no\n")] }), ProxyHost("steady", "steady.test", Route("steady-root", "ua"))],
        };

        PublishResult result = await server.PublishAsync(policyOnly);

        Assert.True(result.Succeeded);
        RuntimeSnapshot after = server.Store.Current;
        Assert.Equal(before.Generation + 1, after.Generation);
        Assert.Equal(before.YarpEndpoints, after.YarpEndpoints);
        using HttpClient client = ProxyTestServer.CreateClient();
        using HttpResponseMessage denied = await client.SendAsync(server.Request("flip.test"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task ABurstOfPublishesCoalescesToTheLatest()
    {
        await using ProxyTestServer server = await ProxyTestServer.StartAsync(FlipTo("ua", "start"));
        CancellationToken ct = TestContext.Current.CancellationToken;

        List<Task<PublishResult>> burst = [];
        for (int i = 0; i < 20; i++)
        {
            burst.Add(server.Publisher.PublishAsync(TestSnapshots.Compile(FlipTo(i % 2 == 0 ? "ub" : "ua", "burst" + i)), ct));
        }

        PublishResult[] results = await Task.WhenAll(burst);

        Assert.True(results[^1].Succeeded);
        Assert.All(results, r => Assert.True(r.Status is PublishStatus.Published or PublishStatus.Superseded, r.Status.ToString()));
        Assert.Contains(results, r => r.Status == PublishStatus.Superseded);
        Assert.Equal("burst19", server.Store.Current.Revision);
        Assert.Equal(results[^1].Generation, server.Store.Current.Generation);
        Assert.Equal("/a/x", await GetAsync(server, "flip.test"));
    }

    [Fact]
    public async Task ClientsAreReusedUnlessTheClientConfigChanges()
    {
        await using ProxyTestServer server = await ProxyTestServer.StartAsync(FlipTo("ua", "one"));
        IProxyStateLookup state = server.Services.GetRequiredService<IProxyStateLookup>();
        HttpMessageInvoker Client(string cluster) => state.TryGetCluster(cluster, out ClusterState? c) ? c.Model.HttpClient : throw new InvalidOperationException(cluster);
        HttpMessageInvoker original = Client("ua");

        Assert.True((await server.PublishAsync(FlipTo("ub", "two"))).Succeeded);
        Assert.Same(original, Client("ua"));

        ProxyConfigModel moreConnections = FlipTo("ub", "three") with
        {
            Upstreams = [Upstream("ua", upstream.Address("a")) with { MaxConnectionsPerServer = 7 }, Upstream("ub", upstream.Address("b"))],
        };
        Assert.True((await server.PublishAsync(moreConnections)).Succeeded);
        Assert.NotSame(original, Client("ua"));
        Assert.Equal("/a/x", await GetAsync(server, "steady.test"));
    }

    [Fact]
    public async Task LoadBalancingAffinityAndPassiveHealthAreInThePipeline()
    {
        ProxyConfigModel model = new()
        {
            Upstreams =
            [
                new UpstreamSpec { Key = "lb", LoadBalancing = "RoundRobin", Destinations = [new("lb-a", upstream.Address("a")), new("lb-b", upstream.Address("b"))] },
                new UpstreamSpec { Key = "aff", SessionAffinity = true, Destinations = [new("aff-a", upstream.Address("a")), new("aff-b", upstream.Address("b"))] },
                new UpstreamSpec
                {
                    Key = "ph",
                    PassiveHealth = true,
                    LoadBalancing = "RoundRobin",
                    ReactivationPeriod = TimeSpan.FromMinutes(10),
                    Destinations = [new("ph-good", upstream.Address("good")), new("ph-dead", $"http://127.0.0.1:{upstream.DeadPort}/")],
                },
            ],
            Hosts = [ProxyHost("lb", "lb.test", Route("lb", "lb")), ProxyHost("aff", "aff.test", Route("aff", "aff")), ProxyHost("ph", "ph.test", Route("ph", "ph"))],
        };
        await using ProxyTestServer server = await ProxyTestServer.StartAsync(model);
        using HttpClient client = ProxyTestServer.CreateClient();
        CancellationToken ct = TestContext.Current.CancellationToken;

        // Load balancing: round robin reaches both destinations.
        HashSet<string> balanced = [];
        for (int i = 0; i < 6; i++)
        {
            balanced.Add(await GetAsync(server, "lb.test"));
        }

        Assert.Equal(["/a/x", "/b/x"], balanced.Order(StringComparer.Ordinal));

        // Session affinity: the cookie pins every follow-up to the first destination.
        using HttpResponseMessage first = await client.SendAsync(server.Request("aff.test", "/x"), ct);
        string pinned = await first.Content.ReadAsStringAsync(ct);
        string cookie = first.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(SnapshotCompiler.AffinityCookieName, StringComparison.Ordinal)).Split(';')[0];
        for (int i = 0; i < 10; i++)
        {
            using HttpRequestMessage request = server.Request("aff.test", "/x");
            request.Headers.Add("Cookie", cookie);
            using HttpResponseMessage response = await client.SendAsync(request, ct);
            Assert.Equal(pinned, await response.Content.ReadAsStringAsync(ct));
        }

        // Passive health: failures against the dead destination take it out of rotation.
        IProxyStateLookup state = server.Services.GetRequiredService<IProxyStateLookup>();
        Assert.True(state.TryGetCluster("ph", out ClusterState? cluster));
        DestinationState dead = cluster.Destinations["ph-dead"];
        for (int i = 0; i < 40 && dead.Health.Passive != DestinationHealth.Unhealthy; i++)
        {
            using HttpResponseMessage response = await client.SendAsync(server.Request("ph.test", "/x"), ct);
        }

        Assert.Equal(DestinationHealth.Unhealthy, dead.Health.Passive);
        for (int i = 0; i < 10; i++)
        {
            Assert.Equal("/good/x", await GetAsync(server, "ph.test"));
        }
    }

    private static async Task<string> GetAsync(ProxyTestServer server, string host)
    {
        using HttpClient client = ProxyTestServer.CreateClient();
        using HttpResponseMessage response = await client.SendAsync(server.Request(host, "/x"), TestContext.Current.CancellationToken);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {body}");
        return body;
    }

    /// <summary>Makes YARP fail (or stall) applying any config that contains a route with the given ID.</summary>
    private sealed class ThrowingFilter(string routeId) : IProxyConfigFilter
    {
        public TaskCompletionSource? Block { get; init; }

        public ValueTask<ClusterConfig> ConfigureClusterAsync(ClusterConfig cluster, CancellationToken cancel) => ValueTask.FromResult(cluster);

        public async ValueTask<RouteConfig> ConfigureRouteAsync(RouteConfig route, ClusterConfig? cluster, CancellationToken cancel)
        {
            if (!string.Equals(route.RouteId, routeId, StringComparison.Ordinal))
            {
                return route;
            }

            if (Block is not null)
            {
                await Block.Task.WaitAsync(cancel);
                return route;
            }

            throw new InvalidOperationException("Simulated apply failure.");
        }
    }
}
