using System.Diagnostics.Metrics;

using Paduraksa.Proxy.Snapshot;

namespace Paduraksa.Proxy.Metrics;

/// <summary>
/// The registry's counters as <c>System.Diagnostics.Metrics</c> instruments, so OpenTelemetry (<c>AddMeter("Paduraksa")</c>) and
/// <c>dotnet-counters</c> work with no exporter of ours (PLAN §7.4). Every instrument is observable: its callback reads the
/// striped arrays when a listener asks, so the request path never touches the meter and costs the same with or without one.
/// </summary>
/// <remarks>
/// <para><b>Names and tags are stable.</b> Hosts carry <c>host</c> (the first name) and <c>host_id</c> (the typed ID); a host's
/// slot is its ID's, so a series survives renames and publishes. Upstreams and destinations carry their typed IDs
/// (<c>upstream_id</c>, <c>destination_id</c>), routes <c>route_id</c> and <c>path</c>.</para>
/// <para><b>No histogram instrument.</b> The meter API has no observable histogram, so a latency is a sum
/// (<c>paduraksa.request.duration.sum</c>, seconds) to divide by <c>paduraksa.requests</c>; the buckets are in the Prometheus
/// endpoint (<see cref="PrometheusWriter"/>) and the percentiles in the dashboard.</para>
/// </remarks>
public sealed class PaduraksaMeter : IDisposable
{
    /// <summary>The meter's name, for <c>AddMeter</c>.</summary>
    public const string MeterName = "Paduraksa";

    private readonly Meter _meter;
    private readonly MetricsRegistry _registry;
    private readonly SnapshotStore _snapshots;
    private readonly Lock _inventoryGate = new();
    private RuntimeSnapshot? _inventorySnapshot;
    private MetricInventory? _inventory;

    public PaduraksaMeter(MetricsRegistry registry, SnapshotStore snapshots, ProcessCounters process)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(process);
        _registry = registry;
        _snapshots = snapshots;
        _meter = new Meter(MeterName, "1");

        _meter.CreateObservableCounter("paduraksa.requests", ObserveRequests, "{request}", "Requests answered, by host and the class of the status the client got.");
        _meter.CreateObservableCounter("paduraksa.gate.outcomes", ObserveOutcomes, "{request}", "What the gate did with each request, by host: forwarded, answered itself, or refused by which kind of policy or limit.");
        _meter.CreateObservableCounter("paduraksa.response.size", ObserveBytes, "By", "Bytes of response bodies whose Content-Length is known, by host.");
        _meter.CreateObservableCounter("paduraksa.request.duration.sum", ObserveRequestSeconds, "s", "Total time spent on requests, by host. Divide its increase by the increase of paduraksa.requests for the mean.");
        _meter.CreateObservableCounter("paduraksa.request.upstream.duration.sum", ObserveHostUpstreamSeconds, "s", "Total time spent forwarding and waiting for the upstream, by host; the difference to paduraksa.request.duration.sum is the proxy's own time.");
        _meter.CreateObservableCounter("paduraksa.route.requests", ObserveRouteRequests, "{request}", "Requests answered, by route and status class.");
        _meter.CreateObservableCounter("paduraksa.upstream.requests", ObserveUpstreamRequests, "{request}", "Requests the gate forwarded to the upstream.");
        _meter.CreateObservableCounter("paduraksa.upstream.errors", ObserveUpstreamErrors, "{error}", "Forwards that failed, by YARP's kind of error.");
        _meter.CreateObservableCounter("paduraksa.upstream.duration.sum", ObserveUpstreamSeconds, "s", "Total time spent forwarding to the upstream and waiting for its answer.");
        _meter.CreateObservableCounter("paduraksa.destination.requests", ObserveDestinationRequests, "{request}", "Requests the gate forwarded to the destination.");
        _meter.CreateObservableCounter("paduraksa.destination.errors", ObserveDestinationErrors, "{error}", "Forwards to the destination that failed on its side of the connection.");
        _meter.CreateObservableGauge("paduraksa.config.generation", () => _snapshots.Current.Generation, "{generation}", "The generation of the live config snapshot: it grows with every publish.");
        _meter.CreateObservableGauge("paduraksa.hosts", () => (long)_snapshots.Current.HostsByIndex.Length, "{host}", "Enabled hosts in the live config.");

        // One instrument per family, whose callback reads each counter of it: the families are the names, in the order the list gives them.
        Dictionary<string, List<ProcessCounter>> families = new(StringComparer.Ordinal);
        foreach (var counter in process.All)
        {
            if (!families.TryGetValue(counter.Name, out var family))
            {
                families[counter.Name] = family = [];
            }

            family.Add(counter);
        }

