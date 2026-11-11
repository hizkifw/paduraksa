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
