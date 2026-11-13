using Paduraksa.Proxy.Metrics;
using Paduraksa.Tests.Proxy.Testing;

namespace Paduraksa.Tests.Proxy.Metrics;

public sealed class MetricsRegistryTests
{
    private static readonly Guid HostA = new("00000000-0000-7000-8000-00000000000a");
    private static readonly Guid RouteA = new("00000000-0000-7000-8000-0000000000a1");
    private static readonly Guid UpstreamA = new("00000000-0000-7000-8000-0000000000b1");
    private static readonly Guid DestinationA = new("00000000-0000-7000-8000-0000000000c1");

    private static long[] Counters(MetricsRegistry registry, MetricSubject subject, int slot)
    {
        var totals = new long[32];
        registry.ReadCounters(subject, slot, totals);
        return totals;
    }

    [Fact]
    public void ARequestCountsOnTheHostRouteUpstreamAndDestination()
    {
        MetricsRegistry registry = new(processors: 2);
        var host = registry.SlotFor(MetricSubject.Host, HostA);
        var route = registry.SlotFor(MetricSubject.Route, RouteA);
        var upstream = registry.SlotFor(MetricSubject.Upstream, UpstreamA);
        var destination = registry.SlotFor(MetricSubject.Destination, DestinationA);

        registry.RecordRequest(new RequestMetrics
        {
            Host = host,
            Route = route,
            Upstream = upstream,
            Destination = destination,
            Status = 200,
            Outcome = GateOutcome.Forwarded,
            ResponseBytes = 512,
            RequestBytes = 2_048,
            TotalMicros = 1_500,
            UpstreamMicros = 1_000,
        });
        registry.RecordRequest(new RequestMetrics
        {
            Host = host,
            Route = route,
            Upstream = upstream,
            Destination = destination,
            Status = 502,
            Outcome = GateOutcome.Forwarded,
            TotalMicros = 300,
            UpstreamMicros = 200,
            ForwarderError = 10,
        });

        var hostCounters = Counters(registry, MetricSubject.Host, host.Index);
        Assert.Equal(1, hostCounters[RequestMetric.Status + 1]);
        Assert.Equal(1, hostCounters[RequestMetric.Status + 4]);
        Assert.Equal(2, hostCounters[RequestMetric.Outcome + (int)GateOutcome.Forwarded]);
        Assert.Equal(512, hostCounters[RequestMetric.BytesOut]);
        Assert.Equal(2_048, hostCounters[RequestMetric.BytesIn]);
        Assert.Equal(1_800, hostCounters[RequestMetric.TotalMicros]);
        Assert.Equal(1_200, hostCounters[RequestMetric.UpstreamMicros]);

        var routeCounters = Counters(registry, MetricSubject.Route, route.Index);
        Assert.Equal(1, routeCounters[RequestMetric.Status + 1]);
        Assert.Equal(512, routeCounters[RequestMetric.BytesOut]);
        Assert.Equal(2_048, routeCounters[RequestMetric.BytesIn]);

        var upstreamCounters = Counters(registry, MetricSubject.Upstream, upstream.Index);
        Assert.Equal(2, upstreamCounters[UpstreamMetric.Requests]);
        Assert.Equal(1, upstreamCounters[UpstreamMetric.Errors + 10]);
        Assert.Equal(1_200, upstreamCounters[UpstreamMetric.UpstreamMicros]);
        Assert.Equal(512, upstreamCounters[UpstreamMetric.BytesOut]);
        Assert.Equal(2_048, upstreamCounters[UpstreamMetric.BytesIn]);

        var destinationCounters = Counters(registry, MetricSubject.Destination, destination.Index);
        Assert.Equal(2, destinationCounters[DestinationMetric.Requests]);
        Assert.Equal(1, destinationCounters[DestinationMetric.Errors]);

        var total = new long[LatencyBuckets.Stride];
        var upstreamTime = new long[LatencyBuckets.Stride];
        registry.ReadLatency(MetricSubject.Host, host.Index, total, upstreamTime);
        Assert.Equal(1, total[LatencyBuckets.IndexOf(1_500)]);
        Assert.Equal(1, total[LatencyBuckets.IndexOf(300)]);
        Assert.Equal(1, upstreamTime[LatencyBuckets.IndexOf(1_000)]);
        registry.ReadLatency(MetricSubject.Upstream, upstream.Index, total, upstreamTime);
        Assert.Equal(1, upstreamTime[LatencyBuckets.IndexOf(200)]);
        Assert.All(total, count => Assert.Equal(0, count));
    }

