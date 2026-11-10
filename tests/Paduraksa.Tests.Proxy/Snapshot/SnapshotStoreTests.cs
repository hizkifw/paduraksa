using System.Globalization;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Yarp;
using Paduraksa.Tests.Shared;

using Yarp.ReverseProxy.Configuration;

namespace Paduraksa.Tests.Proxy.Snapshot;

public sealed class SnapshotStoreTests
{
    [Fact]
    public void StartsWithTheBootstrapSnapshot()
    {
        using SnapshotStore store = new();

        Assert.Same(RuntimeSnapshot.Bootstrap, store.Current);
        Assert.False(store.HasPublished);
        Assert.Equal(0, store.Current.Generation);
        Assert.Equal(DefaultHostAction.Respond, store.Current.Default.Action);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, store.Current.Default.Response!.StatusCode);
    }

    [Fact]
    public void PublishSwapsAndFiresTheChangeToken()
    {
        using SnapshotStore store = new();
        var token = store.GetChangeToken();
        var first = Candidate().Bind(1, []);

        store.Publish(first);

        Assert.Same(first, store.Current);
        Assert.True(store.HasPublished);
        Assert.True(token.HasChanged);
        Assert.False(store.GetChangeToken().HasChanged);
    }

    [Fact]
    public void GenerationsMustIncrease()
    {
        using SnapshotStore store = new();
        store.Publish(Candidate().Bind(2, []));

        Assert.Throws<InvalidOperationException>(() => store.Publish(Candidate().Bind(2, [])));
        Assert.Throws<InvalidOperationException>(() => store.Publish(Candidate().Bind(1, [])));
        store.Publish(Candidate().Bind(5, []));
        Assert.Equal(5, store.Current.Generation);
    }

    [Fact]
    public void UnboundSnapshotsAreRefused()
    {
        using SnapshotStore store = new();
        var unbound = Candidate(routes: ["r1"]);

        Assert.False(unbound.IsBound);
        Assert.Throws<ArgumentException>(() => store.Publish(unbound));
    }

    [Fact]
    public void BindNeedsOneEndpointPerSlotAndKeepsTheContent()
    {
        var candidate = Candidate(routes: ["r1", "r2"]);
        Endpoint e1 = new(static _ => Task.CompletedTask, null, "r1");
        Endpoint e2 = new(static _ => Task.CompletedTask, null, "r2");

        Assert.Throws<ArgumentException>(() => candidate.Bind(1, [e1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => candidate.Bind(0, [e1, e2]));

        var bound = candidate.Bind(7, [e1, e2]);
        Assert.True(bound.IsBound);
        Assert.Equal(7, bound.Generation);
        Assert.Equal([e1, e2], bound.YarpEndpoints);
        Assert.Same(candidate.Hosts, bound.Hosts);
        Assert.Same(candidate.Yarp, bound.Yarp);
        Assert.Equal(0, candidate.Generation);
        Assert.Empty(candidate.YarpEndpoints);
    }

    [Fact]
    public async Task ConcurrentReadersAlwaysSeeAWholeSnapshot()
    {
        using SnapshotStore store = new();
        using CancellationTokenSource stop = new();
        const int Generations = 2_000;
        ConfigRevisionId[] revisions = [.. Enumerable.Range(0, Generations + 1).Select(g => TestIds.Revision("rev" + g.ToString(CultureInfo.InvariantCulture)))];
        long torn = 0;
        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var s = store.Current;
                if (s.Generation > 0 && s.Revision != revisions[s.Generation])
                {
                    Interlocked.Increment(ref torn);
                }
            }
        }, TestContext.Current.CancellationToken);

        for (var g = 1; g <= Generations; g++)
        {
            store.Publish(Candidate(revisions[g]).Bind(g, []));
        }

        stop.Cancel();
        await reader;
        Assert.Equal(0, torn);
    }

    private static RuntimeSnapshot Candidate(ConfigRevisionId revision = default, string[]? routes = null)
    {
        RouteId[] routeIds = [.. (routes ?? []).Select(TestIds.Route)];
        var yarpRoutes = new RouteConfig[routeIds.Length];
        for (var i = 0; i < yarpRoutes.Length; i++)
        {
            yarpRoutes[i] = new RouteConfig { RouteId = routeIds[i].ToString(), ClusterId = TestIds.Upstream("c").ToString(), Match = new RouteMatch { Path = "/x" } };
        }

        return new RuntimeSnapshot
        {
            Generation = 0,
            Revision = revision,
            Hosts = HostTable.Empty,
            HostsByIndex = [],
            Yarp = new YarpConfig(yarpRoutes, []),
            RouteIdBySlot = routeIds,
            YarpEndpoints = [],
            Default = DefaultHostBehaviour.NotFound(),
        };
    }
}
