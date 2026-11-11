using Microsoft.Extensions.Logging;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Metrics;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Yarp;
using Paduraksa.Tests.Shared;

namespace Paduraksa.Tests.Proxy.Metrics;

/// <summary>
/// A publish that loses a metric slot: the object's slot went to another object while its candidate waited. The publisher says so,
/// and the stale candidate's requests are dropped instead of counted into the slot's new owner.
/// </summary>
public sealed class MetricSlotConflictTests
{
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;

        public override long GetTimestamp() => Now.UtcTicks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    private sealed class ListLogger : ILoggerFactory, ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));

        public void Dispose()
        {
        }
    }

    private static ProxyConfigModel Model(string label) => new()
    {
        Hosts = [new HostSpec { Id = TestIds.Host(label), Names = [label + ".test"], Kind = HostKind.Respond, Respond = new RespondSpec { StatusCode = 503, Body = "soon" } }],
    };

    [Fact]
    public async Task AConflictIsLoggedAndTheStaleHostRecordsNothingIntoTheSlotsNewOwner()
    {
        var ct = TestContext.Current.CancellationToken;
        ManualClock clock = new(new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero));
        MetricsRegistry registry = new(processors: 2, gracePeriod: TimeSpan.FromSeconds(60), timeProvider: clock);
        ListLogger logs = new();
        SnapshotCompilerOptions options = new() { Metrics = registry };
        await using SnapshotPublisher publisher = new(new SnapshotStore(), logs, clock, metrics: registry);

        // Two candidates of host A compiled together (one slot), the first published.
        var first = SnapshotCompiler.Compile(Model("a"), options).Snapshot!;
        var waiting = SnapshotCompiler.Compile(Model("a"), options).Snapshot!;
        Assert.True((await publisher.PublishAsync(first, ct)).Succeeded);
        var stale = waiting.HostsByIndex[0];

        // A is deleted, its slot is retired and, a grace period later, rented to host B.
        Assert.True((await publisher.PublishAsync(SnapshotCompiler.Compile(new ProxyConfigModel(), options).Snapshot!, ct)).Succeeded);
        clock.Now = clock.Now.AddSeconds(61);
        var b = SnapshotCompiler.Compile(Model("b"), options).Snapshot!.HostsByIndex[0];
        Assert.Equal(stale.MetricSlot.Index, b.MetricSlot.Index);

        // The old candidate finally goes live: it can't have its slot back.
        Assert.True((await publisher.PublishAsync(waiting, ct)).Succeeded);

        var warning = Assert.Single(logs.Entries, entry => entry.Message.Contains("metric slot", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("1 object(s)", warning.Message, StringComparison.Ordinal);
        // Nothing the live (stale) host answers is counted into B, and B's own counters stay B's.
        registry.RecordRequest(new RequestMetrics { Host = stale.MetricSlot, Status = 503, Outcome = GateOutcome.Answered, TotalMicros = 5 });
        registry.RecordRequest(new RequestMetrics { Host = b.MetricSlot, Status = 200, Outcome = GateOutcome.Answered, TotalMicros = 5 });
        var counters = new long[RequestMetric.Stride];
        registry.ReadCounters(MetricSubject.Host, b.MetricSlot.Index, counters);
        Assert.Equal(0, counters[RequestMetric.Status + 4]);
        Assert.Equal(1, counters[RequestMetric.Status + 1]);
        // ... and the stale host is not exported under a label that would carry B's counters.
        using var writer = new StringWriter();
        PrometheusWriter.Write(writer, registry, publisher.Store.Current, ProcessCounters.None);
        Assert.DoesNotContain("host=\"a.test\"", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task APublishThatKeepsEveryObjectLogsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        MetricsRegistry registry = new(processors: 1);
        ListLogger logs = new();
        await using SnapshotPublisher publisher = new(new SnapshotStore(), logs, metrics: registry);

        Assert.True((await publisher.PublishAsync(SnapshotCompiler.Compile(Model("a"), new SnapshotCompilerOptions { Metrics = registry }).Snapshot!, ct)).Succeeded);

        Assert.DoesNotContain(logs.Entries, entry => entry.Message.Contains("metric slot", StringComparison.Ordinal));
    }
}
