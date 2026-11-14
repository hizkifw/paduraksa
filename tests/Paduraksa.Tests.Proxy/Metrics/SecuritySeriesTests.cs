using Paduraksa.Core.Model;
using Paduraksa.Proxy.AccessLists;
using Paduraksa.Proxy.ForwardAuth;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Metrics;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.RateLimiting;
using Paduraksa.Proxy.RuleSets;
using Paduraksa.Tests.Data;
using Paduraksa.Tests.Proxy.Testing;

namespace Paduraksa.Tests.Proxy.Metrics;

public sealed class SecuritySeriesTests
{
    // A minute boundary, so a test knows which minute each second lands in.
    private static readonly DateTimeOffset Start = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly AuthProviderId Provider = AuthProviderId.New();
    private static readonly RuleSetId Rules = RuleSetId.New();

    private sealed class Rig
    {
        public Rig(double bootSeconds = 0)
        {
            Clock = new ManualTimeProvider(Start.AddSeconds(bootSeconds));
            ForwardAuth = new ForwardAuthRuntime(Clock, null);
            RuleSets = new RuleSetRuntime();
            Sources = new SecuritySources(ProofOfWork, RateLimit, AccessLists, Connections, ForwardAuth, RuleSets);
            Sampler = new MetricSampler(new MetricsRegistry(processors: 2, timeProvider: Clock), Clock, new MetricRollupBacklog(), Sources);
        }

        public ManualTimeProvider Clock { get; }

        public ProofOfWorkStats ProofOfWork { get; } = new();

        public RateLimitStats RateLimit { get; } = new();

        public AccessListStats AccessLists { get; } = new();

        public ConnectionStats Connections { get; } = new();

        public ForwardAuthRuntime ForwardAuth { get; }

        public RuleSetRuntime RuleSets { get; }

        public SecuritySources Sources { get; }

        public MetricSampler Sampler { get; }

        public void TickAt(double seconds)
        {
            Clock.Now = Start.AddSeconds(seconds);
            Sampler.Tick();
        }

        public void TickEverySecond(int from, int to)
        {
            for (var second = from; second <= to; second++)
            {
                TickAt(second);
            }
        }

        public List<SecurityRollup> Minutes() => Sampler.SecurityRollups.Drain();
    }

    [Fact]
    public void EachMinuteHoldsWhatTheCountersGainedInItAndAQuietMinuteIsStillATotalsRow()
    {
        Rig rig = new();
        rig.ProofOfWork.CountChallengeServed();
        rig.ProofOfWork.CountChallengeServed();
        rig.RateLimit.CountLimited();
        rig.TickEverySecond(1, 30);
        rig.AccessLists.CountAddressRefused();
        rig.Connections.CountDenied(ConnectionTransport.Tcp);
        rig.TickEverySecond(31, 120);

        var minutes = rig.Minutes();

        Assert.Equal(2, minutes.Count);
        Assert.All(minutes, minute => Assert.Equal(SecurityRollupSubject.Total, minute.Subject));
        var first = minutes[0].Point;
        Assert.Equal(Start.ToUnixTimeSeconds(), first.Start);
        Assert.Equal(60, first.CoveredSeconds);
        Assert.Equal(2, first[SecurityCounter.ChallengesServed]);
        Assert.Equal(1, first[SecurityCounter.RateLimited]);
        Assert.Equal(1, first[SecurityCounter.AddressRefused]);
        Assert.Equal(1, first[SecurityCounter.ConnectionsDenied]);
        Assert.False(minutes[1].Point.HasCounts);
        Assert.Equal(60, minutes[1].Point.CoveredSeconds);
    }

    [Fact]
    public void AProviderAndARuleSetHaveTheirOwnSparseRowsAndAreInTheTotals()
    {
        Rig rig = new();
        var provider = rig.ForwardAuth.CountersFor(Provider);
        var set = rig.RuleSets.CountersFor(Rules);
        provider.CountAllowed();
        provider.CountDenied();
        set.Count(RuleAction.Deny);
        set.Count(RuleAction.Drop);
        set.Count(RuleAction.Drop);
        rig.TickEverySecond(1, 120);

        var minutes = rig.Minutes();

        var own = Assert.Single(minutes, minute => minute.Subject == SecurityRollupSubject.AuthProvider);
        Assert.Equal(Provider.ToString(), own.SubjectId);
        Assert.Equal(1, own.Point[SecurityCounter.SignInAllowed]);
        Assert.Equal(1, own.Point[SecurityCounter.SignInDenied]);
        var rules = Assert.Single(minutes, minute => minute.Subject == SecurityRollupSubject.RuleSet);
        Assert.Equal(Rules.ToString(), rules.SubjectId);
        Assert.Equal(2, rules.Point[SecurityCounter.RulesDropped]);
        var total = minutes.First(minute => minute.Subject == SecurityRollupSubject.Total).Point;
        Assert.Equal(1, total[SecurityCounter.SignInAllowed]);
        Assert.Equal(1, total[SecurityCounter.RulesDenied]);
        Assert.Equal(2, total[SecurityCounter.RulesDropped]);
    }

