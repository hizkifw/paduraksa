using System.Text;

using Microsoft.AspNetCore.Http;

using Paduraksa.Core.Acme;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.Gate;

/// <summary>
/// The HTTP-01 answer in the gate: exact token, plain HTTP, answered before host lookup and the
/// host's policies, 0 B, and nothing else under the path is served from the store.
/// </summary>
public sealed class AcmeChallengeGateTests : IDisposable
{
    private const string Token = "evaGxfADs6pSRb2LAv9IZf17Dt3juxGJ-PCt92wr-oA";
    private const string KeyAuthorization = Token + ".nP1qzpXGymHBrUEepNY9HCsQk7K8KhRYsnPIqFzfSKs";

    private readonly SnapshotStore _store = new();
    private readonly AcmeChallengeStore _challenges = new();
    private readonly GateMiddleware _gate;

    public AcmeChallengeGateTests()
    {
        // A host that forces HTTPS and only lets one LAN address in: an unanswered challenge would be redirected or refused.
        ProxyConfigModel config = new()
        {
            Upstreams = [Upstream("web", "http://127.0.0.1:1/")],
            Hosts = [ProxyHost("secure", "secure.test", Route("secure-root", "web")) with { ForceHttps = true, Policies = [new IpAllowListPolicySpec(["10.0.0.5"])] }],
        };
        _store.Publish(Bound(config, options: Port8443));
        _gate = new GateMiddleware(_store, new TrustedProxyClientAddressSource(), _challenges);
        _challenges.Publish(Token, KeyAuthorization);
    }

    public void Dispose() => _store.Dispose();

    [Theory]
    [InlineData("secure.test")]
    [InlineData("unknown.test")]
    [InlineData(null)]
    public async Task APublishedTokenIsAnsweredBeforeHostRoutingAndPolicies(string? host)
    {
        DefaultHttpContext context = TestContexts.Create(host, "/.well-known/acme-challenge/" + Token);
        context.Response.Body = new MemoryStream();

        GateDecision decision = _gate.Decide(context);
        await GateMiddleware.CompleteAsync(context, decision, static _ => throw new InvalidOperationException("must not forward"));

        Assert.Equal(GateAction.Respond, decision.Action);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("text/plain", context.Response.ContentType);
        Assert.Equal(KeyAuthorization.Length, context.Response.ContentLength);
        Assert.Equal(KeyAuthorization, Encoding.ASCII.GetString(((MemoryStream)context.Response.Body).ToArray()));
    }

    [Fact]
    public async Task HeadIsAnsweredWithTheLengthButWithoutABody()
    {
        DefaultHttpContext context = TestContexts.Create("secure.test", "/.well-known/acme-challenge/" + Token, method: "HEAD");
        context.Response.Body = new MemoryStream();

        GateDecision decision = _gate.Decide(context);
        await GateMiddleware.CompleteAsync(context, decision, static _ => throw new InvalidOperationException("must not forward"));

        Assert.Equal(GateAction.Respond, decision.Action);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal(KeyAuthorization.Length, context.Response.ContentLength);
        Assert.Empty(((MemoryStream)context.Response.Body).ToArray());
    }

    [Theory]
    [InlineData("/.well-known/acme-challenge/unknown-token")]
    [InlineData("/.well-known/acme-challenge/")]
    [InlineData("/.well-known/acme-challenge")]
    [InlineData("/.well-known/acme-challenge/" + Token + "/")]
    [InlineData("/.well-known/acme-challenge/" + Token + "x")]
    [InlineData("/.well-known/acme-challenge/evaGxfADs6pSRb2LAv9IZf17Dt3juxGJ-PCt92wr-o")]
    [InlineData("/.well-known/acme-challenge/../" + Token)]
    [InlineData("/.WELL-KNOWN/acme-challenge/" + Token)]
    [InlineData("/.well-known/acme-challenge/" + "EVAGXFADS6PSRB2LAV9IZF17DT3JUXGJ-PCT92WR-OA")]
    public void AnythingButAnExactPublishedTokenFallsThrough(string path)
    {
        // On this host a fall-through meets its IP allow list (403), not a challenge answer.
        DefaultHttpContext context = TestContexts.Create("secure.test", path);

        GateDecision decision = _gate.Decide(context);

        Assert.Equal(GateAction.Respond, decision.Action);
        Assert.NotEqual(200, decision.Response!.StatusCode);
    }

