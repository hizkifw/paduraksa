using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

using Paduraksa.Proxy.Yarp;
using Paduraksa.Tests.Proxy.Testing;

using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Health;
using Yarp.ReverseProxy.Model;

namespace Paduraksa.Tests.Proxy.Yarp;

/// <summary>
/// The passive policy on a clock the test moves: the upstream's own reactivation period reaches YARP's updater (YARP's policy raises it to
/// its 60 s window), and failures from before a verdict, from while a destination was out, or from before the active check saw it back
/// don't take it out again.
/// </summary>
public sealed class PassiveFailureRatePolicyTests
{
    private const int Threshold = 10;

    private readonly ManualClock _clock = new();
    private readonly RecordingUpdater _updater = new();
    private readonly DestinationState _destination = new("d");

    private PassiveFailureRatePolicy Policy(double rateLimit = 0.3) => new(
        Options.Create(new TransportFailureRateHealthPolicyOptions { MinimalTotalCountThreshold = Threshold, DefaultFailureRateLimit = rateLimit }),
        _clock,
        _updater);

    private static ClusterState Cluster(TimeSpan? reactivation) => new("c", new ClusterModel(
        new ClusterConfig
        {
            ClusterId = "c",
            HealthCheck = new HealthCheckConfig { Passive = new PassiveHealthCheckConfig { Enabled = true, ReactivationPeriod = reactivation } },
        },
        new HttpMessageInvoker(new HttpClientHandler())));

    private static DefaultHttpContext Request(bool failed)
    {
        DefaultHttpContext context = new();
        if (failed)
        {
            context.Features.Set<IForwarderErrorFeature>(new ErrorFeature(ForwarderError.Request));
        }

        return context;
    }

    private void Send(PassiveFailureRatePolicy policy, ClusterState cluster, int count, bool failed)
    {
        for (var i = 0; i < count; i++)
        {
            policy.RequestProxied(Request(failed), cluster, _destination);
        }
    }

    [Fact]
    public void ADestinationTakenOutGetsTheUpstreamsOwnReactivationPeriod()
    {
        var policy = Policy();
        var cluster = Cluster(TimeSpan.FromSeconds(2));

        Send(policy, cluster, Threshold - 1, failed: true);
        Assert.NotEqual(DestinationHealth.Unhealthy, _destination.Health.Passive);
        Send(policy, cluster, 1, failed: true);

        Assert.Equal(DestinationHealth.Unhealthy, _destination.Health.Passive);
        Assert.Equal((DestinationHealth.Unhealthy, TimeSpan.FromSeconds(2)), _updater.Calls[^1]);
    }

    [Fact]
    public void WithoutAPeriodItIsYarpsDefaultMinute()
    {
        var policy = Policy();
        Send(policy, Cluster(null), Threshold, failed: true);

        Assert.Equal((DestinationHealth.Unhealthy, TimeSpan.FromMinutes(1)), _updater.Calls[^1]);
    }

    [Fact]
    public void AfterReactivationTheOutagesFailuresDoNotTakeItOutAgain()
    {
        var policy = Policy();
        var cluster = Cluster(TimeSpan.FromSeconds(2));
        Send(policy, cluster, Threshold, failed: true);

        // Requests in flight when it was taken out end while it is out; they aren't recorded.
        Send(policy, cluster, 50, failed: true);
        _clock.Advance(TimeSpan.FromSeconds(2));
        _destination.Health.Passive = DestinationHealth.Unknown; // YARP's reactivation

        Send(policy, cluster, 1, failed: true);
        Assert.Equal(DestinationHealth.Unknown, _destination.Health.Passive);
        Send(policy, cluster, 1, failed: false);
        Assert.Equal(DestinationHealth.Healthy, _destination.Health.Passive);
    }

    [Fact]
    public void TheActiveCheckSeeingItBackForgetsTheOutageAndLiftsThePassiveVerdict()
    {
        var policy = Policy();
        var cluster = Cluster(TimeSpan.FromMinutes(10));

        // Failures that didn't reach a verdict before the active check took it out.
        Send(policy, cluster, Threshold - 1, failed: true);
        policy.ActiveRecovered(cluster, _destination);
        Send(policy, cluster, 1, failed: true);
        Assert.NotEqual(DestinationHealth.Unhealthy, _destination.Health.Passive);

        // A passive verdict from the same outage is lifted at once, not after the ten minutes.
        Send(policy, cluster, Threshold, failed: true);
        Assert.Equal(DestinationHealth.Unhealthy, _destination.Health.Passive);
        policy.ActiveRecovered(cluster, _destination);
        Assert.Equal(DestinationHealth.Unknown, _destination.Health.Passive);
    }