    [Fact]
    public void AClientThatLeftIsNotTheDestinationsError()
    {
        MetricsRegistry registry = new(processors: 1);
        var upstream = registry.SlotFor(MetricSubject.Upstream, UpstreamA);
        var destination = registry.SlotFor(MetricSubject.Destination, DestinationA);

        // ResponseBodyClient (9): the client hung up mid-transfer. The upstream's kind counter still records it.
        registry.RecordRequest(new RequestMetrics { Upstream = upstream, Destination = destination, Status = 200, Outcome = GateOutcome.Forwarded, ForwarderError = 9 });

        Assert.Equal(0, Counters(registry, MetricSubject.Destination, destination.Index)[DestinationMetric.Errors]);
        Assert.Equal(1, Counters(registry, MetricSubject.Upstream, upstream.Index)[UpstreamMetric.Errors + 9]);
    }

    [Fact]
    public void RequestsNoHostOwnsCountOnTheReservedUnmatchedSlot()
    {
        MetricsRegistry registry = new(processors: 1);

        registry.RecordRequest(new RequestMetrics { Host = MetricsRegistry.UnmatchedHost, Status = 404, Outcome = GateOutcome.UnknownHost, TotalMicros = 10 });

        var counters = Counters(registry, MetricSubject.Host, MetricsRegistry.UnmatchedHost.Index);
        Assert.Equal(1, counters[RequestMetric.Status + 3]);
        Assert.Equal(1, counters[RequestMetric.Outcome + (int)GateOutcome.UnknownHost]);
    }

    [Theory]
    [InlineData(100, 0)]
    [InlineData(204, 1)]
    [InlineData(301, 2)]
    [InlineData(404, 3)]
    [InlineData(599, 4)]
    [InlineData(0, 5)]
    [InlineData(600, 5)]
    [InlineData(99, 5)]
    public void StatusesFallInTheirClassAndTheRestInNoResponse(int status, int expected) => Assert.Equal(expected, RequestMetric.ClassOf(status));

    [Fact]
    public void ARequestWithNoSlotsCountsNothingAndThrowsNothing()
    {
        MetricsRegistry registry = new(processors: 1);

        registry.RecordRequest(new RequestMetrics { Status = 200, Outcome = GateOutcome.Forwarded, TotalMicros = 5 });

        Assert.All(Counters(registry, MetricSubject.Host, 0), count => Assert.Equal(0, count));
    }

    [Fact]
    public void ARetiredObjectsCountersAreClearedWhenItsSlotGoesToTheNextOne()
    {
        MetricsRegistry registry = new(processors: 1, gracePeriod: TimeSpan.Zero);
        var first = registry.SlotFor(MetricSubject.Route, RouteA);
        registry.RecordRequest(new RequestMetrics { Route = first, Status = 200, Outcome = GateOutcome.Forwarded });
        Assert.Equal(1, Counters(registry, MetricSubject.Route, first.Index)[RequestMetric.Status + 1]);

        registry.Publish(new MetricRegistrations());
        var second = registry.SlotFor(MetricSubject.Route, new Guid("00000000-0000-7000-8000-0000000000a2"));

        Assert.Equal(first.Index, second.Index);
        Assert.Equal(0, Counters(registry, MetricSubject.Route, second.Index)[RequestMetric.Status + 1]);
    }

