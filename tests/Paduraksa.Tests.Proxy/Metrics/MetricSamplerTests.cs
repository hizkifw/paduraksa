using Paduraksa.Core.Model;
using Paduraksa.Proxy.Metrics;

namespace Paduraksa.Tests.Proxy.Metrics;

public sealed class MetricSamplerTests
{
    private static readonly Guid HostA = new("00000000-0000-7000-8000-00000000000a");
    private static readonly Guid HostB = new("00000000-0000-7000-8000-00000000000b");
    private static readonly Guid HostC = new("00000000-0000-7000-8000-00000000000c");
    private static readonly Guid UpstreamA = new("00000000-0000-7000-8000-0000000000b1");

    /// <summary>
    /// A clock the test moves: the sampler reads the date for labels and the monotonic timestamp for how long it observed. The two move
    /// together unless a test steps the wall clock alone.
    /// </summary>
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private long _timestamp = start.UtcTicks;

        public DateTimeOffset Now { get; private set; } = start;

        /// <summary>Both clocks move forward by <paramref name="by"/>.</summary>
        public void Advance(TimeSpan by)
        {
            Now += by;
            _timestamp += by.Ticks;
        }

        /// <summary>Moves the wall clock to <paramref name="to"/>; the monotonic clock moves forward by the difference if there is one, and stays put when the wall clock goes back.</summary>
        public void MoveTo(DateTimeOffset to)
        {
            _timestamp += Math.Max(0, (to - Now).Ticks);
            Now = to;
        }

        /// <summary>Steps only the wall clock, as an NTP correction does.</summary>
        public void StepWall(DateTimeOffset to) => Now = to;

        public override DateTimeOffset GetUtcNow() => Now;

