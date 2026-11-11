using System.Globalization;
using System.Text.RegularExpressions;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Metrics;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Yarp;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.Metrics;

public sealed partial class PrometheusWriterTests
{
    /// <summary>One sample line of the text format: a name, optional labels with escaped quoted values, a number.</summary>
    [GeneratedRegex("""^([a-zA-Z_:][a-zA-Z0-9_:]*)(\{([a-zA-Z_][a-zA-Z0-9_]*="(?:[^"\\\n]|\\["\\n])*"(?:,[a-zA-Z_][a-zA-Z0-9_]*="(?:[^"\\\n]|\\["\\n])*")*)?\})? (-?[0-9]+(?:\.[0-9]+)?|\+Inf)$""")]
    private static partial Regex SampleLine();

    private static (MetricsRegistry Registry, RuntimeSnapshot Snapshot) Live(params HostSpec[] hosts)
    {
        MetricsRegistry registry = new(processors: 2);
        var snapshot = Bound(
            new ProxyConfigModel { Upstreams = [Upstream("web", "http://10.0.0.1:80/")], Hosts = hosts },
            options: new SnapshotCompilerOptions { Metrics = registry });
        registry.Publish(snapshot.MetricRegistrations);
        return (registry, snapshot);
    }

    private static string Scrape(MetricsRegistry registry, RuntimeSnapshot snapshot, ProcessCounters? process = null)
    {
        using StringWriter writer = new(CultureInfo.InvariantCulture);
        PrometheusWriter.Write(writer, registry, snapshot, process ?? ProcessCounters.None);
        return writer.ToString();
    }

    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static void Record(MetricsRegistry registry, CompiledHost host, int status, GateOutcome outcome, long totalMicros, long upstreamMicros = -1, long bytes = -1)
    {
        var route = host.Routes[0];
        registry.RecordRequest(new RequestMetrics
        {
            Host = host.MetricSlot,
            Route = route.MetricSlot,
            Upstream = route.Upstream!.Slot,
            Destination = route.Upstream.DestinationSlot(route.Upstream.DestinationKeys[0]),
            Status = status,
            Outcome = outcome,
            ResponseBytes = bytes,
            TotalMicros = totalMicros,
            UpstreamMicros = upstreamMicros,
        });
    }

    [Fact]
    public void EveryLineIsACommentOrASampleOfTheFormat()
    {
        var (registry, snapshot) = Live(ProxyHost("app", "app.test", Route("app-root", "web")));
        Record(registry, snapshot.HostsByIndex[0], 200, GateOutcome.Forwarded, 1_500, 1_000, bytes: 42);

        var lines = Lines(Scrape(registry, snapshot, ProcessCounters.Create(new(), new(), new())));

        Assert.NotEmpty(lines);
        foreach (var line in lines)
        {
            Assert.True(line.StartsWith("# HELP ", StringComparison.Ordinal) || line.StartsWith("# TYPE ", StringComparison.Ordinal) || SampleLine().IsMatch(line), $"not a valid line: {line}");
        }
    }

