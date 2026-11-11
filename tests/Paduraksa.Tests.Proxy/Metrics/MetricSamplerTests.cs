using Paduraksa.Core.Model;
using Paduraksa.Proxy.Metrics;

namespace Paduraksa.Tests.Proxy.Metrics;

public sealed class MetricSamplerTests
{
    private static readonly Guid HostA = new("00000000-0000-7000-8000-00000000000a");
    private static readonly Guid HostB = new("00000000-0000-7000-8000-00000000000b");
    private static readonly Guid HostC = new("00000000-0000-7000-8000-00000000000c");
    private static readonly Guid UpstreamA = new("00000000-0000-7000-8000-0000000000b1");

    /// <summary>A clock the test moves: the sampler reads the date, the registry's grace period the monotonic timestamp.</summary>
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;

        public override long GetTimestamp() => Now.UtcTicks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    // Minute and ten-second boundaries line up at this instant, so a test knows which bucket each second lands in.
    private static readonly DateTimeOffset Start = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    private sealed class Rig
    {
        public Rig(TimeSpan? grace = null)
        {
            Clock = new ManualClock(Start);
            Registry = new MetricsRegistry(processors: 2, gracePeriod: grace, timeProvider: Clock);
            Queue = new MetricRollupBacklog();
            Sampler = new MetricSampler(Registry, Clock, Queue);
        }

        public ManualClock Clock { get; }

        public MetricsRegistry Registry { get; }

        public MetricRollupBacklog Queue { get; }

        public MetricSampler Sampler { get; }

        public MetricSlot Host(Guid id) => Registry.SlotFor(MetricSubject.Host, id);

        public void Request(MetricSlot host, int status = 200, long totalMicros = 1_000, long upstreamMicros = -1, GateOutcome outcome = GateOutcome.Forwarded, MetricSlot? upstream = null, int error = 0) =>
            Registry.RecordRequest(new RequestMetrics
            {
                Host = host,
                Upstream = upstream ?? MetricSlot.None,
                Status = status,
                Outcome = outcome,
                TotalMicros = totalMicros,
                UpstreamMicros = upstreamMicros,
                ForwarderError = error,
            });

        /// <summary>Moves the clock to <paramref name="seconds"/> after the start and samples.</summary>
        public void TickAt(int seconds)
        {
            Clock.Now = Start.AddSeconds(seconds);
            Sampler.Tick();
        }
    }

    [Fact]
    public void AFinishedTenSecondBucketHoldsWhatItsSecondsGained()
    {
        Rig rig = new();
        var host = rig.Host(HostA);

        for (var i = 0; i < 100; i++)
        {
            rig.Request(host, 200);
        }

        rig.Request(host, 500);
        rig.TickAt(3);
        rig.Request(host, 404);
        rig.TickAt(7);
        rig.TickAt(10);

        var view = rig.Sampler.Read(MetricRollupSubject.Host, HostA)!.Value;
        var point = Assert.Single(view.Points);
        Assert.Equal(Start.ToUnixTimeSeconds(), point.Start);
        Assert.Equal(10, point.Seconds);
        Assert.Equal(102, point.Requests);
        Assert.Equal(100, point.Status2xx);
        Assert.Equal(1, point.Status4xx);
        Assert.Equal(1, point.Status5xx);
        Assert.Equal(10.2, point.RequestsPerSecond, 3);
        Assert.Equal(1.0 / 102, point.ErrorRate, 6);
    }

    [Fact]
    public void PercentilesComeFromTheBucketsOwnHistogram()
    {
        Rig rig = new();
        var host = rig.Host(HostA);
        for (var i = 0; i < 90; i++)
        {
            rig.Request(host, totalMicros: 1_000, upstreamMicros: 600);
        }

        for (var i = 0; i < 10; i++)
        {
            rig.Request(host, totalMicros: 100_000, upstreamMicros: 90_000);
        }

        rig.TickAt(1);
        rig.TickAt(10);

        var point = Assert.Single(rig.Sampler.Read(MetricRollupSubject.Host, HostA)!.Value.Points);
        Assert.InRange(point.TotalP50, 900, 1_300);
        Assert.InRange(point.TotalP95, 90_000, 130_000);
        Assert.InRange(point.UpstreamP50, 500, 800);
        Assert.InRange(point.UpstreamP99, 80_000, 110_000);
        Assert.Equal(100, point.UpstreamTimed);
    }

    [Fact]
    public void ASecondsGainIsTakenOnceAndNotAgainOnTheNextTick()
    {
        Rig rig = new();
        var host = rig.Host(HostA);
        rig.Request(host);

        rig.TickAt(1);
        rig.TickAt(2);
        rig.TickAt(10);

        Assert.Equal(1, Assert.Single(rig.Sampler.Read(MetricRollupSubject.Host, HostA)!.Value.Points).Requests);
    }

