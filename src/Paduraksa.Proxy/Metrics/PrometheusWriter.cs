using System.Globalization;

using Paduraksa.Proxy.Snapshot;

namespace Paduraksa.Proxy.Metrics;

/// <summary>
/// The Prometheus text format (0.0.4) straight from the <see cref="MetricsRegistry"/>: no exporter package, real histograms
/// (<c>_bucket</c>, <c>_sum</c>, <c>_count</c>) and counters that continue across publishes because they live in slots, not
/// in snapshots. Labelled from the live snapshot, so a deleted host's series ends with it.
/// </summary>
/// <remarks>
/// <para><b>Histogram buckets.</b> The registry's 101 log-linear buckets are reported as the cumulative counts at the octave
/// boundaries, labelled <c>le="0.000015"</c> to <c>le="67.108863"</c> (2^4 - 1 µs to 2^26 - 1 µs): Prometheus's <c>le</c> means "at
/// most", and a latency is a whole number of microseconds, so the count of observations below 2^e µs, which is a bucket edge and so
/// exact, is the count of those at most 2^e - 1.</para>
/// <para><b>Escaping.</b> A label value is escaped per the format (<c>\\</c>, <c>\"</c>, <c>\n</c>), so a host name that tries to
/// close the label, start a new line or inject a series stays one label value. Host names are validated DNS names or IP literals
/// when the config is written; this is the second line of defence, and the only one for a name a config document could ever carry.</para>
/// <para><b>Consistency.</b> Each counter is read when its line is written; a histogram's sum and count may differ from the
/// buckets by the requests that finished in between. A scrape is a read, not a snapshot of the process.</para>
/// </remarks>
public static class PrometheusWriter
{
    /// <summary>The media type of the text format.</summary>
    public const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

    /// <summary>The smallest octave reported as a bucket boundary: 2^4 µs is 16 µs.</summary>
    private const int FirstBucketOctave = 4;

