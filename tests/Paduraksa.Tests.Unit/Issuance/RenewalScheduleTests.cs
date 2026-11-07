using Paduraksa.Acme;
using Paduraksa.Application.Issuance;

namespace Paduraksa.Tests.Unit.Issuance;

/// <summary>The scheduler's arithmetic (PLAN §9.3): ARI against the lifetime rule, ARI recheck bounds, and the backoff per failure kind.</summary>
public sealed class RenewalScheduleTests
{
    private static readonly AcmeIssuanceOptions Options = new();
    private static readonly DateTimeOffset NotBefore = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

    private static AcmeRenewalInfo Window(DateTimeOffset start, DateTimeOffset end, TimeSpan? retryAfter = null) => new(start, end, null, retryAfter);

    [Theory]
    [InlineData(0d, 30d)]
    [InlineData(0.5d, 31.5d)]
    [InlineData(1d, 33d)]
    public void WithoutAriARenewalStartsAThirdOfTheLifetimeBeforeExpiryMovedEarlierByJitter(double random, double daysBeforeExpiry)
    {
        DateTimeOffset notAfter = NotBefore.AddDays(90);

        DateTimeOffset at = RenewalSchedule.NextRenewalAt(NotBefore, notAfter, null, random, Options);

        Assert.Equal(notAfter - TimeSpan.FromDays(daysBeforeExpiry), at);
    }

    [Fact]
    public void AShortLivedCertificateRenewsAThirdOfItsLifeBeforeExpiry()
    {
        DateTimeOffset notAfter = NotBefore.AddDays(6);

        Assert.Equal(notAfter - TimeSpan.FromDays(2), RenewalSchedule.NextRenewalAt(NotBefore, notAfter, null, 0d, Options));
        // At most a tenth of that third earlier: 4.8 hours of 2 days.
        Assert.Equal(notAfter - TimeSpan.FromDays(2) - TimeSpan.FromHours(4.8), RenewalSchedule.NextRenewalAt(NotBefore, notAfter, null, 1d, Options));
    }

    [Theory]
    [InlineData(0d, 0)]
    [InlineData(0.5d, 12)]
    [InlineData(1d, 24)]
    public void WithAriTheRenewalIsAPointInTheWindow(double random, double hoursIn)
    {
        DateTimeOffset start = NotBefore.AddDays(40);
        AcmeRenewalInfo info = Window(start, start.AddDays(1));

        DateTimeOffset at = RenewalSchedule.NextRenewalAt(NotBefore, NotBefore.AddDays(90), info, random, Options);

        Assert.Equal(start.AddHours(hoursIn), at);
    }

    [Fact]
    public void AnAriWindowLateInTheLifetimeIsCutAtTheLifetimeRuleAndAWindowInThePastIsDueNow()
    {
        DateTimeOffset notAfter = NotBefore.AddDays(90);

        // After expiry, or just before it: never later than a third of the lifetime before expiry, so a CA's window can't cause an outage.
        Assert.Equal(notAfter.AddDays(-30), RenewalSchedule.NextRenewalAt(NotBefore, notAfter, Window(notAfter.AddDays(1), notAfter.AddDays(2)), 0.5, Options));
        Assert.Equal(notAfter.AddDays(-30), RenewalSchedule.NextRenewalAt(NotBefore, notAfter, Window(notAfter.AddDays(-2), notAfter.AddDays(-1)), 0.5, Options));
        Assert.Equal(notAfter.AddDays(-40), RenewalSchedule.NextRenewalAt(NotBefore, notAfter, Window(notAfter.AddDays(-41), notAfter.AddDays(-39)), 0.5, Options));
        DateTimeOffset past = RenewalSchedule.NextRenewalAt(NotBefore, notAfter, Window(Now.AddDays(-3), Now.AddDays(-2)), 0.5, Options);
        Assert.True(past < Now);
    }

    [Fact]
    public void APickInsideTheNewWindowIsKeptAndAnythingElseIsPickedAgain()
    {
        AcmeRenewalInfo info = Window(Now, Now.AddDays(2));

        Assert.True(RenewalSchedule.KeepsPick(Now.AddDays(1), info));
        Assert.True(RenewalSchedule.KeepsPick(Now, info));
        Assert.True(RenewalSchedule.KeepsPick(Now.AddDays(2), info));
        Assert.False(RenewalSchedule.KeepsPick(Now.AddDays(-1), info));
        Assert.False(RenewalSchedule.KeepsPick(Now.AddDays(3), info));
        Assert.False(RenewalSchedule.KeepsPick(null, info));
    }

