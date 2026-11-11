using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Metrics;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.Metrics;

public sealed class MetricsCompilationTests
{
    private static ProxyConfigModel Model(params HostSpec[] hosts) => new()
    {
        Upstreams = [Upstream("web", "http://10.0.0.1:80/", "http://10.0.0.2:80/")],
        Hosts = hosts,
    };

    private static HostSpec App => ProxyHost("app", "app.test", Route("app-api", "web", "/api"), Route("app-root", "web"));

    [Fact]
    public void ACompileThatWillBePublishedGivesEveryObjectASlot()
    {
        MetricsRegistry registry = new(processors: 1);

        var snapshot = Compile(Model(App), new SnapshotCompilerOptions { Metrics = registry });

        var host = Assert.Single(snapshot.HostsByIndex);
        Assert.False(host.MetricSlot.IsNone);
        Assert.Equal(TestIds.Host("app").Value, registry.Hosts.OwnerOf(host.MetricSlot.Index));
        Assert.All(host.Routes, route =>
        {
            Assert.False(route.MetricSlot.IsNone);
            Assert.Equal(route.Id.Value, registry.Routes.OwnerOf(route.MetricSlot.Index));
            Assert.NotNull(route.Upstream);
            Assert.Equal(TestIds.Upstream("web").Value, registry.Upstreams.OwnerOf(route.Upstream.Slot.Index));
        });
        Assert.Same(host.Routes[0].Upstream, host.Routes[1].Upstream);
        var destinations = host.Routes[0].Upstream!;
        Assert.Equal(TestIds.Destination("web-d0").Value, registry.Destinations.OwnerOf(destinations.DestinationSlot(TestIds.Destination("web-d0").ToString()).Index));
        Assert.Equal(TestIds.Destination("web-d1").Value, registry.Destinations.OwnerOf(destinations.DestinationSlot(TestIds.Destination("web-d1").ToString()).Index));
        Assert.True(destinations.DestinationSlot("dst_unknown").IsNone);
        Assert.True(destinations.DestinationSlot(null).IsNone);
        Assert.Equal(1, snapshot.MetricRegistrations.Count(MetricSubject.Host));
        Assert.Equal(2, snapshot.MetricRegistrations.Count(MetricSubject.Route));
        Assert.Equal(1, snapshot.MetricRegistrations.Count(MetricSubject.Upstream));
        Assert.Equal(2, snapshot.MetricRegistrations.Count(MetricSubject.Destination));
    }

    [Fact]
    public void AnObjectKeepsItsSlotAcrossCompilesWhateverIsAroundIt()
    {
        MetricsRegistry registry = new(processors: 1);
        SnapshotCompilerOptions options = new() { Metrics = registry };
        var first = Compile(Model(App), options);

        // Another host comes first in the document and takes the first host index: slots must not follow positions.
        var second = Compile(Model(ProxyHost("front", "front.test", Route("front-root", "web")), App), options);

        var before = first.HostsByIndex.Single(h => h.Id == TestIds.Host("app"));
        var after = second.HostsByIndex.Single(h => h.Id == TestIds.Host("app"));
        Assert.NotEqual(before.Index, after.Index);
        Assert.Equal(before.MetricSlot, after.MetricSlot);
        Assert.Equal(before.Routes[0].MetricSlot, after.Routes[0].MetricSlot);
        Assert.Equal(before.Routes[0].Upstream!.Slot, after.Routes[0].Upstream!.Slot);
    }

    [Fact]
    public void ADraftADryRunOrASimulationAllocatesNoSlotAndFindsTheOnesThatExist()
    {
        MetricsRegistry registry = new(processors: 1);
        var draft = new SnapshotCompilerOptions { Metrics = registry, WillBePublished = false };

        var unpublished = Compile(Model(App), draft);

        Assert.True(unpublished.HostsByIndex[0].MetricSlot.IsNone);
        Assert.Equal(1, registry.Hosts.HighWater);
        Assert.Equal(0, registry.Routes.HighWater);

        var published = Compile(Model(App), new SnapshotCompilerOptions { Metrics = registry });
        var again = Compile(Model(App), draft);

        Assert.Equal(published.HostsByIndex[0].MetricSlot, again.HostsByIndex[0].MetricSlot);
        Assert.Equal(published.HostsByIndex[0].Routes[0].MetricSlot, again.HostsByIndex[0].Routes[0].MetricSlot);
    }

    [Fact]
    public void ADisabledHostOwnsNoCounters()
    {
        MetricsRegistry registry = new(processors: 1);

        var snapshot = Compile(Model(App with { Enabled = false }), new SnapshotCompilerOptions { Metrics = registry });

        Assert.Empty(snapshot.HostsByIndex);
        Assert.Equal(1, registry.Hosts.HighWater);
        Assert.Equal(0, registry.Routes.HighWater);
        Assert.Equal(0, registry.Upstreams.HighWater);
    }

    [Fact]
    public void AnUpstreamNoEnabledHostRoutesToOwnsNoCounters()
    {
        MetricsRegistry registry = new(processors: 1);

        _ = Compile(Model(App), new SnapshotCompilerOptions { Metrics = registry });

        Assert.Equal(1, registry.Upstreams.HighWater);
        Assert.Equal(2, registry.Destinations.HighWater);
    }

    [Fact]
    public void WithoutARegistryNothingGetsASlot()
    {
        var snapshot = Compile(Model(App));

        Assert.True(snapshot.HostsByIndex[0].MetricSlot.IsNone);
        Assert.True(snapshot.HostsByIndex[0].Routes[0].MetricSlot.IsNone);
        Assert.Null(snapshot.HostsByIndex[0].Routes[0].Upstream);
        Assert.Equal(0, snapshot.MetricRegistrations.Count(MetricSubject.Host));
    }

    [Fact]
    public void PublishingASnapshotRetiresTheObjectsTheNextOneDrops()
    {
        MetricsRegistry registry = new(processors: 1);
        SnapshotCompilerOptions options = new() { Metrics = registry };
        var both = Compile(Model(App, ProxyHost("other", "other.test", Route("other-root", "web"))), options);
        registry.Publish(both.MetricRegistrations);
        var otherSlot = both.HostsByIndex.Single(h => h.Id == TestIds.Host("other")).MetricSlot;
        Assert.True(registry.Hosts.IsLive(otherSlot.Index));

        var onlyApp = Compile(Model(App), options);
        registry.Publish(onlyApp.MetricRegistrations);

        Assert.False(registry.Hosts.IsLive(otherSlot.Index));
        Assert.True(registry.Hosts.IsLive(onlyApp.HostsByIndex[0].MetricSlot.Index));
        // The reserved unmatched slot is nobody's and is never retired.
        Assert.True(registry.Hosts.IsLive(MetricsRegistry.UnmatchedHost.Index));
    }
}