    [Fact]
    public void ALateTickSplitsItsGainOverTheMinutesItCrossed()
    {
        Rig rig = new();
        rig.TickEverySecond(1, 50);
        for (var i = 0; i < 20; i++)
        {
            rig.ProofOfWork.CountSolved();
        }

        // Ten seconds late, across the minute: the gain was served through the stall, half each side.
        rig.TickAt(70);
        rig.TickEverySecond(71, 120);

        var minutes = rig.Minutes();
        Assert.Equal([10L, 10L], minutes.Select(minute => minute.Point[SecurityCounter.Solved]));
        Assert.Equal([60, 60], minutes.Select(minute => minute.Point.CoveredSeconds));
    }

    [Fact]
    public void ALongGapWithNothingCountedIsNotObserved()
    {
        Rig rig = new();
        rig.TickEverySecond(1, 30);
        rig.TickAt(200);
        rig.TickEverySecond(201, 240);

        var minutes = rig.Minutes();

        Assert.Equal([Start.ToUnixTimeSeconds(), Start.ToUnixTimeSeconds() + 180], minutes.Select(minute => minute.Point.Start));
        Assert.Equal([30, 40], minutes.Select(minute => minute.Point.CoveredSeconds));
    }

    [Fact]
    public void AFlushWritesTheOpenMinuteAsItStands()
    {
        Rig rig = new();
        rig.TickEverySecond(1, 10);
        rig.RateLimit.CountLimited();
        rig.ForwardAuth.CountersFor(Provider).CountError();
        rig.Clock.Now = Start.AddSeconds(20);

        rig.Sampler.Flush();

        var minutes = rig.Minutes();
        var total = Assert.Single(minutes, minute => minute.Subject == SecurityRollupSubject.Total).Point;
        Assert.Equal(20, total.CoveredSeconds);
        Assert.Equal(1, total[SecurityCounter.RateLimited]);
        Assert.Equal(1, total[SecurityCounter.SignInErrors]);
        Assert.Equal(1, Assert.Single(minutes, minute => minute.Subject == SecurityRollupSubject.AuthProvider).Point[SecurityCounter.SignInErrors]);
    }

    [Fact]
    public void TheHourAddsTheSeededMinutesToWhatThisRunCountedAndForgetsThemAsTheyLeaveIt()
    {
        Rig rig = new(bootSeconds: 30);
        Span<long> before = stackalloc long[SecurityPoint.Count];
        before[(int)SecurityCounter.RateLimited] = 5;
        before[(int)SecurityCounter.SignInDenied] = 2;
        Span<long> provider = stackalloc long[SecurityPoint.Count];
        provider[(int)SecurityCounter.SignInDenied] = 2;
        var lastMinute = Start.ToUnixTimeSeconds() - 60;
        rig.Sampler.SeedSecurity(
        [
            new SecurityRollup(SecurityRollupSubject.Total, string.Empty, new SecurityPoint(lastMinute, 60, 60, before)),
            new SecurityRollup(SecurityRollupSubject.AuthProvider, Provider.ToString(), new SecurityPoint(lastMinute, 60, 60, provider)),
            new SecurityRollup(SecurityRollupSubject.AuthProvider, "not an id", new SecurityPoint(lastMinute, 60, 60, provider)),
        ]);
        rig.RateLimit.CountLimited();
        rig.TickEverySecond(31, 90);

        var hour = rig.Sampler.SummariseSecurity(SecurityRollupSubject.Total, TimeSpan.FromHours(1))[Guid.Empty];
        Assert.Equal(6, hour[SecurityCounter.RateLimited]);
        Assert.Equal(2, hour[SecurityCounter.SignInDenied]);
        Assert.Equal(2, Assert.Single(rig.Sampler.SummariseSecurity(SecurityRollupSubject.AuthProvider, TimeSpan.FromHours(1))).Value[SecurityCounter.SignInDenied]);

        rig.TickAt(3_600 + 30);
        Assert.Equal(1, rig.Sampler.SummariseSecurity(SecurityRollupSubject.Total, TimeSpan.FromHours(1))[Guid.Empty][SecurityCounter.RateLimited]);
        Assert.Empty(rig.Sampler.SummariseSecurity(SecurityRollupSubject.AuthProvider, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void ASteadyTickAllocatesNothingWithEverySecuritySourceAndAProviderAndASet()
    {
        Rig rig = new();
        var registry = new MetricsRegistry(processors: 2, timeProvider: rig.Clock);
        registry.SlotFor(MetricSubject.Host, HostId.New().Value);
        MetricSampler sampler = new(registry, rig.Clock, new MetricRollupBacklog(), rig.Sources);
        rig.ForwardAuth.CountersFor(Provider).CountAllowed();
        rig.RuleSets.CountersFor(Rules).Count(RuleAction.Deny);
        rig.Clock.Now = Start.AddSeconds(1);
        sampler.Tick();

        // Every tick inside one minute: buckets close into the ring, but no minute closes (that queues a row, which may allocate).
        AllocAssert.Zero(
            (rig, sampler),
            static state =>
            {
                state.rig.RateLimit.CountLimited();
                state.rig.Clock.Now += TimeSpan.FromSeconds(1);
                state.sampler.Tick();
            },
            "MetricSampler.Tick",
            warmup: 3,
            iterations: 50);
    }
}
