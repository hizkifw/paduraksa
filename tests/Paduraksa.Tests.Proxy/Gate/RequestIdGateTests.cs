using Microsoft.AspNetCore.Http;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Logging;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.Gate;

/// <summary>
/// <c>X-Request-Id</c>: honoured from a trusted proxy only, replaced otherwise, and always ours on the way out.
/// The trust decision is the forwarding headers' own (<see cref="ClientIpResolution.PeerTrusted"/>).
/// </summary>
public sealed class RequestIdGateTests : IDisposable
{
    private static readonly ProxyConfigModel TrustingGeneric = new()
    {
        Upstreams = [Upstream("web", "http://127.0.0.1:1/")],
        Hosts = [ProxyHost("app", "app.test", Route("app-root", "web"))],
        Defaults = new DefaultsSpec { TrustedProxies = new TrustedProxySpec { Mode = TrustedProxyMode.Generic, Proxies = ["127.0.0.0/8"] } },
    };

    private readonly SnapshotStore _store = new();

    public RequestIdGateTests() => _store.Publish(Bound(TrustingGeneric));

    public void Dispose() => _store.Dispose();

    private GateMiddleware Gate => new(_store, new TrustedProxyClientAddressSource());

    [Fact]
    public void ATrustedProxysIdIsKept()
    {
        DefaultHttpContext context = TestContexts.Create("app.test", "/", remote: System.Net.IPAddress.Loopback);
        context.Request.Headers[RequestIds.HeaderName] = "edge-1234";

        GateDecision decision = Gate.Decide(context);

        Assert.Equal(GateAction.Forward, decision.Action);
        GateRequestState state = context.Features.Get<IGateFeature>()!.State;
        Assert.Equal("edge-1234", state.RequestIdText());
        Assert.True(state.HasClientRequestId);
    }

    [Fact]
    public void AnUntrustedPeersIdIsReplaced()
    {
        DefaultHttpContext context = TestContexts.Create("app.test", "/", remote: System.Net.IPAddress.Parse("203.0.113.9"));
        context.Request.Headers[RequestIds.HeaderName] = "client-chosen";

        Gate.Decide(context);

        GateRequestState state = context.Features.Get<IGateFeature>()!.State;
        Assert.NotEqual("client-chosen", state.RequestIdText());
        Assert.False(state.HasClientRequestId);
        Assert.Equal(RequestIds.GeneratedLength, state.RequestIdText().Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("line\nbreak")]
    [InlineData("café")]
    public void ATrustedProxyCanStillSendAnInvalidValueItIsReplaced(string value)
    {
        DefaultHttpContext context = TestContexts.Create("app.test", "/", remote: System.Net.IPAddress.Loopback);
        context.Request.Headers[RequestIds.HeaderName] = value;

        Gate.Decide(context);

        GateRequestState state = context.Features.Get<IGateFeature>()!.State;
        Assert.False(state.HasClientRequestId);
        Assert.Equal(RequestIds.GeneratedLength, state.RequestIdText().Length);
    }

    [Fact]
    public void ARepeatedHeaderIsReplaced()
    {
        DefaultHttpContext context = TestContexts.Create("app.test", "/", remote: System.Net.IPAddress.Loopback);
        context.Request.Headers[RequestIds.HeaderName] = new Microsoft.Extensions.Primitives.StringValues(["one", "two"]);

        Gate.Decide(context);

        Assert.False(context.Features.Get<IGateFeature>()!.State.HasClientRequestId);
    }

    [Fact]
    public void AnOverlongValueIsReplaced()
    {
        DefaultHttpContext context = TestContexts.Create("app.test", "/", remote: System.Net.IPAddress.Loopback);
        context.Request.Headers[RequestIds.HeaderName] = new string('a', RequestIds.MaxLength + 1);

        Gate.Decide(context);

        Assert.False(context.Features.Get<IGateFeature>()!.State.HasClientRequestId);
    }

    [Fact]
    public void ARequestWithoutOneGetsAGeneratedId()
    {
        DefaultHttpContext context = TestContexts.Create("app.test", "/");

        Gate.Decide(context);

        GateRequestState state = context.Features.Get<IGateFeature>()!.State;
        Assert.False(state.HasClientRequestId);
        Assert.Equal(RequestIds.GeneratedLength, state.RequestIdText().Length);
        // The text is materialised once and shared.
        Assert.Same(state.RequestIdText(), state.RequestIdText());
    }

    [Fact]
    public void AcceptingAndGeneratingTheRequestIdAllocateNothing()
    {
        DefaultHttpContext trusted = TestContexts.Create("app.test", "/api", remote: System.Net.IPAddress.Loopback);
        trusted.Request.Headers[RequestIds.HeaderName] = "edge-1234";
        DefaultHttpContext untrusted = TestContexts.Create("app.test", "/api", remote: System.Net.IPAddress.Parse("203.0.113.9"));
        GateMiddleware gate = Gate;

        // The counter only: the string the header needs is materialised by InvokeAsync, outside the measured decision.
        AllocAssert.Zero(
            (gate, trusted, untrusted),
            static s =>
            {
                _ = s.gate.Decide(s.trusted);
                _ = s.gate.Decide(s.untrusted);
            },
            "request-ID adoption and generation in the gate");
    }
}