    [Fact]
    public void RecordingAllocatesNothing()
    {
        MetricsRegistry registry = new(processors: 2);
        RequestMetrics request = new()
        {
            Host = registry.SlotFor(MetricSubject.Host, HostA),
            Route = registry.SlotFor(MetricSubject.Route, RouteA),
            Upstream = registry.SlotFor(MetricSubject.Upstream, UpstreamA),
            Destination = registry.SlotFor(MetricSubject.Destination, DestinationA),
            Status = 200,
            Outcome = GateOutcome.Forwarded,
            ResponseBytes = 10,
            TotalMicros = 1_234,
            UpstreamMicros = 1_000,
            ForwarderError = 2,
        };

        AllocAssert.Zero((registry, request), static state => state.registry.RecordRequest(in state.request), "RecordRequest");
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;

        public override long GetTimestamp() => Now.UtcTicks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    [Fact]
    public void ARequestThatOutlivesItsSlotsGracePeriodIsNotCountedIntoTheNextOwner()
    {
        ManualClock clock = new(new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero));
        MetricsRegistry registry = new(processors: 2, gracePeriod: TimeSpan.FromSeconds(60), timeProvider: clock);
        var oldHost = registry.SlotFor(MetricSubject.Host, HostA);
        var oldRoute = registry.SlotFor(MetricSubject.Route, RouteA);
        // A WebSocket on host A: it started under A's slot and is still open.
        RequestMetrics inFlight = new()
        {
            Host = oldHost,
            Route = oldRoute,
            Status = 101,
            Outcome = GateOutcome.Forwarded,
            ResponseBytes = 10,
            TotalMicros = 7_000_000_000,
        };
        registry.Publish(new MetricRegistrations());
        clock.Now = clock.Now.AddSeconds(61);

        // The slots go to a new host and route.
        var newHost = registry.SlotFor(MetricSubject.Host, new Guid("00000000-0000-7000-8000-00000000000b"));
        var newRoute = registry.SlotFor(MetricSubject.Route, new Guid("00000000-0000-7000-8000-0000000000a2"));
        Assert.Equal(oldHost.Index, newHost.Index);
        Assert.Equal(oldRoute.Index, newRoute.Index);
        Assert.NotEqual(oldHost.Version, newHost.Version);
        Assert.False(registry.IsCurrent(MetricSubject.Host, oldHost));
        Assert.True(registry.IsCurrent(MetricSubject.Host, newHost));
        registry.RecordRequest(new RequestMetrics { Host = newHost, Route = newRoute, Status = 200, Outcome = GateOutcome.Forwarded, TotalMicros = 1_000 });

        registry.RecordRequest(in inFlight);

        var host = Counters(registry, MetricSubject.Host, newHost.Index);
        Assert.Equal(1, host.Take(RequestMetric.StatusClasses).Sum());
        Assert.Equal(0, host[RequestMetric.Status]);
        Assert.Equal(0, host[RequestMetric.BytesOut]);
        Assert.Equal(1_000, host[RequestMetric.TotalMicros]);
        Assert.Equal(1, Counters(registry, MetricSubject.Route, newRoute.Index).Take(RequestMetric.StatusClasses).Sum());
        var total = new long[LatencyBuckets.Stride];
        registry.ReadLatency(MetricSubject.Host, newHost.Index, total, new long[LatencyBuckets.Stride]);
        Assert.Equal(1, total.Sum());
        Assert.Equal(0, total[LatencyBuckets.Overflow]);
    }

    [Fact]
    public void ASlotThatWasNeverRentedKeepsItsVersionAcrossRetireAndReclaim()
    {
        ManualClock clock = new(new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero));
        MetricsRegistry registry = new(processors: 1, gracePeriod: TimeSpan.FromSeconds(60), timeProvider: clock);
        var slot = registry.SlotFor(MetricSubject.Host, HostA);
        MetricRegistrations keep = new();
        keep.Add(MetricSubject.Host, HostA, slot.Index);
        registry.Publish(new MetricRegistrations());

        Assert.Equal(0, registry.Publish(keep));

        Assert.True(registry.IsCurrent(MetricSubject.Host, slot));
        registry.RecordRequest(new RequestMetrics { Host = slot, Status = 200, Outcome = GateOutcome.Forwarded });
        Assert.Equal(1, Counters(registry, MetricSubject.Host, slot.Index)[RequestMetric.Status + 1]);
    }
}