    [Fact]
    public void TheOpenBucketIsReadAsItIsWithItsAge()
    {
        Rig rig = new();
        var host = rig.Host(HostA);
        rig.Request(host);
        rig.Request(host);
        rig.TickAt(2);

        var view = rig.Sampler.Read(MetricRollupSubject.Host, HostA)!.Value;

        Assert.Empty(view.Points);
        Assert.Equal(2, view.Open.Requests);
        Assert.Equal(3, view.Open.Seconds);
    }

    [Fact]
    public void AFinishedMinuteGoesToTheRollupQueueUnderTheTypedIdNotTheSlot()
    {
        Rig rig = new();
        var host = rig.Host(HostA);
        var upstream = rig.Registry.SlotFor(MetricSubject.Upstream, UpstreamA);
        for (var i = 0; i < 30; i++)
        {
            rig.Request(host, 200, upstreamMicros: 500, upstream: upstream);
        }

        rig.Request(host, 502, upstreamMicros: 900, upstream: upstream, error: 10);
        rig.TickAt(30);
        rig.TickAt(59);
        Assert.Equal(0, rig.Queue.Count);

        rig.TickAt(60);

        var minutes = rig.Queue.Drain();
        var hostMinute = Assert.Single(minutes, m => m.Subject == MetricRollupSubject.Host);
        Assert.Equal(HostId.FromValue(HostA).ToString(), hostMinute.SubjectId);
        Assert.Equal(31, hostMinute.Point.Requests);
        Assert.Equal(60, hostMinute.Point.Seconds);
        Assert.Equal(Start.ToUnixTimeSeconds(), hostMinute.Point.Start);
        var totalMinute = Assert.Single(minutes, m => m.Subject == MetricRollupSubject.Total);
        Assert.Equal(string.Empty, totalMinute.SubjectId);
        Assert.Equal(31, totalMinute.Point.Requests);
        var upstreamMinute = Assert.Single(minutes, m => m.Subject == MetricRollupSubject.Upstream);
        Assert.Equal(UpstreamId.FromValue(UpstreamA).ToString(), upstreamMinute.SubjectId);
        Assert.Equal(31, upstreamMinute.Point.Requests);
        Assert.Equal(1, upstreamMinute.Point.Errors);
    }

    [Fact]
    public void AMinuteWithNoRequestsIsNotQueued()
    {
        Rig rig = new();
        _ = rig.Host(HostA);

        rig.TickAt(1);
        rig.TickAt(61);
        rig.TickAt(121);

        Assert.Equal(0, rig.Queue.Count);
    }

    [Fact]
    public void TheTotalsAreEveryHostAndTheRequestsNoHostOwns()
    {
        Rig rig = new();
        rig.Request(rig.Host(HostA));
        rig.Request(rig.Host(HostB), 503);
        rig.Request(MetricsRegistry.UnmatchedHost, 404, outcome: GateOutcome.UnknownHost);
        rig.TickAt(1);
        rig.TickAt(10);

        var total = Assert.Single(rig.Sampler.Read(MetricRollupSubject.Total, Guid.Empty)!.Value.Points);
        Assert.Equal(3, total.Requests);
        Assert.Equal(1, total.Status5xx);
        Assert.Equal(1, total.Status4xx);
        var unmatched = Assert.Single(rig.Sampler.Read(MetricRollupSubject.Host, Guid.Empty)!.Value.Points);
        Assert.Equal(1, unmatched.Requests);
    }

    [Fact]
    public void RefusalsAreCountedAndAnAnsweredRequestIsNot()
    {
        Rig rig = new();
        var host = rig.Host(HostA);
        rig.Request(host, 429, outcome: GateOutcome.RefusedRateLimit);
        rig.Request(host, 403, outcome: GateOutcome.RefusedAccessList);
        rig.Request(host, 301, outcome: GateOutcome.Answered);
        rig.Request(host, 404, outcome: GateOutcome.NoRoute);
        rig.TickAt(1);
        rig.TickAt(10);

        var point = Assert.Single(rig.Sampler.Read(MetricRollupSubject.Host, HostA)!.Value.Points);

        Assert.Equal(2, point.Refused);
        Assert.Equal(4, point.Requests);
    }

    [Fact]
    public void ADeletedHostsLastMinuteIsFlushedUnderItsOwnIdWhenItsSlotGoesToAnotherHost()
    {
        Rig rig = new(grace: TimeSpan.FromSeconds(60));
        var first = rig.Host(HostA);
        rig.Request(first);
        rig.Request(first);
        rig.TickAt(5);

        rig.Registry.Publish(new MetricRegistrations());
        // Inside the grace period nothing may reuse the slot: the sampler keeps sampling the retired host.
        rig.Request(first);
        rig.TickAt(30);
        Assert.NotEqual(first, rig.Host(HostC));

        rig.Clock.Now = Start.AddSeconds(70);
        var second = rig.Host(HostB);
        Assert.Equal(first.Index, second.Index);
        rig.Request(second, 500);
        rig.Sampler.Tick();

        var minutes = rig.Queue.Drain();
        var gone = Assert.Single(minutes, m => m.Subject == MetricRollupSubject.Host && m.SubjectId == HostId.FromValue(HostA).ToString());
        Assert.Equal(3, gone.Point.Requests);
        var view = rig.Sampler.Read(MetricRollupSubject.Host, HostB)!.Value;
        Assert.Equal(1, view.Open.Requests);
        Assert.Equal(1, view.Open.Status5xx);
        Assert.Null(rig.Sampler.Read(MetricRollupSubject.Host, HostA));
    }

