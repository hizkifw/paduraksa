using System.Runtime.CompilerServices;

using Microsoft.AspNetCore.Http;

using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Net;
using Paduraksa.Tests.Proxy.Testing;

namespace Paduraksa.Tests.Proxy.Gate;

public sealed class PolicyTests
{
    private static readonly PrecomputedResponse Forbidden = PrecomputedResponse.Text(403, "no\n");

    /// <summary>The verdict struct stays 24 bytes: two references and the action, with no boxed value.</summary>
    [Fact]
    public void DecisionIsThreeMachineWords() => Assert.Equal(24, Unsafe.SizeOf<PolicyDecision>());

    [Fact]
    public void AnUninitialisedGateDecisionAborts()
    {
        Assert.Equal(GateAction.Abort, default(GateDecision).Action);
        Assert.Equal(GateDecision.Abort, default);
        Assert.Equal(GateAction.Forward, GateDecision.Forward.Action);
    }

    [Fact]
    public void DecisionsCompareByActionAndResponse()
    {
        Assert.Equal(PolicyAction.Continue, default(PolicyDecision).Action);
        Assert.Equal(PolicyDecision.Continue, default);
        Assert.Equal(PolicyDecision.Respond(Forbidden), PolicyDecision.Respond(Forbidden));
        Assert.NotEqual(PolicyDecision.Respond(Forbidden), PolicyDecision.Drop);
        Assert.Throws<ArgumentNullException>(() => PolicyDecision.Respond(null!));
    }

    [Fact]
    public void StaticPolicyAlwaysReturnsItsDecision()
    {
        GateRequestState state = new();
        var context = TestContexts.Create("a.test");

        Assert.Equal(PolicyDecision.Drop, Evaluate(new StaticPolicy(PolicyDecision.Drop), context, state));
        Assert.Equal(PolicyDecision.Respond(Forbidden), Evaluate(new StaticPolicy(PolicyDecision.Respond(Forbidden)), context, state));
    }

    [Theory]
    [InlineData("192.0.2.1", true)]
    [InlineData("::ffff:192.0.2.1", true)] // the IPv4-mapped spelling (TCP on [::]) is the same client
    [InlineData("10.200.0.1", true)]
    [InlineData("2001:db8::1", true)]
    [InlineData("192.0.2.2", false)]
    [InlineData("2001:db9::1", false)]
    public void AllowListUsesTheResolvedClientAddress(string client, bool allowed)
    {
        IpAllowListPolicy policy = new(CidrSet.Parse(["192.0.2.1", "10.0.0.0/8", "2001:db8::/32"]), PolicyDecision.Respond(Forbidden));
        var context = TestContexts.Create("a.test");
        GateRequestState state = new() { ClientIp = new ClientIpResolution(IpKey.Parse(client), ClientIpSource.Socket, ClientIpOutcome.PeerNotTrusted) };

        Assert.Equal(allowed ? PolicyDecision.Continue : PolicyDecision.Respond(Forbidden), Evaluate(policy, context, state));
    }

    [Fact]
    public void AllowListDenialMustStopTheRequest() =>
        Assert.Throws<ArgumentException>(() => new IpAllowListPolicy(CidrSet.Empty, PolicyDecision.Continue));

    [Fact]
    public void AllowListDoesNotAllocate()
    {
        IpAllowListPolicy policy = new(CidrSet.Parse(["192.0.2.0/24", "2001:db8::/32"]), PolicyDecision.Respond(Forbidden));
        GateRequestState state = new() { ClientIp = new ClientIpResolution(IpKey.Parse("2001:db8::7"), ClientIpSource.Socket, ClientIpOutcome.PeerNotTrusted) };
        (IpAllowListPolicy Policy, DefaultHttpContext Context, GateRequestState State) run = (policy, TestContexts.Create("a.test"), state);
        AllocAssert.Zero(run, static r => _ = Evaluate(r.Policy, r.Context, r.State), "IpAllowListPolicy.Evaluate");
    }

    [Fact]
    public void TheFormattedClientAddressIsCachedWhileTheClientRepeats()
    {
        GateRequestState state = new() { ClientIp = new ClientIpResolution(IpKey.Parse("::ffff:192.0.2.1"), ClientIpSource.Socket, ClientIpOutcome.PeerNotTrusted) };
        var first = state.FormattedClientAddress();
        Assert.Equal("192.0.2.1", first);

        state.Reset();
        state.ClientIp = new ClientIpResolution(IpKey.Parse("192.0.2.1"), ClientIpSource.Socket, ClientIpOutcome.PeerNotTrusted);
        Assert.Same(first, state.FormattedClientAddress());
        AllocAssert.Zero(state, static s => _ = s.FormattedClientAddress(), "FormattedClientAddress (repeat)");

        state.ClientIp = new ClientIpResolution(IpKey.Parse("2001:db8::1"), ClientIpSource.Socket, ClientIpOutcome.PeerNotTrusted);
        Assert.Equal("2001:db8::1", state.FormattedClientAddress());
    }

    private static PolicyDecision Evaluate(IRequestPolicy policy, HttpContext context, GateRequestState state)
    {
        RequestView view = new(context, state);
        return policy.Evaluate(in view);
    }
}