    [Theory]
    [InlineData(null, 6 * 60)]
    [InlineData(1d, 15)]
    [InlineData(120d, 120)]
    [InlineData(4320d, 24 * 60)]
    public void TheAriRecheckFollowsRetryAfterWithinBounds(double? minutes, double expectedMinutes)
    {
        TimeSpan? retryAfter = minutes is { } m ? TimeSpan.FromMinutes(m) : null;

        Assert.Equal(Now.AddMinutes(expectedMinutes), RenewalSchedule.NextAriCheck(Now, retryAfter, Options));
    }

    [Theory]
    [InlineData(AcmeFailureKind.Transient, 1, 5)]
    [InlineData(AcmeFailureKind.Transient, 2, 10)]
    [InlineData(AcmeFailureKind.Transient, 3, 20)]
    [InlineData(AcmeFailureKind.Transient, 8, 300)]
    [InlineData(AcmeFailureKind.Transient, 50, 300)]
    [InlineData(AcmeFailureKind.Permanent, 1, 60)]
    [InlineData(AcmeFailureKind.Permanent, 2, 120)]
    [InlineData(AcmeFailureKind.Permanent, 10, 20 * 60)]
    [InlineData(AcmeFailureKind.ProtocolViolation, 1, 30)]
    [InlineData(AcmeFailureKind.ProtocolViolation, 3, 120)]
    [InlineData(AcmeFailureKind.RateLimited, 1, 60)]
    [InlineData(AcmeFailureKind.RateLimited, 3, 240)]
    public void TheBackoffDoublesFromTheKindsBaseUpToItsCap(AcmeFailureKind kind, int attempts, double expectedMinutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), RenewalSchedule.Backoff(kind, attempts, null, 0.5, Options));
    }

    [Theory]
    [InlineData(0d, 0.8d)]
    [InlineData(0.5d, 1d)]
    [InlineData(1d, 1.2d)]
    public void TheBackoffIsJitteredByTwentyPercent(double random, double factor)
    {
        TimeSpan wait = RenewalSchedule.Backoff(AcmeFailureKind.Transient, 2, null, random, Options);

        Assert.Equal(TimeSpan.FromMinutes(10 * factor).Ticks, wait.Ticks, tolerance: 1);
    }

    [Theory]
    [InlineData(0d, 4d)]
    [InlineData(0.5d, 5d)]
    [InlineData(1d, 6d)]
    public void AtTheCapTheJitterIsSymmetricAroundAndBoundedByIt(double random, double hours)
    {
        // Once doubling would pass the cap, the wait is centred a sixth under it (cap / 1.2) and never goes above it.
        TimeSpan wait = RenewalSchedule.Backoff(AcmeFailureKind.Transient, 30, null, random, Options);

        Assert.Equal(TimeSpan.FromHours(hours).Ticks, wait.Ticks, tolerance: 1);
        Assert.True(wait <= Options.TransientBackoffCap);
    }

    [Fact]
    public void ARateLimitWaitsAtLeastForRetryAfterButNeverPastTheCap()
    {
        // Longer than the backoff: the CA's word wins, unjittered.
        Assert.Equal(TimeSpan.FromDays(3), RenewalSchedule.Backoff(AcmeFailureKind.RateLimited, 1, TimeSpan.FromDays(3), 0d, Options));
        // Shorter than the backoff: the backoff stays.
        Assert.Equal(TimeSpan.FromHours(1), RenewalSchedule.Backoff(AcmeFailureKind.RateLimited, 1, TimeSpan.FromMinutes(1), 0.5, Options));
        // Absurdly long: capped at a week.
        Assert.Equal(TimeSpan.FromDays(7), RenewalSchedule.Backoff(AcmeFailureKind.RateLimited, 1, TimeSpan.FromDays(400), 0.5, Options));
    }

    [Fact]
    public void RetryAfterMeansNothingForPermanentFailures()
    {
        Assert.Equal(TimeSpan.FromHours(1), RenewalSchedule.Backoff(AcmeFailureKind.Permanent, 1, TimeSpan.FromDays(3), 0.5, Options));
    }

    [Fact]
    public void ATransientFailuresRetryAfterIsAFloorBelowTheCap()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), RenewalSchedule.Backoff(AcmeFailureKind.Transient, 1, TimeSpan.FromSeconds(30), 0.5, Options));
        Assert.Equal(TimeSpan.FromHours(2), RenewalSchedule.Backoff(AcmeFailureKind.Transient, 1, TimeSpan.FromHours(2), 0.5, Options));
        Assert.Equal(Options.TransientBackoffCap, RenewalSchedule.Backoff(AcmeFailureKind.Transient, 1, TimeSpan.FromDays(3), 0.5, Options));
    }
}
