
namespace Paduraksa.Proxy.Metrics;

/// <summary>One finished request, as the access-log completion hands it over (PLAN §7.4). Slots are none when the request had no such object.</summary>
public readonly struct RequestMetrics
{
    public MetricSlot Host { get; init; } = MetricSlot.None;

    public MetricSlot Route { get; init; } = MetricSlot.None;

    public MetricSlot Upstream { get; init; } = MetricSlot.None;

    public MetricSlot Destination { get; init; } = MetricSlot.None;

    /// <summary>The status the client got; 0 when nothing was sent.</summary>
    public int Status { get; init; }

    public GateOutcome Outcome { get; init; }

    /// <summary>Response body bytes written to the client, or -1 when they weren't counted.</summary>
    public long ResponseBytes { get; init; } = -1;

    /// <summary>Request body bytes read from the client, or -1 when they weren't counted.</summary>
    public long RequestBytes { get; init; } = -1;

    /// <summary>The whole request, in whole microseconds.</summary>
    public long TotalMicros { get; init; }

    /// <summary>The forward to the upstream, in whole microseconds; -1 when nothing was forwarded.</summary>
    public long UpstreamMicros { get; init; } = -1;

    /// <summary>YARP's <c>ForwarderError</c> as an integer; 0 when the forward went well or didn't happen.</summary>
    public int ForwarderError { get; init; }

    public RequestMetrics()
    {
    }
}

/// <summary>
/// The process's request counters and latency histograms (PLAN §6.3, §7.4): four append-only slot registries (host, route,
/// upstream, destination), the striped tables the hot path adds to, and the cold-path reads the sampler, the Prometheus
/// endpoint and the OpenTelemetry instruments use.
/// </summary>
/// <remarks>
/// <para><b>Slots, not positions.</b> The compiler asks <see cref="SlotFor"/> for each object's slot, so a host keeps its
/// counters across every publish; the publisher calls <see cref="Publish"/> just before a snapshot goes live, which
/// retires what the snapshot no longer has. A deleted object's slot is rented to another only after the grace period (one
/// rollup interval), and its counters are cleared first (the registry's <c>prepare</c> step).</para>
/// <para><b>What a request costs.</b> One <see cref="RecordRequest"/> is about ten to fifteen <see cref="Interlocked"/>
/// operations on cache lines this core owns (a host's status class, gate outcome, bytes out and in, duration sum and two histogram
/// buckets; the route's four; the upstream's and destination's; a byte count only when the body had bytes), no lock, no allocation. The tables are read only by the
/// sampler and the exporters, a second apart or more.</para>
/// <para><b>Unmatched requests</b> (no host for the name) count on the reserved host slot <see cref="UnmatchedHost"/>, so a scanner
/// shows up as traffic that belongs to nobody.</para>
/// </remarks>
public sealed class MetricsRegistry
{
    /// <summary>One rollup's worth of time, like the rate-limit slots: long enough for the sampler to see a retired slot's last counts.</summary>
    public static readonly TimeSpan DefaultGracePeriod = TimeSpan.FromMinutes(1);

    /// <summary>The host slot of requests no host owns.</summary>
    public static MetricSlot UnmatchedHost => new(0);

    private readonly TimeProvider _time;

    /// <param name="processors">The core count the stripes are sized from; null: <see cref="Environment.ProcessorCount"/>.</param>
    /// <param name="gracePeriod">How long a deleted object's slot stays out of reuse.</param>
    /// <param name="timeProvider">The clock slot retirement is timed on; tests drive it.</param>
    public MetricsRegistry(int? processors = null, TimeSpan? gracePeriod = null, TimeProvider? timeProvider = null)
    {
        var grace = gracePeriod ?? DefaultGracePeriod;
        var stripes = MetricTable.StripesFor(processors ?? Environment.ProcessorCount);
        _time = timeProvider ?? TimeProvider.System;
        HostCounters = new MetricTable(RequestMetric.Stride, stripes);
        HostLatency = new MetricTable(RequestMetric.LatencyStride, stripes);
        RouteCounters = new MetricTable(RequestMetric.Stride, stripes);
        UpstreamCounters = new MetricTable(UpstreamMetric.Stride, stripes);
        UpstreamLatency = new MetricTable(UpstreamMetric.LatencyStride, stripes);
        DestinationCounters = new MetricTable(DestinationMetric.Stride, stripes);
        Hosts = new MetricSlotRegistry(MetricSubject.Host, grace, (slot, reused) => Prepare(slot, reused, HostCounters, HostLatency), reserved: 1);
        Routes = new MetricSlotRegistry(MetricSubject.Route, grace, (slot, reused) => Prepare(slot, reused, RouteCounters));
        Upstreams = new MetricSlotRegistry(MetricSubject.Upstream, grace, (slot, reused) => Prepare(slot, reused, UpstreamCounters, UpstreamLatency));
        Destinations = new MetricSlotRegistry(MetricSubject.Destination, grace, (slot, reused) => Prepare(slot, reused, DestinationCounters));
    }