        public override long GetTimestamp() => _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    // Minute and ten-second boundaries line up at this instant, so a test knows which bucket each second lands in.
    private static readonly DateTimeOffset Start = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    private sealed class Rig
    {
        /// <param name="grace">How long a deleted object's slot is kept before another can have it.</param>
        /// <param name="bootSeconds">How long after <see cref="Start"/> the process starts, so a test can begin inside a minute.</param>
        public Rig(TimeSpan? grace = null, double bootSeconds = 0)
        {
            Clock = new ManualClock(Start.AddSeconds(bootSeconds));
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
        public void TickAt(double seconds)
        {
            Clock.MoveTo(Start.AddSeconds(seconds));
            Sampler.Tick();
            _last = (int)Math.Floor(seconds);
        }

        /// <summary>Samples at every whole second after the last one up to <paramref name="seconds"/>: the sampler as the service runs it, with no tick late.</summary>
        public void TickEverySecondTo(int seconds)
        {
            for (var second = Math.Max(_last, (int)(Clock.Now - Start).TotalSeconds) + 1; second <= seconds; second++)
            {
                TickAt(second);
            }
        }

        private int _last = int.MinValue;
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
        Assert.Equal(10, point.CoveredSeconds);
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
    public void TheOpenBucketIsReadAsItIsWithTheSecondsItHasObserved()
    {
        Rig rig = new();
        var host = rig.Host(HostA);
        rig.Request(host);
        rig.Request(host);
        rig.TickAt(2);

        var view = rig.Sampler.Read(MetricRollupSubject.Host, HostA)!.Value;

        Assert.Empty(view.Points);
        Assert.Equal(2, view.Open.Requests);
        Assert.Equal(2, view.Open.CoveredSeconds);
        Assert.Equal(10, view.Open.Width);
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
        rig.TickEverySecondTo(59);
        Assert.Equal(0, rig.Queue.Count);

        rig.TickEverySecondTo(60);

        var minutes = rig.Queue.Drain();
        var hostMinute = Assert.Single(minutes, m => m.Subject == MetricRollupSubject.Host);
        Assert.Equal(HostId.FromValue(HostA).ToString(), hostMinute.SubjectId);
        Assert.Equal(31, hostMinute.Point.Requests);
        Assert.Equal(60, hostMinute.Point.Width);
        Assert.Equal(60, hostMinute.Point.CoveredSeconds);
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

        rig.TickEverySecondTo(72);
        var second = rig.Host(HostB);
        Assert.Equal(first.Index, second.Index);
        rig.Request(second, 500);
        rig.TickAt(73);

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
    public void AWallClockStepBackwardsNeverReopensAMinuteAndEveryRealSecondIsCountedOnce()
    {
        Rig rig = new();
        var host = rig.Host(HostA);
        for (var second = 1; second <= 55; second++)
        {
            rig.Request(host);
            rig.TickAt(second);
        }

        // NTP puts the wall clock back 100 s; real time goes on at a second a tick. The labels run on from where they were.
        rig.Clock.StepWall(Start.AddSeconds(-45));
        for (var second = 56; second <= 70; second++)
        {
            rig.Request(host);
            rig.Clock.Advance(TimeSpan.FromSeconds(1));
            rig.Sampler.Tick();
        }

        var closed = Assert.Single(rig.Queue.Drain(), m => m.Subject == MetricRollupSubject.Host);
        Assert.Equal(Start.ToUnixTimeSeconds(), closed.Point.Start);
        Assert.Equal(60, closed.Point.Requests);
        Assert.Equal(60, closed.Point.CoveredSeconds);
        Assert.Equal(1, closed.Point.RequestsPerSecond);

        rig.Sampler.Flush();

        var rest = Assert.Single(rig.Queue.Drain(), m => m.Subject == MetricRollupSubject.Host);
        Assert.Equal(Start.ToUnixTimeSeconds() + 60, rest.Point.Start);
        Assert.Equal(10, rest.Point.Requests);
        Assert.Equal(10, rest.Point.CoveredSeconds);
        var starts = rig.Sampler.Read(MetricRollupSubject.Host, HostA)!.Value.Points.Select(p => p.Start).ToArray();
        Assert.Equal(starts.Order(), starts);
        Assert.Equal(starts.Length, starts.Distinct().Count());
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
        rig.TickEverySecondTo(72);
        var second = rig.Host(HostB);
        Assert.Equal(first.Index, second.Index);

        // The rent cleared the counters (50 -> 0): the drop must not be read as a gain, nor the old 50 as the new owner's.
        rig.Request(second);
        rig.TickAt(73);

        var view = rig.Sampler.Read(MetricRollupSubject.Host, HostB)!.Value;
        Assert.Equal(1, view.Open.Requests);
        Assert.Equal(1, rig.Sampler.SummariseTotal(TimeSpan.FromSeconds(30)).Requests);
    }

    [Fact]
    public void AnIdleMinuteIsQueuedForTheTotalsWithItsSecondsAndNotForAHostThatSawNothing()
    {
        Rig rig = new();
        _ = rig.Host(HostA);

        rig.TickEverySecondTo(61);

        var minute = Assert.Single(rig.Queue.Drain());
        Assert.Equal(MetricRollupSubject.Total, minute.Subject);
        Assert.Equal(Start.ToUnixTimeSeconds(), minute.Point.Start);
        Assert.Equal(0, minute.Point.Requests);
        Assert.Equal(60, minute.Point.CoveredSeconds);
    }

    [Fact]
    public void TheFirstMinuteAfterBootCoversOnlyTheSecondsTheProcessWasUp()
    {
        Rig rig = new(bootSeconds: 20);

        rig.TickEverySecondTo(61);

        var first = Assert.Single(rig.Queue.Drain());
        Assert.Equal(Start.ToUnixTimeSeconds(), first.Point.Start);
        Assert.Equal(40, first.Point.CoveredSeconds);
        Assert.Equal(60, first.Point.Width);
    }

    [Fact]
    public void ARateIsOverTheSecondsObservedNotTheMinute()
    {
        Rig rig = new(bootSeconds: 20);
        var host = rig.Host(HostA);
        for (var i = 0; i < 400; i++)
        {
            rig.Request(host);
        }

        rig.TickEverySecondTo(60);

        var minute = Assert.Single(rig.Queue.Drain(), m => m.Subject == MetricRollupSubject.Host);
        Assert.Equal(400, minute.Point.Requests);
        Assert.Equal(10, minute.Point.RequestsPerSecond);
    }

    [Fact]
    public void AGapOfOverFiveSecondsWithNothingCountedInItIsNotObservedAndShowsAsBucketsNobodyObserved()
    {
        Rig rig = new();
        _ = rig.Host(HostA);
        rig.TickEverySecondTo(8);

        rig.TickAt(41);

        var view = rig.Sampler.Read(MetricRollupSubject.Total, Guid.Empty)!.Value;
        Assert.Equal([8, 0, 0, 0], view.Points.Select(p => p.CoveredSeconds));
        Assert.Equal(Start.ToUnixTimeSeconds() + 30, view.Points[3].Start);
        Assert.Equal(Start.ToUnixTimeSeconds() + 40, view.Open.Start);
        Assert.Equal(0, view.Open.CoveredSeconds);
        Assert.All(view.Points, point => Assert.Equal(0, point.Requests));
    }

    [Fact]
    public void AStallInWhichRequestsWereServedIsObservedAndItsGainIsSpreadOverTheWholeGap()
    {
        Rig rig = new();
        var host = rig.Host(HostA);
        rig.TickEverySecondTo(5);
        for (var i = 0; i < 300; i++)
        {
            rig.Request(host);
        }

        // The sampler was 25 s late (thread pool starvation); the requests were answered all along.
        rig.TickAt(30);

        var view = rig.Sampler.Read(MetricRollupSubject.Total, Guid.Empty)!.Value;
        Assert.Equal([10, 10, 10], view.Points.Select(p => p.CoveredSeconds));
        Assert.Equal([60, 120, 120], view.Points.Select(p => p.Requests));
        rig.TickEverySecondTo(65);
        var minute = Assert.Single(rig.Queue.Drain(), m => m.Subject == MetricRollupSubject.Total);
        Assert.Equal(60, minute.Point.CoveredSeconds);
        Assert.Equal(300, minute.Point.Requests);
        Assert.Equal(5, minute.Point.RequestsPerSecond);
    }

    [Fact]
    public void AStallOverAMinuteLeavesNoRowForTheMinutesNobodyObserved()
    {
        Rig rig = new();
        rig.TickEverySecondTo(30);

        rig.TickAt(200);
        rig.TickEverySecondTo(215);
        rig.TickEverySecondTo(240);

        var minutes = rig.Queue.Drain();
        Assert.Equal([Start.ToUnixTimeSeconds(), Start.ToUnixTimeSeconds() + 180], minutes.Select(m => m.Point.Start));
        // The stall's own tick, at 200 s, observed nothing: the minute that 200 s is in was seen from 201 s on.
        Assert.Equal([30, 40], minutes.Select(m => m.Point.CoveredSeconds));
    }

    [Fact]
    public void AStallLongerThanTheRingLeavesAHourOfUnobservedBucketsEndingRightBeforeNow()
    {
        Rig rig = new();
        rig.TickEverySecondTo(5);

        rig.TickAt(3 * 3_600);

        var points = rig.Sampler.Read(MetricRollupSubject.Total, Guid.Empty)!.Value.Points;
        Assert.Equal(MetricSeries.RingPoints, points.Length);
        Assert.Equal(Start.ToUnixTimeSeconds() + (3 * 3_600) - 3_600, points[0].Start);
        Assert.Equal(Start.ToUnixTimeSeconds() + (3 * 3_600) - 10, points[^1].Start);
        Assert.All(points, point => Assert.Equal(0, point.CoveredSeconds));
    }

    [Fact]
    public void ALateTickThatCrossesABoundarySplitsItsGainInProportionToTheTimeOnEachSide()
    {
        Rig rig = new();
        var host = rig.Host(HostA);
        var upstream = rig.Registry.SlotFor(MetricSubject.Upstream, UpstreamA);
        rig.TickEverySecondTo(6);
        for (var i = 0; i < 13; i++)
        {
            rig.Request(host, upstreamMicros: 500, upstream: upstream);
        }

        // 4 s of the 5 s since the last tick are before the boundary at 10 s, 1 s after it: 10.4 and 2.6 requests.
        rig.TickAt(11);

        var view = rig.Sampler.Read(MetricRollupSubject.Host, HostA)!.Value;
        var closed = Assert.Single(view.Points);
        Assert.Equal(10, closed.Requests);
        Assert.Equal(10, closed.Status2xx);
        Assert.Equal(10, closed.UpstreamTimed);
        Assert.Equal(10, closed.CoveredSeconds);
        Assert.Equal(3, view.Open.Requests);
        Assert.Equal(3, view.Open.Status2xx);
        Assert.Equal(3, view.Open.UpstreamTimed);
        Assert.Equal(1, view.Open.CoveredSeconds);
        var total = rig.Sampler.Read(MetricRollupSubject.Total, Guid.Empty)!.Value;
        Assert.Equal(10, Assert.Single(total.Points).Requests);
        Assert.Equal(3, total.Open.Requests);
        // The upstream's series and its latency histogram are split the same way.
        var upstreamView = rig.Sampler.Read(MetricRollupSubject.Upstream, UpstreamA)!.Value;
        Assert.Equal(10, Assert.Single(upstreamView.Points).UpstreamTimed);
        Assert.Equal(3, upstreamView.Open.UpstreamTimed);
        Assert.InRange(closed.UpstreamP50, 450, 650);
        Assert.InRange(view.Open.UpstreamP50, 450, 650);
    }

    [Fact]
    public void ALateTickThatCrossesAMinuteBoundarySplitsTheMinutesToo()
    {
        Rig rig = new();
        var host = rig.Host(HostA);
        rig.TickEverySecondTo(56);
        for (var i = 0; i < 100; i++)
        {
            rig.Request(host);
        }

        rig.TickAt(61);

        var minute = Assert.Single(rig.Queue.Drain(), m => m.Subject == MetricRollupSubject.Host);
        Assert.Equal(80, minute.Point.Requests);
        Assert.Equal(60, minute.Point.CoveredSeconds);
        Assert.Equal(20, rig.Sampler.Read(MetricRollupSubject.Host, HostA)!.Value.Open.Requests);
    }

    [Fact]
    public void AnEvenSplitGivesTheLeftoverCountToTheEarlierPieceAndANearBoundaryTickKeepsItsCountsWhereTheyHappened()
    {
        Rig rig = new();
        var host = rig.Host(HostA);
        rig.TickEverySecondTo(8);
        rig.Request(host);
        rig.Request(host);
        rig.Request(host);

        // The tick is 4 ms late: 3 requests, of which 0.012 belong after the boundary.
        rig.TickAt(10.004);

        Assert.Equal(3, Assert.Single(rig.Sampler.Read(MetricRollupSubject.Host, HostA)!.Value.Points).Requests);
        Assert.Equal(0, rig.Sampler.Read(MetricRollupSubject.Host, HostA)!.Value.Open.Requests);
    }

    [Fact]
    public void WhateverTheJitterNothingIsLostOrCountedTwiceAndEveryObservedSecondIsAccountedFor()
    {
        Rig rig = new();
        var host = rig.Host(HostA);
        Random random = new(7);
        long sent = 0;
        double seconds = 0;
        while (seconds < 400)
        {
            var burst = random.Next(0, 9);
            for (var i = 0; i < burst; i++)
            {
                rig.Request(host);
            }

            sent += burst;
            // Mostly on the second, sometimes late by a few milliseconds, sometimes by a few seconds.
            seconds += random.Next(0, 10) switch { < 7 => 1, < 9 => 1 + (random.Next(1, 90) / 1000.0), _ => random.Next(2, 6) };
            rig.TickAt(seconds);
        }

        rig.Sampler.Flush();

        var minutes = rig.Queue.Drain();
        Assert.Equal(sent, minutes.Where(m => m.Subject == MetricRollupSubject.Host).Sum(m => m.Point.Requests));
        Assert.Equal(sent, minutes.Where(m => m.Subject == MetricRollupSubject.Total).Sum(m => m.Point.Requests));
        Assert.Equal(sent, rig.Sampler.Read(MetricRollupSubject.Host, HostA)!.Value.Points.Sum(p => p.Requests));
        // Every interval was at most five seconds, so all of the time was observed; each minute's figure is rounded to a second.
        var covered = minutes.Where(m => m.Subject == MetricRollupSubject.Total).ToArray();
        Assert.InRange(covered.Sum(m => m.Point.CoveredSeconds), seconds - (covered.Length / 2.0) - 1, seconds + (covered.Length / 2.0) + 1);
    }

    [Theory]
    [InlineData(0, 1000)]
    [InlineData(1, 999)]
    [InlineData(999, 1)]
    [InlineData(1_000, 1000)]
    [InlineData(2_345, 655)]
    public void TheServiceWaitsForTheNextWholeSecondOfTheClock(int millisIntoTheSecond, int expectedWait) =>
        Assert.Equal(TimeSpan.FromMilliseconds(expectedWait), MetricSamplerService.UntilNextSecond(Start.AddMilliseconds(millisIntoTheSecond + 5_000_000)));

    [Fact]
    public void AFlushWritesTheOpenMinuteAsItStandsAndTheOpenBucketGoesIntoTheRing()
    {
        Rig rig = new(bootSeconds: 20);
        var host = rig.Host(HostA);
        rig.TickEverySecondTo(44);
        rig.Request(host);
        rig.Request(host);
        rig.Clock.MoveTo(Start.AddSeconds(45.4));

        rig.Sampler.Flush();

        var minutes = rig.Queue.Drain();
        var total = Assert.Single(minutes, m => m.Subject == MetricRollupSubject.Total);
        Assert.Equal(Start.ToUnixTimeSeconds(), total.Point.Start);
        Assert.Equal(25, total.Point.CoveredSeconds);
        Assert.Equal(2, total.Point.Requests);
        Assert.Equal(2, Assert.Single(minutes, m => m.Subject == MetricRollupSubject.Host).Point.Requests);
        var points = rig.Sampler.Read(MetricRollupSubject.Total, Guid.Empty)!.Value.Points;
        Assert.Equal(2, points.Sum(p => p.Requests));
    }

    private static MetricRollup SeededMinute(MetricRollupSubject subject, Guid owner, int minutesBefore, long requests, int covered = 60) => new(
        subject,
        owner == Guid.Empty ? string.Empty : subject == MetricRollupSubject.Host ? HostId.FromValue(owner).ToString() : UpstreamId.FromValue(owner).ToString(),
        new MetricPoint { Start = Start.ToUnixTimeSeconds() - (minutesBefore * 60), Width = 60, CoveredSeconds = covered, Requests = requests, Status2xx = requests, TotalP95 = 2_000 });

    [Fact]
    public void ASummaryOfTheHourReachesBackIntoTheSeededMinutesAndIncludesHostsThatOnlyHaveHistory()
    {
        Rig rig = new(bootSeconds: 30);
        rig.Sampler.Seed(
        [
            SeededMinute(MetricRollupSubject.Host, HostA, 10, 50),
            SeededMinute(MetricRollupSubject.Host, HostA, 5, 30),
            SeededMinute(MetricRollupSubject.Host, HostB, 5, 7),
            SeededMinute(MetricRollupSubject.Upstream, UpstreamA, 5, 9),
            SeededMinute(MetricRollupSubject.Host, HostC, 90, 1_000),
        ]);
        var host = rig.Host(HostA);
        for (var i = 0; i < 10; i++)
        {
            rig.Request(host);
        }

        rig.TickEverySecondTo(40);

        var hosts = rig.Sampler.Summarise(MetricRollupSubject.Host, TimeSpan.FromHours(1));
        Assert.Equal(90, hosts[HostA].Requests);
        Assert.Equal(7, hosts[HostB].Requests);
        Assert.False(hosts.ContainsKey(HostC));
        Assert.Equal(9, rig.Sampler.Summarise(MetricRollupSubject.Upstream, TimeSpan.FromHours(1))[UpstreamA].Requests);
        Assert.Equal(10, rig.Sampler.Summarise(MetricRollupSubject.Host, TimeSpan.FromMinutes(1))[HostA].Requests);
    }

    [Fact]
    public void TheSeedIsOnlyTheHourBeforeSoThePaceNowIgnoresItAndOldMinutesAreForgottenAsTimePasses()
    {
        Rig rig = new();
        rig.Sampler.Seed([SeededMinute(MetricRollupSubject.Total, Guid.Empty, 20, 500), SeededMinute(MetricRollupSubject.Total, Guid.Empty, 50, 500)]);
        var host = rig.Host(HostA);
        for (var i = 0; i < 4; i++)
        {
            rig.Request(host);
        }

        rig.TickEverySecondTo(10);

        Assert.Equal(0.4, rig.Sampler.SummariseTotal(TimeSpan.FromSeconds(30)).RequestsPerSecond, 3);
        Assert.Equal(2, rig.Sampler.History(MetricRollupSubject.Total, Guid.Empty).Length);

        // Thirty minutes on, the minute that was 50 minutes before the start is more than an hour old.
        rig.TickAt(30 * 60);
        rig.TickEverySecondTo((30 * 60) + 3);

        Assert.Equal([Start.ToUnixTimeSeconds() - (20 * 60)], rig.Sampler.History(MetricRollupSubject.Total, Guid.Empty).Select(p => p.Start));
    }
}
