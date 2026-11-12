using Paduraksa.Application.Notifications;
using Paduraksa.Contracts.Notifications;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Yarp;

namespace Paduraksa.Tests.Unit.Notifications;

/// <summary>The debounce, the flap guard and what is not a change (a new destination, a removed one, an upstream with no checks).</summary>
public sealed class HealthTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 12, 14, 0, 0, TimeSpan.Zero);
    private static readonly UpstreamId Upstream = UpstreamId.New();
    private static readonly DestinationId A = DestinationId.New();
    private static readonly DestinationId B = DestinationId.New();

    private readonly NotificationOptions _options = new();
    private readonly HealthTracker _tracker;

    public HealthTrackerTests() => _tracker = new HealthTracker(_options);

    private static DestinationReading Destination(DestinationId id, DestinationVerdict verdict, string probe = "HTTP 503") => new(
        id,
        $"http://{id.ToString()[^4..]}:80",
        verdict == DestinationVerdict.Unhealthy ? DestinationVerdict.Unhealthy : DestinationVerdict.Healthy,
        DestinationVerdict.Unknown,
        verdict,
        new ProbeOutcome(T0, verdict != DestinationVerdict.Unhealthy, probe));

    private static UpstreamReading Reading(params DestinationReading[] destinations) => new(Upstream, ActiveChecks: true, PassiveChecks: false, destinations);

    private IReadOnlyList<HealthChange> Observe(int seconds, params DestinationReading[] destinations) =>
        _tracker.Observe([Reading(destinations)], T0.AddSeconds(seconds)).Changes;

    /// <summary>Runs <paramref name="seconds"/> of ticks (one a second) with a fixed state and returns every change seen.</summary>
    private List<HealthChange> Hold(int from, int seconds, params DestinationReading[] destinations)
    {
        List<HealthChange> seen = [];
        for (var s = from; s < from + seconds; s++)
        {
            seen.AddRange(Observe(s, destinations));
        }

        return seen;
    }

    [Fact]
    public void AnUpstreamFirstSeenHealthyOrUnknownReportsNothing()
    {
        Assert.Empty(Hold(0, 30, Destination(A, DestinationVerdict.Healthy), Destination(B, DestinationVerdict.Unknown)));
    }

    [Fact]
    public void ADestinationGoingDownIsReportedOnlyAfterItHasHeldForTheHoldAndTheUpstreamStaysUp()
    {
        Hold(0, 5, Destination(A, DestinationVerdict.Healthy), Destination(B, DestinationVerdict.Healthy));

        var seen = Hold(5, 20, Destination(A, DestinationVerdict.Unhealthy), Destination(B, DestinationVerdict.Healthy));

        var change = Assert.Single(seen);
        Assert.Equal((HealthScope.Destination, true, A, T0.AddSeconds(5)), (change.Scope, change.Down, change.Destination, change.Since));
        Assert.Equal("active probe: HTTP 503", change.Reason);
        Assert.Equal(T0.AddSeconds(5), change.Since);
    }

    [Fact]
    public void ABlipShorterThanTheHoldReportsNothingEitherWay()
    {
        Hold(0, 5, Destination(A, DestinationVerdict.Healthy));
        Assert.Empty(Hold(5, 6, Destination(A, DestinationVerdict.Unhealthy)));

        Assert.Empty(Hold(11, 30, Destination(A, DestinationVerdict.Healthy)));
    }

    [Fact]
    public void TheWholeUpstreamIsReportedWhenNoDestinationIsLeftAndItsDestinationsAreNot()
    {
        Hold(0, 5, Destination(A, DestinationVerdict.Healthy), Destination(B, DestinationVerdict.Healthy));

        var seen = Hold(5, 15, Destination(A, DestinationVerdict.Unhealthy), Destination(B, DestinationVerdict.Unhealthy));

        var change = Assert.Single(seen);
        Assert.Equal((HealthScope.Upstream, true), (change.Scope, change.Down));
        Assert.Equal(2, change.Readings.Count);
    }

    [Fact]
    public void AnUpstreamComingBackIsReportedOnceAndAbsorbsTheDestinationThatCausedIt()
    {
        Hold(0, 5, Destination(A, DestinationVerdict.Healthy), Destination(B, DestinationVerdict.Healthy));
        Hold(5, 15, Destination(A, DestinationVerdict.Unhealthy), Destination(B, DestinationVerdict.Unhealthy));

        var seen = Hold(20, 20, Destination(A, DestinationVerdict.Healthy), Destination(B, DestinationVerdict.Unhealthy));

        var change = Assert.Single(seen);
        Assert.Equal((HealthScope.Upstream, false), (change.Scope, change.Down));
    }

    [Fact]
    public void ADestinationStillDownWhenTheUpstreamComesBackIsNotReportedAgain()
    {
        Hold(0, 5, Destination(A, DestinationVerdict.Healthy), Destination(B, DestinationVerdict.Healthy));
        var down = Hold(5, 15, Destination(A, DestinationVerdict.Unhealthy), Destination(B, DestinationVerdict.Unhealthy));
        Assert.Single(down);

        // B recovers later than A did: that is a destination change in the other direction, so it is reported alongside the upstream's.
        var up = Hold(20, 20, Destination(A, DestinationVerdict.Healthy), Destination(B, DestinationVerdict.Unhealthy));

        Assert.Equal(HealthScope.Upstream, Assert.Single(up).Scope);
    }

    [Fact]
    public void ADestinationFirstSeenUnhealthyIsDownButOneFirstSeenHealthyThatThenGoesUnknownIsNot()
    {
        Assert.Single(Observe(0, Destination(A, DestinationVerdict.Unhealthy)));
        _ = Hold(1, 30, Destination(A, DestinationVerdict.Unknown));
    }

    [Fact]
    public void ARemovedDestinationIsForgottenSoReAddingItCannotReportARecovery()
    {
        Hold(0, 5, Destination(A, DestinationVerdict.Healthy), Destination(B, DestinationVerdict.Healthy));
        Hold(5, 15, Destination(A, DestinationVerdict.Unhealthy), Destination(B, DestinationVerdict.Healthy));

        // Removed (a publish dropped it), then back with a fresh Unknown, then healthy: no "recovered".
        Assert.Empty(Hold(20, 10, Destination(B, DestinationVerdict.Healthy)));
        Assert.Empty(Hold(30, 5, Destination(A, DestinationVerdict.Unknown), Destination(B, DestinationVerdict.Healthy)));
        Assert.Empty(Hold(35, 30, Destination(A, DestinationVerdict.Healthy), Destination(B, DestinationVerdict.Healthy)));
    }

    [Fact]
    public void AnUpstreamWithNoHealthCheckIsNeverTrackedAndNeverDown()
    {
        UpstreamReading unchecked_ = new(Upstream, ActiveChecks: false, PassiveChecks: false, [Destination(A, DestinationVerdict.Unhealthy)]);

        for (var s = 0; s < 60; s++)
        {
            var (changes, views) = _tracker.Observe([unchecked_], T0.AddSeconds(s));
            Assert.Empty(changes);
            Assert.Equal(UpstreamHealthState.Unchecked, Assert.Single(views).State);
        }
    }

    [Fact]
    public void AFlappingDestinationIsReportedAsFlappingOnceThenSilentUntilItHasSettled()
    {
        var reports = new List<HealthChange>();
        var second = 0;
        reports.AddRange(Hold(second, 5, Destination(A, DestinationVerdict.Healthy)));
        second += 5;

        // Down for 11 s, up for 11 s, four times over: the fourth confirmed change within the window is the flapping one.
        for (var round = 0; round < 6; round++)
        {
            reports.AddRange(Hold(second, 11, Destination(A, DestinationVerdict.Unhealthy)));
            second += 11;
            reports.AddRange(Hold(second, 11, Destination(A, DestinationVerdict.Healthy)));
            second += 11;
        }

        Assert.Equal(4, reports.Count);
        Assert.True(reports[3].Flapping);
        Assert.False(reports[2].Flapping);

        // Still for the settle time: one more report, of the state it settled in.
        var settled = Hold(second, (int)_options.FlapSettle.TotalSeconds + 5, Destination(A, DestinationVerdict.Healthy));

        var last = Assert.Single(settled);
        Assert.True(last.Settled);
        Assert.False(last.Down);
    }

    [Fact]
    public void TheViewShowsTheLiveVerdictsTheWholeStateAndTheLastChange()
    {
        Hold(0, 5, Destination(A, DestinationVerdict.Healthy), Destination(B, DestinationVerdict.Healthy));
        Hold(5, 15, Destination(A, DestinationVerdict.Unhealthy), Destination(B, DestinationVerdict.Healthy));

        var view = Assert.Single(_tracker.Observe([Reading(Destination(A, DestinationVerdict.Unhealthy), Destination(B, DestinationVerdict.Healthy))], T0.AddSeconds(21)).Views);

        Assert.Equal(UpstreamHealthState.Degraded, view.State);
        Assert.Equal([DestinationHealthState.Unhealthy, DestinationHealthState.Healthy], view.Destinations.Select(d => d.State));
        Assert.Equal("active probe: HTTP 503", view.Destinations[0].Reason);
        Assert.Equal(T0.AddSeconds(5), view.LastChange!.At);
        Assert.EndsWith("down", view.LastChange.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void AllDestinationsUnhealthyIsDownAndAllUnknownIsUnknown()
    {
        var down = _tracker.Observe([Reading(Destination(A, DestinationVerdict.Unhealthy))], T0).Views.Single();
        Assert.Equal(UpstreamHealthState.Down, down.State);

        HealthTracker other = new(_options);
        var unknown = other.Observe([Reading(Destination(A, DestinationVerdict.Unknown), Destination(B, DestinationVerdict.Unknown))], T0).Views.Single();
        Assert.Equal(UpstreamHealthState.Unknown, unknown.State);
    }

    [Fact]
    public void AnUpstreamWithNoDestinationsIsUnknownNeverHealthy()
    {
        var view = _tracker.Observe([new UpstreamReading(Upstream, ActiveChecks: true, PassiveChecks: false, [])], T0).Views.Single();

        Assert.Equal(UpstreamHealthState.Unknown, view.State);
        Assert.Empty(view.Destinations);
    }

    [Fact]
    public void ReasonsNameTheProbeOrThePassiveCheck()
    {
        DestinationReading probe = new(A, "http://a", DestinationVerdict.Unhealthy, DestinationVerdict.Unknown, DestinationVerdict.Unhealthy, new ProbeOutcome(T0, false, "timed out"));
        DestinationReading passive = new(A, "http://a", DestinationVerdict.Unknown, DestinationVerdict.Unhealthy, DestinationVerdict.Unhealthy, null);
        DestinationReading healthy = new(A, "http://a", DestinationVerdict.Healthy, DestinationVerdict.Unknown, DestinationVerdict.Healthy, new ProbeOutcome(T0, true, "HTTP 200"));

        Assert.Equal("active probe: timed out", HealthTracker.ReasonOf(probe, down: true));
        Assert.Equal("passive check: too many failed requests", HealthTracker.ReasonOf(passive, down: true));
        Assert.Equal("active probe: HTTP 200", HealthTracker.ReasonOf(healthy, down: false));
        Assert.Null(HealthTracker.ReasonOf(passive, down: false));
    }
}
