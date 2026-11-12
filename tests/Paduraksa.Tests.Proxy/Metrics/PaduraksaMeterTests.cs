using System.Diagnostics.Metrics;

using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Metrics;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.Metrics;

public sealed class PaduraksaMeterTests
{
    private sealed record Observed(string Instrument, double Value, Dictionary<string, string?> Tags);

    private static List<Observed> Collect(PaduraksaMeter meter, params string[] instruments)
    {
        List<Observed> observed = [];
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PaduraksaMeter.MeterName && (instruments.Length == 0 || instruments.Contains(instrument.Name)))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => observed.Add(new(instrument.Name, value, ToDictionary(tags))));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => observed.Add(new(instrument.Name, value, ToDictionary(tags))));
        listener.Start();
        listener.RecordObservableInstruments();
        _ = meter;
        return observed;
    }

    private static Dictionary<string, string?> ToDictionary(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        Dictionary<string, string?> map = [];
        foreach (var tag in tags)
        {
            map[tag.Key] = tag.Value?.ToString();
        }

        return map;
    }

    private static (MetricsRegistry Registry, SnapshotStore Store, RuntimeSnapshot Snapshot) Live()
    {
        MetricsRegistry registry = new(processors: 2);
        var snapshot = Bound(
            new ProxyConfigModel
            {
                Upstreams = [Upstream("web", "http://10.0.0.1:80/")],
                Hosts = [ProxyHost("app", "app.test", Route("app-root", "web"))],
            },
            options: new SnapshotCompilerOptions { Metrics = registry });
        registry.Publish(snapshot.MetricRegistrations);
        SnapshotStore store = new();
        store.Publish(snapshot);
        return (registry, store, snapshot);
    }

    [Fact]
    public void TheInstrumentsReadTheCountersWithTheHostAndItsIdAsTags()
    {
        var (registry, store, snapshot) = Live();
        var host = snapshot.HostsByIndex[0];
        registry.RecordRequest(new RequestMetrics { Host = host.MetricSlot, Route = host.Routes[0].MetricSlot, Status = 200, Outcome = GateOutcome.Forwarded, TotalMicros = 2_000_000, ResponseBytes = 10 });
        registry.RecordRequest(new RequestMetrics { Host = host.MetricSlot, Status = 404, Outcome = GateOutcome.NoRoute, TotalMicros = 1_000_000 });
        using PaduraksaMeter meter = new(registry, store, ProcessCounters.None);

        var observed = Collect(meter);

        var ok = Assert.Single(observed, o => o.Instrument == "paduraksa.requests" && o.Tags["host"] == "app.test" && o.Tags["status_class"] == "2xx");
        Assert.Equal(1, ok.Value);
        Assert.Equal(TestIds.Host("app").ToString(), ok.Tags["host_id"]);
        Assert.Equal(1, Assert.Single(observed, o => o.Instrument == "paduraksa.requests" && o.Tags["host"] == "app.test" && o.Tags["status_class"] == "4xx").Value);
        Assert.Equal(1, Assert.Single(observed, o => o.Instrument == "paduraksa.gate.outcomes" && o.Tags["host"] == "app.test" && o.Tags["outcome"] == "no_route").Value);
        Assert.Equal(10, Assert.Single(observed, o => o.Instrument == "paduraksa.response.size" && o.Tags["host"] == "app.test").Value);
        Assert.Equal(3, Assert.Single(observed, o => o.Instrument == "paduraksa.request.duration.sum" && o.Tags["host"] == "app.test").Value);
        Assert.Equal(1, Assert.Single(observed, o => o.Instrument == "paduraksa.config.generation").Value);
        Assert.Equal(1, Assert.Single(observed, o => o.Instrument == "paduraksa.hosts").Value);
        Assert.Contains(observed, o => o.Instrument == "paduraksa.requests" && o.Tags["host"] == MetricInventoryNames.Unmatched);
    }

    [Fact]
    public void UpstreamsAndDestinationsAreTaggedWithTheirIds()
    {
        var (registry, store, snapshot) = Live();
        var route = snapshot.HostsByIndex[0].Routes[0];
        registry.RecordRequest(new RequestMetrics
        {
            Host = snapshot.HostsByIndex[0].MetricSlot,
            Upstream = route.Upstream!.Slot,
            Destination = route.Upstream.DestinationSlots[0],
            Status = 502,
            Outcome = GateOutcome.Forwarded,
            UpstreamMicros = 500,
            ForwarderError = 10,
        });
        using PaduraksaMeter meter = new(registry, store, ProcessCounters.None);

        var observed = Collect(meter);

        var upstream = TestIds.Upstream("web").ToString();
        Assert.Equal(1, Assert.Single(observed, o => o.Instrument == "paduraksa.upstream.requests" && o.Tags["upstream_id"] == upstream).Value);
        Assert.Equal(1, Assert.Single(observed, o => o.Instrument == "paduraksa.upstream.errors" && o.Tags["upstream_id"] == upstream && o.Tags["kind"] == "response_body_destination").Value);
        Assert.Equal(1, Assert.Single(observed, o => o.Instrument == "paduraksa.destination.errors" && o.Tags["destination_id"] == TestIds.Destination("web-d0").ToString()).Value);

        // The sums read the same way for the host's and the upstream's share of the time (500 microseconds here).
        Assert.Equal(0.0005, Assert.Single(observed, o => o.Instrument == "paduraksa.request.upstream.duration.sum" && o.Tags["host"] == "app.test").Value, 6);
        Assert.Equal(0.0005, Assert.Single(observed, o => o.Instrument == "paduraksa.upstream.duration.sum" && o.Tags["upstream_id"] == upstream).Value, 6);
    }

    [Fact]
    public void TheProcessCountersAreInstrumentsToo()
    {
        var (registry, store, _) = Live();
        using PaduraksaMeter meter = new(
            registry, store,
            new ProcessCounters([
                new("paduraksa.rate_limit.decisions", "Rate-limit decisions.", "result", "allowed", () => 7),
                new("paduraksa.rate_limit.decisions", "Rate-limit decisions.", "result", "limited", () => 3),
                new("paduraksa.lone", "A counter with no label.", null, null, () => 11),
                new("paduraksa.depth", "A level.", null, null, () => 5, Gauge: true),
            ]));

        var observed = Collect(meter);

        Assert.Equal(7, Assert.Single(observed, o => o.Instrument == "paduraksa.rate_limit.decisions" && o.Tags["result"] == "allowed").Value);
        Assert.Equal(3, Assert.Single(observed, o => o.Instrument == "paduraksa.rate_limit.decisions" && o.Tags["result"] == "limited").Value);
        Assert.Equal(11, Assert.Single(observed, o => o.Instrument == "paduraksa.lone").Value);
        Assert.Equal(5, Assert.Single(observed, o => o.Instrument == "paduraksa.depth").Value);
    }


    private static class MetricInventoryNames
    {
        public const string Unmatched = "(unmatched)";
    }
}
