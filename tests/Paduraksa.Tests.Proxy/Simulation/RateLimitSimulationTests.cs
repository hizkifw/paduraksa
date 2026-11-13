using System.Net;

using Microsoft.AspNetCore.Http;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.RateLimiting;
using Paduraksa.Proxy.Simulation;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;

namespace Paduraksa.Tests.Proxy.Simulation;

/// <summary>
/// The request simulator's rate-limit step: it reports what a request would get and leaves the bucket exactly as it found
/// it (docs/design/hot-path.md "Request simulator", "stateful policies must not mutate when simulated").
/// </summary>
public sealed class RateLimitSimulationTests
{
    [Fact]
    public void SimulatingARateLimitReportsTheVerdictAndSpendsNothing()
    {
        RateLimitRuntime runtime = new(new RateLimitTable(64));
        RateLimitPolicyId policy = TestIds.RateLimitPolicy("sim");
        var snapshot = TestSnapshots.Bound(Model(policy, "sim", new RateLimitParameters(1.0 / 60, 2)), options: new SnapshotCompilerOptions { RateLimits = runtime });
        GateProbe probe = Probe;

        // Two simulations in a row see the same full bucket: neither took a token.
        Assert.Contains("would allow, 1 token left", RateLimitStep(snapshot, probe), StringComparison.Ordinal);
        Assert.Contains("would allow, 1 token left", RateLimitStep(snapshot, probe), StringComparison.Ordinal);
        Assert.Contains("policy 'sim'", RateLimitStep(snapshot, probe), StringComparison.Ordinal);
        Assert.Contains("keyed by client IP", RateLimitStep(snapshot, probe), StringComparison.Ordinal);

        // Two real requests spend the burst, so a simulation now reports the refusal.
        using SnapshotStore store = new();
        store.Publish(snapshot);
        GateMiddleware gate = new(store, new TrustedProxyClientAddressSource());
        var context = Context();
        Assert.Equal(GateAction.Forward, gate.Decide(context).Action);
        Assert.Equal(GateAction.Forward, gate.Decide(context).Action);
        Assert.Contains("would refuse, retry after", RateLimitStep(snapshot, probe), StringComparison.Ordinal);
    }

    [Fact]
    public void ANewPolicyInAnUnpublishedDraftReportsAFullBucketAndTakesNoSlot()
    {
        RateLimitRuntime runtime = new(new RateLimitTable(64));
        RateLimitPolicyId policy = TestIds.RateLimitPolicy("draft");
        var draft = TestSnapshots.Bound(
            Model(policy, "draft", new RateLimitParameters(1, 1)),
            options: new SnapshotCompilerOptions { RateLimits = runtime, WillBePublished = false });

        Assert.Contains("would allow, full burst (new policy)", RateLimitStep(draft, Probe), StringComparison.Ordinal);
        Assert.False(runtime.Slots.TryGetSlot($"{policy}/{TestIds.Host("rl")}", out _));
    }

    private static GateProbe Probe => new() { Host = "rl.test", Path = "/", ClientAddress = IPAddress.Parse("192.0.2.10") };

    private static ProxyConfigModel Model(RateLimitPolicyId policy, string name, RateLimitParameters parameters) => new()
    {
        Upstreams = [new UpstreamSpec { Id = TestIds.Upstream("web"), Destinations = [new DestinationSpec(TestIds.Destination("web-d0"), "http://127.0.0.1:1")] }],
        RateLimitPolicies = [new RateLimitPolicySpec { Id = policy, Name = name, Parameters = parameters }],
        Hosts =
        [
            new HostSpec
            {
                Id = TestIds.Host("rl"),
                Names = ["rl.test"],
                Https = false,
                Routes = [new RouteSpec { Id = TestIds.Route("root"), Upstream = TestIds.Upstream("web"), RateLimit = policy, RateLimitRouteOverride = true }],
            },
        ],
    };

    private static string RateLimitStep(RuntimeSnapshot snapshot, GateProbe probe)
    {
        var step = Assert.Single(GateSimulator.Run(snapshot, probe).Steps, s => s.Kind == GateStepKind.RoutePolicy && s.Summary.Contains("rateLimit:", StringComparison.Ordinal));
        return step.Summary;
    }

    private static DefaultHttpContext Context()
    {
        DefaultHttpContext context = new();
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
        context.Request.Method = "GET";
        context.Request.Path = "/";
        context.Request.Host = new HostString("rl.test");
        return context;
    }
}