    internal MetricTable HostCounters { get; }

    internal MetricTable HostLatency { get; }

    internal MetricTable RouteCounters { get; }

    internal MetricTable UpstreamCounters { get; }

    internal MetricTable UpstreamLatency { get; }

    internal MetricTable DestinationCounters { get; }

    public MetricSlotRegistry Hosts { get; }

    public MetricSlotRegistry Routes { get; }

    public MetricSlotRegistry Upstreams { get; }

    public MetricSlotRegistry Destinations { get; }

    /// <summary>The slots registry of <paramref name="subject"/>.</summary>
    public MetricSlotRegistry RegistryOf(MetricSubject subject) => subject switch
    {
        MetricSubject.Host => Hosts,
        MetricSubject.Route => Routes,
        MetricSubject.Upstream => Upstreams,
        MetricSubject.Destination => Destinations,
        _ => throw new ArgumentOutOfRangeException(nameof(subject), subject, "Undefined metric subject."),
    };

    /// <summary>The object's slot, assigning one on the compiler's cold path (lock).</summary>
    public MetricSlot SlotFor(MetricSubject subject, Guid id)
    {
        var registry = RegistryOf(subject);
        var slot = registry.GetOrAdd(id, NowMs);
        return new(slot, registry.VersionOf(slot));
    }

    /// <summary>The object's slot if it has one: how a compile that will never be published looks an object up.</summary>
    public MetricSlot FindSlot(MetricSubject subject, Guid id) =>
        RegistryOf(subject) is var registry && registry.TryGetSlot(id, out var slot) ? new MetricSlot(slot, registry.VersionOf(slot)) : MetricSlot.None;

    /// <summary>Whether <paramref name="slot"/> still belongs to the object it was handed to (not rented to another since).</summary>
    public bool IsCurrent(MetricSubject subject, MetricSlot slot) => !slot.IsNone && RegistryOf(subject).IsCurrent(slot.Index, slot.Version);

    /// <summary>
    /// Makes <paramref name="registrations"/> the live set of every subject: re-asserts each slot (an older publish may have
    /// retired it) and retires the rest. Called by the publisher at its swap, just before the snapshot goes live.
    /// </summary>
    /// <returns>The objects whose slot had gone to another object meanwhile; they get a fresh slot at the next compile.</returns>
    public int Publish(MetricRegistrations registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        var now = NowMs;
        var conflicts = 0;
        foreach (var subject in Enum.GetValues<MetricSubject>())
        {
            conflicts += RegistryOf(subject).Publish(registrations.Of(subject), now);
        }

        return conflicts;
    }

