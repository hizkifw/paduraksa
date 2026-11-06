using System.Net;
using System.Runtime.CompilerServices;

using Microsoft.AspNetCore.Http;

using Paduraksa.Proxy.Gate;
using Paduraksa.Tests.Proxy.Testing;

namespace Paduraksa.Tests.Proxy.Gate;

public sealed class PolicyTests
{
    private static readonly PrecomputedResponse Forbidden = PrecomputedResponse.Text(403, "no\n");

    [Fact]
    public void DecisionIsSixteenBytes() => Assert.Equal(16, Unsafe.SizeOf<PolicyDecision>());

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
        DefaultHttpContext context = TestContexts.Create("a.test");

        Assert.Equal(PolicyDecision.Drop, Evaluate(new StaticPolicy(PolicyDecision.Drop), context, state));
        Assert.Equal(PolicyDecision.Respond(Forbidden), Evaluate(new StaticPolicy(PolicyDecision.Respond(Forbidden)), context, state));
    }

    [Fact]
    public void AllowListUsesTheResolvedClientAddress()
    {
        UInt128 allowed = ClientAddressKey.From(IPAddress.Parse("192.0.2.1"));
        IpAllowListPolicy policy = new(a => a == allowed, PolicyDecision.Respond(Forbidden));
        DefaultHttpContext context = TestContexts.Create("a.test");
        GateRequestState state = new();

        state.ClientAddress = allowed;
        Assert.Equal(PolicyDecision.Continue, Evaluate(policy, context, state));

        state.ClientAddress = ClientAddressKey.From(IPAddress.Parse("192.0.2.2"));
        Assert.Equal(PolicyDecision.Respond(Forbidden), Evaluate(policy, context, state));
    }

    [Fact]
    public void AllowListDenialMustStopTheRequest() =>
        Assert.Throws<ArgumentException>(() => new IpAllowListPolicy(static _ => true, PolicyDecision.Continue));

    [Theory]
    [InlineData("192.0.2.1", "::ffff:192.0.2.1")]
    [InlineData("::ffff:192.0.2.1", "::ffff:192.0.2.1")]
    [InlineData("2001:db8::1", "2001:db8::1")]
    [InlineData("::1", "::1")]
    public void ClientAddressKeysAreIPv4MappedIPv6(string address, string expected)
    {
        UInt128 key = ClientAddressKey.From(IPAddress.Parse(address));
        byte[] bytes = IPAddress.Parse(expected).GetAddressBytes();
        UInt128 expectedKey = new(BitConverter.ToUInt64([.. bytes[..8].Reverse()]), BitConverter.ToUInt64([.. bytes[8..].Reverse()]));

        Assert.Equal(expectedKey, key);
    }

    [Fact]
    public void ClientAddressConversionDoesNotAllocate()
    {
        IPAddress[] addresses = [IPAddress.Parse("192.0.2.1"), IPAddress.Parse("2001:db8::1"), IPAddress.IPv6Loopback];
        AllocAssert.Zero(
            addresses,
            static a =>
            {
                foreach (IPAddress address in a)
                {
                    _ = ClientAddressKey.From(address);
                }
            },
            "ClientAddressKey.From");
    }

    private static PolicyDecision Evaluate(IRequestPolicy policy, HttpContext context, GateRequestState state)
    {
        RequestView view = new(context, state);
        return policy.Evaluate(in view);
    }
}