    [Fact]
    public void FailuresOlderThanTheWindowAreForgotten()
    {
        var policy = Policy();
        var cluster = Cluster(TimeSpan.FromSeconds(2));
        Send(policy, cluster, Threshold - 1, failed: true);

        _clock.Advance(TimeSpan.FromSeconds(61));
        Send(policy, cluster, 1, failed: true);
        Assert.NotEqual(DestinationHealth.Unhealthy, _destination.Health.Passive);

        // Within the window they add up.
        _clock.Advance(TimeSpan.FromSeconds(30));
        Send(policy, cluster, Threshold - 1, failed: true);
        Assert.Equal(DestinationHealth.Unhealthy, _destination.Health.Passive);
    }

    [Fact]
    public void ARateUnderTheLimitKeepsItIn()
    {
        var policy = Policy();
        var cluster = Cluster(TimeSpan.FromSeconds(2));
        for (var i = 0; i < 100; i++)
        {
            Send(policy, cluster, 1, failed: i % 5 == 0);
        }

        Assert.Equal(DestinationHealth.Healthy, _destination.Health.Passive);
    }

    [Fact]
    public void AClientThatWentAwayIsNotTheDestinationsFailure()
    {
        var policy = Policy();
        var cluster = Cluster(TimeSpan.FromSeconds(2));
        using CancellationTokenSource aborted = new();
        aborted.Cancel();
        for (var i = 0; i < Threshold * 2; i++)
        {
            var context = Request(failed: true);
            context.RequestAborted = aborted.Token;
            policy.RequestProxied(context, cluster, _destination);
        }

        Assert.NotEqual(DestinationHealth.Unhealthy, _destination.Health.Passive);
    }

    [Fact]
    public void ARecordedRequestAllocatesNothing()
    {
        // A limit no rate reaches, so failures are summed every time and never change the verdict.
        var policy = Policy(rateLimit: 2);
        var cluster = Cluster(TimeSpan.FromSeconds(2));
        var ok = Request(failed: false);
        var failed = Request(failed: true);
        Send(policy, cluster, 1, failed: false);

        var result = AllocAssert.Measure(
            (policy, cluster, ok, failed, _destination),
            static state =>
            {
                state.policy.RequestProxied(state.ok, state.cluster, state._destination);
                state.policy.RequestProxied(state.failed, state.cluster, state._destination);
            });

        Assert.Equal(0, result.TotalBytes);
    }

    [Fact]
    public void ActiveRecoveryReachesThePassivePolicyThroughTheProbeRecorder()
    {
        var policy = Policy();
        var cluster = Cluster(TimeSpan.FromMinutes(10));
        Send(policy, cluster, Threshold, failed: true);
        _destination.Health.Active = DestinationHealth.Unhealthy;
        FlippingActivePolicy inner = new();
        ProbeRecordingPolicy recorder = new(inner, new ProbeLog(_clock), policy);

        recorder.ProbingCompleted(cluster, [new DestinationProbingResult(_destination, null, new HttpRequestException("refused"))]);
        Assert.Equal(DestinationHealth.Unhealthy, _destination.Health.Passive); // still down to the probe: nothing lifted

        inner.Next = DestinationHealth.Healthy;
        recorder.ProbingCompleted(cluster, [new DestinationProbingResult(_destination, null, new HttpRequestException("refused"))]);
        Assert.Equal(DestinationHealth.Unknown, _destination.Health.Passive);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks = TimeSpan.TicksPerDay;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    private sealed class RecordingUpdater : IDestinationHealthUpdater
    {
        public List<(DestinationHealth Health, TimeSpan Period)> Calls { get; } = [];

        public void SetActive(ClusterState cluster, IEnumerable<NewActiveDestinationHealth> newHealthPairs)
        {
            foreach (var pair in newHealthPairs)
            {
                pair.Destination.Health.Active = pair.NewActiveHealth;
            }
        }

        public void SetPassive(ClusterState cluster, DestinationState destination, DestinationHealth newHealth, TimeSpan reactivationPeriod)
        {
            if (destination.Health.Passive != newHealth)
            {
                destination.Health.Passive = newHealth;
                Calls.Add((newHealth, reactivationPeriod));
            }
        }
    }

    private sealed class FlippingActivePolicy : IActiveHealthCheckPolicy
    {
        public DestinationHealth Next { get; set; } = DestinationHealth.Unhealthy;

        public string Name => "Flipping";

        public void ProbingCompleted(ClusterState cluster, IReadOnlyList<DestinationProbingResult> probingResults)
        {
            foreach (var result in probingResults)
            {
                result.Destination.Health.Active = Next;
            }
        }
    }

    private sealed class ErrorFeature(ForwarderError error) : IForwarderErrorFeature
    {
        public ForwarderError Error { get; } = error;

        public Exception? Exception => null;
    }
}