    [Fact]
    public void OnlyGetAndHeadAreAnswered()
    {
        DefaultHttpContext context = TestContexts.Create("secure.test", "/.well-known/acme-challenge/" + Token, method: "POST");

        Assert.NotEqual(200, _gate.Decide(context).Response!.StatusCode);
    }

    [Fact]
    public void HttpsRequestsAreNotAnswered()
    {
        DefaultHttpContext context = TestContexts.Create("secure.test", "/.well-known/acme-challenge/" + Token, scheme: "https");

        GateDecision decision = _gate.Decide(context);

        // The host's own pipeline (421 or its allow list), not a challenge answer.
        Assert.Equal(GateAction.Respond, decision.Action);
        Assert.NotEqual(200, decision.Response!.StatusCode);
    }

    [Fact]
    public void ARemovedTokenIsNoLongerAnswered()
    {
        Assert.True(_challenges.Remove(Token));
        DefaultHttpContext context = TestContexts.Create("secure.test", "/.well-known/acme-challenge/" + Token);

        Assert.NotEqual(200, _gate.Decide(context).Response!.StatusCode);
        Assert.False(_challenges.Remove(Token));
    }

    [Fact]
    public void ATokenTwoIssuancesPublishedIsAnsweredUntilBothRemovedIt()
    {
        _challenges.Publish(Token, KeyAuthorization);
        DefaultHttpContext context = TestContexts.Create("secure.test", "/.well-known/acme-challenge/" + Token);

        Assert.True(_challenges.Remove(Token));
        Assert.Equal(200, _gate.Decide(context).Response!.StatusCode);
        Assert.Equal(1, _challenges.Count);

        Assert.True(_challenges.Remove(Token));
        Assert.NotEqual(200, _gate.Decide(context).Response!.StatusCode);
        Assert.False(_challenges.Remove(Token));
    }

    [Fact]
    public void WithoutAStoreNothingIsAnswered()
    {
        GateMiddleware gate = new(_store, new TrustedProxyClientAddressSource());
        DefaultHttpContext context = TestContexts.Create("secure.test", "/.well-known/acme-challenge/" + Token);

        Assert.NotEqual(200, gate.Decide(context).Response!.StatusCode);
    }

    [Fact]
    public void AnAnswerAndAMissDoNotAllocate()
    {
        DefaultHttpContext hit = TestContexts.Create("secure.test", "/.well-known/acme-challenge/" + Token);
        DefaultHttpContext miss = TestContexts.Create("secure.test", "/.well-known/acme-challenge/not-published-token-value");
        AllocAssert.Zero((_gate, hit), static s => _ = s._gate.Decide(s.hit), "HTTP-01 answer");
        AllocAssert.Zero((_gate, miss), static s => _ = s._gate.Decide(s.miss), "HTTP-01 miss falling through to the redirect");
    }

    [Fact]
    public void TheStoreValidatesWhatItPublishes()
    {
        Assert.Throws<ArgumentException>(() => _challenges.Publish("", "x"));
        Assert.Throws<ArgumentException>(() => _challenges.Publish("a/b", "x"));
        Assert.Throws<ArgumentException>(() => _challenges.Publish("a.b", "x"));
        Assert.Throws<ArgumentException>(() => _challenges.Publish("abc", ""));
        Assert.Throws<ArgumentException>(() => _challenges.Publish("abc", "with space"));
        Assert.Throws<ArgumentException>(() => _challenges.Publish("abc", "line\nbreak"));
    }

    [Fact]
    public void TheStoreIsBoundedButAllowsReplacingAToken()
    {
        AcmeChallengeStore store = new();
        for (int i = 0; i < IAcmeChallengeStore.MaxEntries; i++)
        {
            store.Publish($"t{i}", "ka");
        }

        Assert.Throws<InvalidOperationException>(() => store.Publish("overflow", "ka"));
        store.Publish("t0", "replaced");
        Assert.Equal(IAcmeChallengeStore.MaxEntries, store.Count);
        Assert.True(store.TryGet("t0", out PrecomputedResponse? response));
        Assert.Equal("replaced", Encoding.ASCII.GetString(response.Body));
    }
}