    /// <summary>Counts one finished request. Lock-free, 0 B.</summary>
    public void RecordRequest(in RequestMetrics request)
    {
        var stripe = HostCounters.CurrentStripe();
        var statusClass = RequestMetric.ClassOf(request.Status);
        var outcome = (int)request.Outcome;
        var timed = request.UpstreamMicros >= 0;

        // A request that outlived its object's slot (a WebSocket on a deleted host, say, finishing after the slot went to
        // another host) is dropped, never counted into the new owner: one compare per slot.
        if (!request.Host.IsNone && Hosts.IsCurrent(request.Host.Index, request.Host.Version))
        {
            var host = request.Host.Index;
            HostCounters.Increment(stripe, host, RequestMetric.Status + statusClass);
            HostCounters.Increment(stripe, host, RequestMetric.Outcome + outcome);
            AddBytes(HostCounters, stripe, host, RequestMetric.BytesOut, request.ResponseBytes);
            AddBytes(HostCounters, stripe, host, RequestMetric.BytesIn, request.RequestBytes);

            HostCounters.Add(stripe, host, RequestMetric.TotalMicros, request.TotalMicros);
            HostLatency.Increment(stripe, host, LatencyBuckets.IndexOf(request.TotalMicros));
            if (timed)
            {
                HostCounters.Add(stripe, host, RequestMetric.UpstreamMicros, request.UpstreamMicros);
                HostLatency.Increment(stripe, host, RequestMetric.UpstreamHistogram + LatencyBuckets.IndexOf(request.UpstreamMicros));
            }
        }

        if (!request.Route.IsNone && Routes.IsCurrent(request.Route.Index, request.Route.Version))
        {
            var route = request.Route.Index;
            RouteCounters.Increment(stripe, route, RequestMetric.Status + statusClass);
            RouteCounters.Increment(stripe, route, RequestMetric.Outcome + outcome);
            AddBytes(RouteCounters, stripe, route, RequestMetric.BytesOut, request.ResponseBytes);
            AddBytes(RouteCounters, stripe, route, RequestMetric.BytesIn, request.RequestBytes);
        }

        if (!request.Upstream.IsNone && Upstreams.IsCurrent(request.Upstream.Index, request.Upstream.Version))
        {
            var upstream = request.Upstream.Index;
            UpstreamCounters.Increment(stripe, upstream, UpstreamMetric.Requests);
            AddBytes(UpstreamCounters, stripe, upstream, UpstreamMetric.BytesOut, request.ResponseBytes);
            AddBytes(UpstreamCounters, stripe, upstream, UpstreamMetric.BytesIn, request.RequestBytes);
            if (request.ForwarderError > 0)
            {
                UpstreamCounters.Increment(stripe, upstream, UpstreamMetric.Errors + ForwarderErrors.Clamp(request.ForwarderError));
            }

            if (timed)
            {
                UpstreamCounters.Add(stripe, upstream, UpstreamMetric.UpstreamMicros, request.UpstreamMicros);
                UpstreamLatency.Increment(stripe, upstream, LatencyBuckets.IndexOf(request.UpstreamMicros));
            }
        }

        if (!request.Destination.IsNone && Destinations.IsCurrent(request.Destination.Index, request.Destination.Version))
        {
            var destination = request.Destination.Index;
            DestinationCounters.Increment(stripe, destination, DestinationMetric.Requests);
            if (request.ForwarderError > 0 && ForwarderErrors.IsUpstreamFault((global::Yarp.ReverseProxy.Forwarder.ForwarderError)request.ForwarderError))
            {
                DestinationCounters.Increment(stripe, destination, DestinationMetric.Errors);
            }
        }
    }

    /// <summary>The slot's counters, summed over the stripes, into <paramref name="totals"/> (laid out as <see cref="RequestMetric"/>, <see cref="UpstreamMetric"/> or <see cref="DestinationMetric"/>).</summary>
    public void ReadCounters(MetricSubject subject, int slot, Span<long> totals) => CountersOf(subject).SumInto(slot, totals);

    /// <summary>The slot's latency histograms: a host's total-time and upstream-time (either may be empty), an upstream's upstream-time only.</summary>
    public void ReadLatency(MetricSubject subject, int slot, Span<long> total, Span<long> upstream)
    {
        switch (subject)
        {
            case MetricSubject.Host:
                Span<long> both = stackalloc long[RequestMetric.LatencyStride];
                HostLatency.SumInto(slot, both);
                both[..LatencyBuckets.Stride].CopyTo(total);
                both[RequestMetric.UpstreamHistogram..].CopyTo(upstream);
                break;
            case MetricSubject.Upstream:
                total.Clear();
                UpstreamLatency.SumInto(slot, upstream);
                break;
            default:
                total.Clear();
                upstream.Clear();
                break;
        }
    }

    internal MetricTable CountersOf(MetricSubject subject) => subject switch
    {
        MetricSubject.Host => HostCounters,
        MetricSubject.Route => RouteCounters,
        MetricSubject.Upstream => UpstreamCounters,
        MetricSubject.Destination => DestinationCounters,
        _ => throw new ArgumentOutOfRangeException(nameof(subject), subject, "Undefined metric subject."),
    };

    // Divided first: a monotonic timestamp is in nanoseconds on Linux, and a year of uptime times 1000 would overflow a long.
    private long NowMs => (long)(_time.GetTimestamp() / (double)_time.TimestampFrequency * 1000);

    // Most requests move no body one way (a GET's upload, a 304's download): no atomic for those.
    private static void AddBytes(MetricTable table, int stripe, int slot, int metric, long bytes)
    {
        if (bytes > 0)
        {
            table.Add(stripe, slot, metric, bytes);
        }
    }

    private static void Prepare(int slot, bool reused, params ReadOnlySpan<MetricTable> tables)
    {
        foreach (var table in tables)
        {
            table.Ensure(slot);
            if (reused)
            {
                table.Clear(slot);
            }
        }
    }
}