    [Fact]
    public void ARingKeepsAnHourOfTenSecondBucketsAndForgetsTheOldest()
    {
        Rig rig = new();
        var host = rig.Host(HostA);

        for (var second = 0; second <= 3_700; second += 10)
        {
            rig.Request(host);
            rig.TickAt(second);
        }

        var points = rig.Sampler.Read(MetricRollupSubject.Host, HostA)!.Value.Points;
        Assert.Equal(360, points.Length);
        Assert.True(points[0].Start < points[^1].Start);
        Assert.Equal(points[0].Start + (359 * 10), points[^1].Start);
        Assert.Equal(Start.ToUnixTimeSeconds() + 3_700 - 3_600, points[0].Start);
    }

    [Fact]
    public void ASummaryCoversTheWindowAndWeighsPercentilesByRequests()
    {
        Rig rig = new();
        var host = rig.Host(HostA);
        for (var i = 0; i < 300; i++)
        {
            rig.Request(host, totalMicros: 1_000);
        }

        rig.TickAt(5);
        rig.TickAt(10);
        for (var i = 0; i < 100; i++)
        {
            rig.Request(host, totalMicros: 10_000);
        }

        rig.TickAt(15);

        var summary = rig.Sampler.Summarise(MetricRollupSubject.Host, TimeSpan.FromSeconds(30))[HostA];

        Assert.Equal(400, summary.Requests);
        Assert.InRange(summary.TotalP95, 2_500, 4_000);
        Assert.Equal(400, rig.Sampler.SummariseTotal(TimeSpan.FromSeconds(30)).Requests);
        Assert.Equal(100, rig.Sampler.SummariseTotal(TimeSpan.FromSeconds(5)).Requests);
    }

    [Fact]
    public void AnObjectWithNoTrafficYetStillHasAnEmptySeries()
    {
        Rig rig = new();
        _ = rig.Host(HostA);
        rig.TickAt(1);
        rig.TickAt(10);

        var point = Assert.Single(rig.Sampler.Read(MetricRollupSubject.Host, HostA)!.Value.Points);

        Assert.Equal(0, point.Requests);
        Assert.Equal(0, point.TotalP95);
        Assert.Equal(0, point.ErrorRate);
    }

    [Fact]
    public void AWallClockStepBackwardsNeverClosesAMinuteTwiceOrAddsAnEarlierRow()
    {
        Rig rig = new();
        var host = rig.Host(HostA);
        rig.Request(host);
        rig.TickAt(5);
        rig.Request(host);
        rig.TickAt(65);
        rig.Request(host);
        rig.TickAt(125);
        Assert.Equal(2, rig.Queue.Drain().Count(m => m.Subject == MetricRollupSubject.Host));

        // NTP puts the clock back two minutes: ticks go on at the second the sampler had last reached.
        rig.Request(host);
        rig.TickAt(20);
        rig.Request(host);
        rig.TickAt(30);
        Assert.Equal(0, rig.Queue.Count);

        rig.Request(host);
        rig.TickAt(185);

        var minutes = rig.Queue.Drain().Where(m => m.Subject == MetricRollupSubject.Host).ToArray();
        var minute = Assert.Single(minutes);
        // The minute that was open when the clock stepped back, with what was counted while it was "in the past".
        Assert.Equal(Start.ToUnixTimeSeconds() + 120, minute.Point.Start);
        Assert.Equal(3, minute.Point.Requests);
        var points = rig.Sampler.Read(MetricRollupSubject.Host, HostA)!.Value.Points;
        Assert.Equal(points.OrderBy(p => p.Start).Select(p => p.Start), points.Select(p => p.Start));
        Assert.Equal(points.Length, points.Select(p => p.Start).Distinct().Count());
    }

    [Fact]
    public void ARentedSlotsNewOwnerStartsAtZeroAndTheOldOwnersCountsAreNeverAddedToIt()
    {
        Rig rig = new(grace: TimeSpan.FromSeconds(60));
        var first = rig.Host(HostA);
        for (var i = 0; i < 50; i++)
        {
            rig.Request(first);
        }

        rig.TickAt(5);
        rig.Registry.Publish(new MetricRegistrations());
        rig.Clock.Now = Start.AddSeconds(70);
        var second = rig.Host(HostB);
        Assert.Equal(first.Index, second.Index);

        // The rent cleared the counters (50 -> 0): the drop must not be read as a gain, nor the old 50 as the new owner's.
        rig.Request(second);
        rig.Sampler.Tick();

        var view = rig.Sampler.Read(MetricRollupSubject.Host, HostB)!.Value;
        Assert.Equal(1, view.Open.Requests);
        Assert.Equal(1, rig.Sampler.SummariseTotal(TimeSpan.FromSeconds(30)).Requests);
    }
}