        foreach (var (name, family) in families)
        {
            var counters = family.ToArray();
            if (counters[0].Gauge)
            {
                _meter.CreateObservableGauge(name, () => Observe(counters), "{item}", counters[0].Help);
            }
            else
            {
                _meter.CreateObservableCounter(name, () => Observe(counters), "{event}", counters[0].Help);
            }
        }
    }

    public void Dispose() => _meter.Dispose();

    private static Measurement<long>[] Observe(ProcessCounter[] counters)
    {
        var measurements = new Measurement<long>[counters.Length];
        for (var i = 0; i < counters.Length; i++)
        {
            var counter = counters[i];
            measurements[i] = counter.LabelName is null
                ? new Measurement<long>(counter.Read())
                : new Measurement<long>(counter.Read(), new KeyValuePair<string, object?>(counter.LabelName, counter.LabelValue));
        }

        return measurements;
    }

    private MetricInventory Inventory()
    {
        var snapshot = _snapshots.Current;
        lock (_inventoryGate)
        {
            if (!ReferenceEquals(_inventorySnapshot, snapshot) || _inventory is null)
            {
                _inventory = MetricInventory.Of(snapshot, _registry);
                _inventorySnapshot = snapshot;
            }

            return _inventory;
        }
    }

    private long[] Counters(MetricSubject subject, MetricSlot slot, int stride)
    {
        var totals = new long[stride];
        _registry.ReadCounters(subject, slot.Index, totals);
        return totals;
    }

    private static KeyValuePair<string, object?>[] HostTags(MetricInventory.HostRow host) =>
        [new("host", host.Name), new("host_id", host.Id)];

    private static KeyValuePair<string, object?>[] With(KeyValuePair<string, object?>[] tags, string name, string value) =>
        [.. tags, new(name, value)];

    private static double Seconds(long micros) => micros / 1_000_000.0;

    private IEnumerable<Measurement<long>> ObserveRequests()
    {
        foreach (var host in Inventory().Hosts)
        {
            var counters = Counters(MetricSubject.Host, host.Slot, RequestMetric.Stride);
            for (var c = 0; c < RequestMetric.StatusClasses; c++)
            {
                yield return new(counters[RequestMetric.Status + c], With(HostTags(host), "status_class", MetricNames.StatusClass(c)));
            }
        }
    }

    private IEnumerable<Measurement<long>> ObserveOutcomes()
    {
        foreach (var host in Inventory().Hosts)
        {
            var counters = Counters(MetricSubject.Host, host.Slot, RequestMetric.Stride);
            foreach (var outcome in MetricNames.CountedOutcomes)
            {
                yield return new(counters[RequestMetric.Outcome + (int)outcome], With(HostTags(host), "outcome", MetricNames.Outcome(outcome)));
            }
        }
    }

    private IEnumerable<Measurement<long>> ObserveBytes()
    {
        foreach (var host in Inventory().Hosts)
        {
            yield return new(Counters(MetricSubject.Host, host.Slot, RequestMetric.Stride)[RequestMetric.BytesOut], HostTags(host));
        }
    }

    private IEnumerable<Measurement<double>> ObserveRequestSeconds()
    {
        foreach (var host in Inventory().Hosts)
        {
            yield return new(Seconds(Counters(MetricSubject.Host, host.Slot, RequestMetric.Stride)[RequestMetric.TotalMicros]), HostTags(host));
        }
    }

    private IEnumerable<Measurement<double>> ObserveHostUpstreamSeconds()
    {
        foreach (var host in Inventory().Hosts)
        {
            yield return new(Seconds(Counters(MetricSubject.Host, host.Slot, RequestMetric.Stride)[RequestMetric.UpstreamMicros]), HostTags(host));
        }
    }

    private IEnumerable<Measurement<long>> ObserveRouteRequests()
    {
        foreach (var route in Inventory().Routes)
        {
            var counters = Counters(MetricSubject.Route, route.Slot, RequestMetric.Stride);
            KeyValuePair<string, object?>[] tags = [new("host", route.Host), new("host_id", route.HostId), new("route_id", route.Id), new("path", route.Path)];
            for (var c = 0; c < RequestMetric.StatusClasses; c++)
            {
                yield return new(counters[RequestMetric.Status + c], With(tags, "status_class", MetricNames.StatusClass(c)));
            }
        }
    }

    private IEnumerable<Measurement<long>> ObserveUpstreamRequests()
    {
        foreach (var upstream in Inventory().Upstreams)
        {
            yield return new(Counters(MetricSubject.Upstream, upstream.Slot, UpstreamMetric.Stride)[UpstreamMetric.Requests], new KeyValuePair<string, object?>("upstream_id", upstream.Id));
        }
    }

    private IEnumerable<Measurement<long>> ObserveUpstreamErrors()
    {
        foreach (var upstream in Inventory().Upstreams)
        {
            var counters = Counters(MetricSubject.Upstream, upstream.Slot, UpstreamMetric.Stride);
            for (var error = 1; error < UpstreamMetric.ErrorKinds; error++)
            {
                if (counters[UpstreamMetric.Errors + error] > 0)
                {
                    yield return new(counters[UpstreamMetric.Errors + error], new KeyValuePair<string, object?>("upstream_id", upstream.Id), new KeyValuePair<string, object?>("kind", ForwarderErrors.NameOf(error)));
                }
            }
        }
    }

    private IEnumerable<Measurement<double>> ObserveUpstreamSeconds()
    {
        foreach (var upstream in Inventory().Upstreams)
        {
            yield return new(Seconds(Counters(MetricSubject.Upstream, upstream.Slot, UpstreamMetric.Stride)[UpstreamMetric.UpstreamMicros]), new KeyValuePair<string, object?>("upstream_id", upstream.Id));
        }
    }

    private IEnumerable<Measurement<long>> ObserveDestinationRequests()
    {
        foreach (var destination in Inventory().Destinations)
        {
            yield return new(Counters(MetricSubject.Destination, destination.Slot, DestinationMetric.Stride)[DestinationMetric.Requests], new KeyValuePair<string, object?>("upstream_id", destination.UpstreamId), new KeyValuePair<string, object?>("destination_id", destination.Id));
        }
    }

    private IEnumerable<Measurement<long>> ObserveDestinationErrors()
    {
        foreach (var destination in Inventory().Destinations)
        {
            yield return new(Counters(MetricSubject.Destination, destination.Slot, DestinationMetric.Stride)[DestinationMetric.Errors], new KeyValuePair<string, object?>("upstream_id", destination.UpstreamId), new KeyValuePair<string, object?>("destination_id", destination.Id));
        }
    }
}
