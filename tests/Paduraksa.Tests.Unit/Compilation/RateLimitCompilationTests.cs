using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.RateLimiting;
using Paduraksa.Proxy.Snapshot;

using static Paduraksa.Tests.Unit.Config.ConfigSamples;

namespace Paduraksa.Tests.Unit.Compilation;

/// <summary>
/// The compiler's half of the rate limit: one slot per (policy, host) pair, stable across compiles; the pair's
/// parameters in the snapshot's registrations; a route's own policy its own pair; nothing registered for a disabled or
/// route-less host or a policy the model doesn't carry; and no slot at all for a compile that will never be published.
/// </summary>
public sealed class RateLimitCompilationTests
{
    private static readonly RateLimitPolicyId Shared = ApiPolicy;
    private static readonly RateLimitPolicyId Own = OtherPolicy;
    private static readonly HostId One = Id<HostId>(11);
    private static readonly HostId Two = Id<HostId>(12);

    [Fact]
    public void OnePairPerPolicyAndHostWithStableSlots()
    {
        RateLimitRuntime runtime = new(new RateLimitTable(64));
        var first = Compile(runtime, Model(new RateLimitParameters(10, 20), new RateLimitParameters(1, 2)));

        Assert.Equal(3, first.RateLimitRegistrations.Length);
        var one = Assert.Single(first.RateLimitRegistrations, r => r.Identity == $"{Shared}/{One}");
        var two = Assert.Single(first.RateLimitRegistrations, r => r.Identity == $"{Shared}/{Two}");
        var own = Assert.Single(first.RateLimitRegistrations, r => r.Identity == $"{Own}/{One}");
        Assert.NotEqual(one.Slot, two.Slot);
        Assert.NotEqual(one.Slot, own.Slot);

        // An edit to the numbers reuses the pair's slot: its buckets survive (PLAN §6.3).
        var second = Compile(runtime, Model(new RateLimitParameters(1, 3), new RateLimitParameters(2, 4)));
        var edited = Assert.Single(second.RateLimitRegistrations, r => r.Identity == one.Identity);
        Assert.Equal(one.Slot, edited.Slot);
        Assert.Equal(new RateLimitParameters(1, 3), edited.Parameters);
    }

    [Fact]
    public void ADisabledHostRegistersNothing()
    {
        RateLimitRuntime runtime = new(new RateLimitTable(64));
        ProxyConfigModel model = Model(new RateLimitParameters(1, 1), new RateLimitParameters(1, 1));
        model = model with { Hosts = [model.Hosts[0] with { Enabled = false }, model.Hosts[1]] };

        var snapshot = Compile(runtime, model);

        Assert.DoesNotContain(snapshot.RateLimitRegistrations, r => r.Identity.Contains(One.ToString()!, StringComparison.Ordinal));
    }

    [Fact]
    public void ADraftCompileTakesNoSlotForANewPair()
    {
        RateLimitRuntime runtime = new(new RateLimitTable(64));
        ProxyConfigModel model = Model(new RateLimitParameters(1, 1), new RateLimitParameters(1, 1));

        // A draft, a dry run or a simulation: no publish would ever retire a slot it allocated, so it allocates none.
        var draft = SnapshotCompiler.Compile(
            model,
            new SnapshotCompilerOptions { RateLimits = runtime, RateLimitsWillBePublished = false });

        Assert.True(draft.Succeeded);
        Assert.Empty(draft.Snapshot!.RateLimitRegistrations);
        Assert.False(runtime.Slots.TryGetSlot($"{Shared}/{One}", out _));
        Assert.False(runtime.Slots.TryGetSlot($"{Own}/{One}", out _));

        // The live compile that follows starts the pairs on their slots as if the draft had never run.
        var live = Compile(runtime, model);
        Assert.True(runtime.Slots.TryGetSlot($"{Shared}/{One}", out var slot));
        Assert.Contains(live.RateLimitRegistrations, r => r.Slot == slot && r.Identity == $"{Shared}/{One}");
    }

    [Fact]
    public void ARedirectHostRegistersNothing()
    {
        // A Redirect host has no routes, so its rate-limit setting is inert; it must not keep a pair's slot alive either.
        RateLimitRuntime runtime = new(new RateLimitTable(64));
        ProxyConfigModel model = Model(new RateLimitParameters(1, 1), new RateLimitParameters(1, 1));
        model = model with
        {
            Hosts =
            [
                new HostSpec
                {
                    Id = One,
                    Names = ["old.test"],
                    Https = false,
                    Kind = HostKind.Redirect,
                    RateLimit = new HostRateLimitSpec { Enabled = true, PolicyId = Shared },
                    Redirect = new RedirectSpec { Target = "https://one.test" },
                },
            ],
        };

        var snapshot = Compile(runtime, model);

        Assert.Empty(snapshot.RateLimitRegistrations);
    }

    [Fact]
    public void AReferenceToAMissingPolicyIsACompileError()
    {
        RateLimitRuntime runtime = new(new RateLimitTable(64));
        ProxyConfigModel model = Model(new RateLimitParameters(1, 1), new RateLimitParameters(1, 1));
        model = model with { RateLimitPolicies = [model.RateLimitPolicies[0]] };

        var result = SnapshotCompiler.Compile(model, new SnapshotCompilerOptions { RateLimits = runtime });

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Message.Contains(Own.ToString()!, StringComparison.Ordinal));
    }

    /// <summary>Host one takes the shared policy on its root and its own policy on <c>/own</c>; host two shares it.</summary>
    private static ProxyConfigModel Model(RateLimitParameters shared, RateLimitParameters own) => new()
    {
        Upstreams = [new UpstreamSpec { Id = Id<UpstreamId>(1), Destinations = [new DestinationSpec(Id<DestinationId>(1), "http://127.0.0.1:1")] }],
        RateLimitPolicies =
        [
            new RateLimitPolicySpec { Id = Shared, Name = "shared", Parameters = shared },
            new RateLimitPolicySpec { Id = Own, Name = "own", Parameters = own },
        ],
        Hosts =
        [
            new HostSpec
            {
                Id = One,
                Names = ["one.test"],
                Https = false,
                RateLimit = new HostRateLimitSpec { Enabled = true, PolicyId = Shared },
                Routes =
                [
                    new RouteSpec { Id = Id<RouteId>(11), Upstream = Id<UpstreamId>(1), RateLimit = Shared },
                    new RouteSpec { Id = Id<RouteId>(12), Upstream = Id<UpstreamId>(1), Path = "/own", RateLimit = Own, RateLimitRouteOverride = true },
                ],
            },
            new HostSpec
            {
                Id = Two,
                Names = ["two.test"],
                Https = false,
                Routes = [new RouteSpec { Id = Id<RouteId>(13), Upstream = Id<UpstreamId>(1), RateLimit = Shared, RateLimitRouteOverride = true }],
            },
        ],
    };

    private static RuntimeSnapshot Compile(RateLimitRuntime runtime, ProxyConfigModel model)
    {
        var result = SnapshotCompiler.Compile(model, new SnapshotCompilerOptions { RateLimits = runtime });
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Errors));
        return result.Snapshot!;
    }
}