    [Fact]
    public void CountersCarryTheHostItsIdAndTheStatusClassOrOutcome()
    {
        var (registry, snapshot) = Live(ProxyHost("app", "app.test", Route("app-root", "web")));
        var host = snapshot.HostsByIndex[0];
        Record(registry, host, 200, GateOutcome.Forwarded, 1_500, 1_000, bytes: 42);
        Record(registry, host, 200, GateOutcome.Forwarded, 1_500, 1_000, bytes: 8);
        Record(registry, host, 503, GateOutcome.Forwarded, 300);
        var id = TestIds.Host("app").ToString();

        var text = Scrape(registry, snapshot);

        Assert.Contains($"paduraksa_requests_total{{host=\"app.test\",host_id=\"{id}\",status_class=\"2xx\"}} 2\n", text, StringComparison.Ordinal);
        Assert.Contains($"paduraksa_requests_total{{host=\"app.test\",host_id=\"{id}\",status_class=\"5xx\"}} 1\n", text, StringComparison.Ordinal);
        Assert.Contains($"paduraksa_requests_total{{host=\"app.test\",host_id=\"{id}\",status_class=\"4xx\"}} 0\n", text, StringComparison.Ordinal);
        Assert.Contains($"paduraksa_gate_outcomes_total{{host=\"app.test\",host_id=\"{id}\",outcome=\"forwarded\"}} 3\n", text, StringComparison.Ordinal);
        Assert.Contains($"paduraksa_response_size_bytes_total{{host=\"app.test\",host_id=\"{id}\"}} 50\n", text, StringComparison.Ordinal);
        Assert.Contains($"paduraksa_upstream_requests_total{{upstream_id=\"{TestIds.Upstream("web")}\"}} 3\n", text, StringComparison.Ordinal);
        Assert.Contains($"paduraksa_destination_requests_total{{upstream_id=\"{TestIds.Upstream("web")}\",destination_id=\"{TestIds.Destination("web-d0")}\"}} 3\n", text, StringComparison.Ordinal);
        Assert.Contains("paduraksa_config_generation 1\n", text, StringComparison.Ordinal);
        Assert.Contains("paduraksa_hosts 1\n", text, StringComparison.Ordinal);
        Assert.Contains("host=\"(unmatched)\",host_id=\"\",status_class=\"4xx\"} 0\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AHistogramIsCumulativeEndsAtInfAndItsSumIsInSeconds()
    {
        var (registry, snapshot) = Live(ProxyHost("app", "app.test", Route("app-root", "web")));
        var host = snapshot.HostsByIndex[0];
        Record(registry, host, 200, GateOutcome.Forwarded, 100, 50);
        Record(registry, host, 200, GateOutcome.Forwarded, 1_000, 500);
        Record(registry, host, 200, GateOutcome.Forwarded, 2_000_000, 1_000_000);
        Record(registry, host, 200, GateOutcome.Forwarded, 100_000_000, 99_000_000);

        var text = Scrape(registry, snapshot);

        var buckets = Lines(text)
            .Where(line => line.StartsWith("paduraksa_request_duration_seconds_bucket{host=\"app.test\"", StringComparison.Ordinal))
            .Select(line => (Le: Regex.Match(line, "le=\"([^\"]+)\"").Groups[1].Value, Count: long.Parse(line[(line.LastIndexOf(' ') + 1)..], CultureInfo.InvariantCulture)))
            .ToArray();
        Assert.Equal("+Inf", buckets[^1].Le);
        Assert.Equal(4, buckets[^1].Count);
        Assert.Equal("0.000015", buckets[0].Le);
        Assert.Equal(0, buckets[0].Count);
        Assert.Equal(buckets.Select(b => b.Count).Order(), buckets.Select(b => b.Count));
        Assert.Equal("67.108863", buckets[^2].Le);
        Assert.Equal(3, buckets[^2].Count);
        Assert.Equal(1, buckets.Single(b => b.Le == "0.000127").Count);
        Assert.Equal(2, buckets.Single(b => b.Le == "0.001023").Count);
        Assert.Contains("paduraksa_request_duration_seconds_count{host=\"app.test\",host_id=\"" + TestIds.Host("app") + "\"} 4\n", text, StringComparison.Ordinal);
        Assert.Contains("paduraksa_request_duration_seconds_sum{host=\"app.test\",host_id=\"" + TestIds.Host("app") + "\"} 102.0011\n", text, StringComparison.Ordinal);
        Assert.Contains("paduraksa_host_upstream_duration_seconds_count{host=\"app.test\"", text, StringComparison.Ordinal);
        Assert.Contains("paduraksa_upstream_duration_seconds_count{upstream_id=\"" + TestIds.Upstream("web") + "\"} 4\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnObservationExactlyOnABoundaryIsCountedAtThatBoundaryAndNotBelow()
    {
        var (registry, snapshot) = Live(ProxyHost("app", "app.test", Route("app-root", "web")));
        var host = snapshot.HostsByIndex[0];
        Record(registry, host, 200, GateOutcome.Forwarded, 15);
        Record(registry, host, 200, GateOutcome.Forwarded, 16);

        var text = Scrape(registry, snapshot);

        long Count(string le) => long.Parse(
            Lines(text).Single(l => l.StartsWith("paduraksa_request_duration_seconds_bucket{host=\"app.test\"", StringComparison.Ordinal) && l.Contains($"le=\"{le}\"", StringComparison.Ordinal))
                .Split(' ')[^1],
            CultureInfo.InvariantCulture);
        // 15 us is at most le=0.000015; 16 us is not, and first appears in the next bucket.
        Assert.Equal(1, Count("0.000015"));
        Assert.Equal(2, Count("0.000031"));
    }

    [Fact]
    public void AHostNameThatTriesToBreakOutOfItsLabelStaysOneLabelValue()
    {
        MetricsRegistry registry = new(processors: 1);
        var slot = registry.SlotFor(MetricSubject.Host, TestIds.Host("evil").Value);
        const string Hostile = "evil\"} 1\n# TYPE injected counter\nfake_total{a=\"\\";
        CompiledHost host = new()
        {
            Index = 0,
            Id = TestIds.Host("evil"),
            Kind = HostKind.Proxy,
            Names = [Hostile],
            Routes = [],
            Policies = [],
            MetricSlot = slot,
        };
        var snapshot = new RuntimeSnapshot
        {
            Generation = 1,
            Revision = ConfigRevisionId.Empty,
            Hosts = HostTable.Empty,
            HostsByIndex = [host],
            Yarp = YarpConfig.Empty,
            RouteIdBySlot = [],
            YarpEndpoints = [],
            Default = DefaultHostBehaviour.NotFound(),
        };
        registry.RecordRequest(new RequestMetrics { Host = slot, Status = 200, Outcome = GateOutcome.Forwarded, TotalMicros = 5 });

        var text = Scrape(registry, snapshot);

        // The name is escaped, so it is one label value and adds no line.
        Assert.Contains("host=\"evil\\\"} 1\\n# TYPE injected counter\\nfake_total{a=\\\"\\\\\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n# TYPE injected", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\nfake_total", text, StringComparison.Ordinal);
        foreach (var line in Lines(text))
        {
            Assert.True(line.StartsWith("# HELP ", StringComparison.Ordinal) || line.StartsWith("# TYPE ", StringComparison.Ordinal) || SampleLine().IsMatch(line), $"not a valid line: {line}");
        }

        Assert.Equal(1, Lines(text).Count(line => line.StartsWith("paduraksa_response_size_bytes_total{host=\"evil", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a\\b", "a\\\\b")]
    [InlineData("a\"b", "a\\\"b")]
    [InlineData("a\nb", "a\\nb")]
    [InlineData("\\\"\n", "\\\\\\\"\\n")]
    public void LabelValuesAreEscapedAsTheFormatSays(string value, string expected) => Assert.Equal(expected, PrometheusWriter.Escape(value));

    [Fact]
    public void EachFamilyHasOneHelpAndOneTypeLine()
    {
        var (registry, snapshot) = Live(ProxyHost("a", "a.test", Route("a-root", "web")), ProxyHost("b", "b.test", Route("b-root", "web")));

        var lines = Lines(Scrape(registry, snapshot, ProcessCounters.Create(new(), new(), new())));

        var types = lines.Where(l => l.StartsWith("# TYPE ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(types.Length, types.Distinct().Count());
        Assert.Equal(lines.Count(l => l.StartsWith("# HELP ", StringComparison.Ordinal)), types.Length);
        Assert.Contains("# TYPE paduraksa_rate_limit_decisions_total counter", types);
        Assert.Contains("# TYPE paduraksa_request_duration_seconds histogram", types);
        Assert.Contains(lines, l => l.StartsWith("paduraksa_rate_limit_decisions_total{result=\"limited\"} ", StringComparison.Ordinal));
    }

    [Fact]
    public void OnlyWhatTheLiveSnapshotHoldsIsReported()
    {
        var (registry, snapshot) = Live(ProxyHost("a", "a.test", Route("a-root", "web")), ProxyHost("b", "b.test", Route("b-root", "web")));
        var only = Bound(
            new ProxyConfigModel { Upstreams = [Upstream("web", "http://10.0.0.1:80/")], Hosts = [ProxyHost("a", "a.test", Route("a-root", "web"))] },
            options: new SnapshotCompilerOptions { Metrics = registry });
        registry.Publish(only.MetricRegistrations);

        var before = Scrape(registry, snapshot);
        var after = Scrape(registry, only);

        Assert.Contains("host=\"b.test\"", before, StringComparison.Ordinal);
        Assert.DoesNotContain("host=\"b.test\"", after, StringComparison.Ordinal);
        Assert.Contains("host=\"a.test\"", after, StringComparison.Ordinal);
    }
}