    public static void Write(TextWriter output, MetricsRegistry registry, RuntimeSnapshot snapshot, ProcessCounters process)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(process);
        var inventory = MetricInventory.Of(snapshot, registry);
        WriteHosts(output, registry, inventory);
        WriteRoutes(output, registry, inventory);
        WriteUpstreams(output, registry, inventory);
        WriteDestinations(output, registry, inventory);
        WriteProcess(output, process);
        WriteGauge(output, "paduraksa_config_generation", "The generation of the live config snapshot: it grows with every publish.", snapshot.Generation);
        WriteGauge(output, "paduraksa_hosts", "Enabled hosts in the live config.", snapshot.HostsByIndex.Length);
    }

    /// <summary>A label value as the format wants it inside quotes.</summary>
    internal static string Escape(string value)
    {
        if (value.AsSpan().IndexOfAny('\\', '"', '\n') < 0)
        {
            return value;
        }

        return value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
    }

    private static void WriteHosts(TextWriter output, MetricsRegistry registry, MetricInventory inventory)
    {
        var rows = inventory.Hosts;
        var hostCounters = new long[rows.Length][];
        for (var i = 0; i < rows.Length; i++)
        {
            hostCounters[i] = new long[RequestMetric.Stride];
            registry.ReadCounters(MetricSubject.Host, rows[i].Slot.Index, hostCounters[i]);
        }

        Header(output, "paduraksa_requests_total", "Requests answered, by host and the class of the status the client got (none: the connection was dropped).", "counter");
        for (var i = 0; i < rows.Length; i++)
        {
            for (var c = 0; c < RequestMetric.StatusClasses; c++)
            {
                Sample(output, "paduraksa_requests_total", [("host", rows[i].Name), ("host_id", rows[i].Id), ("status_class", MetricNames.StatusClass(c))], hostCounters[i][RequestMetric.Status + c]);
            }
        }

        Header(output, "paduraksa_gate_outcomes_total", "What the gate did with each request: forwarded, answered itself, or refused by which kind of policy or limit.", "counter");
        for (var i = 0; i < rows.Length; i++)
        {
            foreach (var outcome in MetricNames.CountedOutcomes)
            {
                Sample(output, "paduraksa_gate_outcomes_total", [("host", rows[i].Name), ("host_id", rows[i].Id), ("outcome", MetricNames.Outcome(outcome))], hostCounters[i][RequestMetric.Outcome + (int)outcome]);
            }
        }

        Header(output, "paduraksa_response_size_bytes_total", "Response body bytes written to clients, as they went out (an upstream's compression counts compressed), by host.", "counter");
        for (var i = 0; i < rows.Length; i++)
        {
            Sample(output, "paduraksa_response_size_bytes_total", [("host", rows[i].Name), ("host_id", rows[i].Id)], hostCounters[i][RequestMetric.BytesOut]);
        }

        Header(output, "paduraksa_request_size_bytes_total", "Request body bytes read from clients, by host.", "counter");
        for (var i = 0; i < rows.Length; i++)
        {
            Sample(output, "paduraksa_request_size_bytes_total", [("host", rows[i].Name), ("host_id", rows[i].Id)], hostCounters[i][RequestMetric.BytesIn]);
        }

        var total = new long[rows.Length][];
        var upstream = new long[rows.Length][];
        for (var i = 0; i < rows.Length; i++)
        {
            total[i] = new long[LatencyBuckets.Stride];
            upstream[i] = new long[LatencyBuckets.Stride];
            registry.ReadLatency(MetricSubject.Host, rows[i].Slot.Index, total[i], upstream[i]);
        }

        Header(output, "paduraksa_request_duration_seconds", "Time from the first byte of the request to the end of the response, by host.", "histogram");
        for (var i = 0; i < rows.Length; i++)
        {
            Histogram(output, "paduraksa_request_duration_seconds", [("host", rows[i].Name), ("host_id", rows[i].Id)], total[i], hostCounters[i][RequestMetric.TotalMicros]);
        }

        Header(output, "paduraksa_host_upstream_duration_seconds", "Time spent forwarding to the upstream and waiting for its answer, by host; the request duration minus this is the proxy's own time.", "histogram");
        for (var i = 0; i < rows.Length; i++)
        {
            Histogram(output, "paduraksa_host_upstream_duration_seconds", [("host", rows[i].Name), ("host_id", rows[i].Id)], upstream[i], hostCounters[i][RequestMetric.UpstreamMicros]);
        }
    }

    private static void WriteRoutes(TextWriter output, MetricsRegistry registry, MetricInventory inventory)
    {
        var rows = inventory.Routes;
        var counters = new long[rows.Length][];
        for (var i = 0; i < rows.Length; i++)
        {
            counters[i] = new long[RequestMetric.Stride];
            registry.ReadCounters(MetricSubject.Route, rows[i].Slot.Index, counters[i]);
        }

        Header(output, "paduraksa_route_requests_total", "Requests answered, by route and the class of the status the client got.", "counter");
        for (var i = 0; i < rows.Length; i++)
        {
            for (var c = 0; c < RequestMetric.StatusClasses; c++)
            {
                Sample(output, "paduraksa_route_requests_total", RouteLabels(rows[i], ("status_class", MetricNames.StatusClass(c))), counters[i][RequestMetric.Status + c]);
            }
        }

        Header(output, "paduraksa_route_gate_outcomes_total", "What the gate did with each request that matched the route.", "counter");
        for (var i = 0; i < rows.Length; i++)
        {
            foreach (var outcome in MetricNames.CountedOutcomes)
            {
                Sample(output, "paduraksa_route_gate_outcomes_total", RouteLabels(rows[i], ("outcome", MetricNames.Outcome(outcome))), counters[i][RequestMetric.Outcome + (int)outcome]);
            }
        }

        Header(output, "paduraksa_route_response_size_bytes_total", "Response body bytes written to clients, by route.", "counter");
        for (var i = 0; i < rows.Length; i++)
        {
            Sample(output, "paduraksa_route_response_size_bytes_total", RouteLabels(rows[i]), counters[i][RequestMetric.BytesOut]);
        }

        Header(output, "paduraksa_route_request_size_bytes_total", "Request body bytes read from clients, by route.", "counter");
        for (var i = 0; i < rows.Length; i++)
        {
            Sample(output, "paduraksa_route_request_size_bytes_total", RouteLabels(rows[i]), counters[i][RequestMetric.BytesIn]);
        }
    }

    private static (string, string)[] RouteLabels(MetricInventory.RouteRow route, (string, string)? extra = null) =>
        extra is { } more
            ? [("host", route.Host), ("host_id", route.HostId), ("route_id", route.Id), ("path", route.Path), more]
            : [("host", route.Host), ("host_id", route.HostId), ("route_id", route.Id), ("path", route.Path)];

    private static void WriteUpstreams(TextWriter output, MetricsRegistry registry, MetricInventory inventory)
    {
        var rows = inventory.Upstreams;
        var counters = new long[rows.Length][];
        var latency = new long[rows.Length][];
        for (var i = 0; i < rows.Length; i++)
        {
            counters[i] = new long[UpstreamMetric.Stride];
            registry.ReadCounters(MetricSubject.Upstream, rows[i].Slot.Index, counters[i]);
            latency[i] = new long[LatencyBuckets.Stride];
            registry.ReadLatency(MetricSubject.Upstream, rows[i].Slot.Index, new long[LatencyBuckets.Stride], latency[i]);
        }

        Header(output, "paduraksa_upstream_requests_total", "Requests the gate forwarded to the upstream.", "counter");
        for (var i = 0; i < rows.Length; i++)
        {
            Sample(output, "paduraksa_upstream_requests_total", [("upstream_id", rows[i].Id)], counters[i][UpstreamMetric.Requests]);
        }

        Header(output, "paduraksa_upstream_response_size_bytes_total", "Response body bytes written to clients for the requests forwarded to the upstream.", "counter");
        for (var i = 0; i < rows.Length; i++)
        {
            Sample(output, "paduraksa_upstream_response_size_bytes_total", [("upstream_id", rows[i].Id)], counters[i][UpstreamMetric.BytesOut]);
        }

        Header(output, "paduraksa_upstream_request_size_bytes_total", "Request body bytes read from clients for the requests forwarded to the upstream.", "counter");
        for (var i = 0; i < rows.Length; i++)
        {
            Sample(output, "paduraksa_upstream_request_size_bytes_total", [("upstream_id", rows[i].Id)], counters[i][UpstreamMetric.BytesIn]);
        }

        Header(output, "paduraksa_upstream_errors_total", "Forwards that failed, by YARP's kind of error: a client that went away is counted too, see paduraksa_upstream_requests_total for the total.", "counter");
        for (var i = 0; i < rows.Length; i++)
        {
            for (var error = 1; error < UpstreamMetric.ErrorKinds; error++)
            {
                var count = counters[i][UpstreamMetric.Errors + error];
                if (count > 0)
                {
                    Sample(output, "paduraksa_upstream_errors_total", [("upstream_id", rows[i].Id), ("kind", ForwarderErrors.NameOf(error))], count);
                }
            }
        }

        Header(output, "paduraksa_upstream_duration_seconds", "Time spent forwarding to the upstream and waiting for its answer.", "histogram");
        for (var i = 0; i < rows.Length; i++)
        {
            Histogram(output, "paduraksa_upstream_duration_seconds", [("upstream_id", rows[i].Id)], latency[i], counters[i][UpstreamMetric.UpstreamMicros]);
        }
    }

    private static void WriteDestinations(TextWriter output, MetricsRegistry registry, MetricInventory inventory)
    {
        var rows = inventory.Destinations;
        var counters = new long[rows.Length][];
        for (var i = 0; i < rows.Length; i++)
        {
            counters[i] = new long[DestinationMetric.Stride];
            registry.ReadCounters(MetricSubject.Destination, rows[i].Slot.Index, counters[i]);
        }

        Header(output, "paduraksa_destination_requests_total", "Requests the gate forwarded to the destination.", "counter");
        for (var i = 0; i < rows.Length; i++)
        {
            Sample(output, "paduraksa_destination_requests_total", [("upstream_id", rows[i].UpstreamId), ("destination_id", rows[i].Id)], counters[i][DestinationMetric.Requests]);
        }

        Header(output, "paduraksa_destination_errors_total", "Forwards to the destination that failed on its side of the connection (a refused or timed-out connection, a broken response).", "counter");
        for (var i = 0; i < rows.Length; i++)
        {
            Sample(output, "paduraksa_destination_errors_total", [("upstream_id", rows[i].UpstreamId), ("destination_id", rows[i].Id)], counters[i][DestinationMetric.Errors]);
        }
    }

    private static void WriteProcess(TextWriter output, ProcessCounters process)
    {
        string? family = null;
        foreach (var counter in process.All)
        {
            var name = counter.Name.Replace('.', '_') + (counter.Gauge ? string.Empty : "_total");
            if (!string.Equals(family, name, StringComparison.Ordinal))
            {
                Header(output, name, counter.Help, counter.Gauge ? "gauge" : "counter");
                family = name;
            }

            Sample(output, name, counter.LabelName is null ? [] : [(counter.LabelName, counter.LabelValue ?? string.Empty)], counter.Read());
        }
    }

    private static void WriteGauge(TextWriter output, string name, string help, long value)
    {
        Header(output, name, help, "gauge");
        Sample(output, name, [], value);
    }

    private static void Header(TextWriter output, string name, string help, string type)
    {
        output.Write("# HELP ");
        output.Write(name);
        output.Write(' ');
        output.Write(help.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal));
        output.Write('\n');
        output.Write("# TYPE ");
        output.Write(name);
        output.Write(' ');
        output.Write(type);
        output.Write('\n');
    }

    private static void Sample(TextWriter output, string name, ReadOnlySpan<(string Name, string Value)> labels, long value)
    {
        output.Write(name);
        WriteLabels(output, labels, extra: null);
        output.Write(' ');
        output.Write(value.ToString(CultureInfo.InvariantCulture));
        output.Write('\n');
    }

    private static void Histogram(TextWriter output, string name, ReadOnlySpan<(string Name, string Value)> labels, ReadOnlySpan<long> counts, long sumMicros)
    {
        for (var octave = FirstBucketOctave; octave <= LatencyBuckets.OverflowOctave; octave++)
        {
            output.Write(name);
            output.Write("_bucket");
            WriteLabels(output, labels, ("le", Seconds((1L << octave) - 1)));
            output.Write(' ');
            output.Write(LatencyBuckets.CountBelowOctave(counts, octave).ToString(CultureInfo.InvariantCulture));
            output.Write('\n');
        }

        long count = 0;
        foreach (var bucket in counts)
        {
            count += bucket;
        }

        output.Write(name);
        output.Write("_bucket");
        WriteLabels(output, labels, ("le", "+Inf"));
        output.Write(' ');
        output.Write(count.ToString(CultureInfo.InvariantCulture));
        output.Write('\n');
        output.Write(name);
        output.Write("_sum");
        WriteLabels(output, labels, extra: null);
        output.Write(' ');
        output.Write(Seconds(sumMicros));
        output.Write('\n');
        output.Write(name);
        output.Write("_count");
        WriteLabels(output, labels, extra: null);
        output.Write(' ');
        output.Write(count.ToString(CultureInfo.InvariantCulture));
        output.Write('\n');
    }

    private static void WriteLabels(TextWriter output, ReadOnlySpan<(string Name, string Value)> labels, (string Name, string Value)? extra)
    {
        if (labels.Length == 0 && extra is null)
        {
            return;
        }

        output.Write('{');
        var first = true;
        foreach (var (name, value) in labels)
        {
            Label(output, name, value, ref first);
        }

        if (extra is { } last)
        {
            Label(output, last.Name, last.Value, ref first);
        }

        output.Write('}');
    }

    private static void Label(TextWriter output, string name, string value, ref bool first)
    {
        if (!first)
        {
            output.Write(',');
        }

        first = false;
        output.Write(name);
        output.Write("=\"");
        output.Write(Escape(value));
        output.Write('"');
    }

    /// <summary>Whole microseconds as seconds, to the microsecond: <c>16 → 0.000016</c>.</summary>
    private static string Seconds(long micros) => (micros / 1_000_000.0).ToString("0.######", CultureInfo.InvariantCulture);
}
