using System.Net.Http.Headers;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.ForwardAuth;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Yarp;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Proxy.ForwardAuth.ForwardAuthFixtures;

namespace Paduraksa.Tests.Proxy.ForwardAuth;

/// <summary>The synchronous half of the policy stays at 0 B (a remembered allow, and the verdict that defers to a call), and what a
/// provider's headers do to the request the upstream gets.</summary>
public sealed class ForwardAuthAllocationAndHeadersTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(ForwardAuthCacheScope.Request)]
    [InlineData(ForwardAuthCacheScope.Host)]
    public async Task ARememberedAllowAndTheVerdictThatDefersToACallAllocateNothing(ForwardAuthCacheScope scope)
    {
        await using var auth = await FakeAuthServer.StartAsync(Cancel);
        auth.Handler = AllowsSessionOk();
        var gate = GateFor(new ForwardAuthRuntime(), Model(Spec(auth.Address, b => b.Scope = scope)), out var snapshot);
        Assert.Equal("pass", await RunAsync(gate, Context(cookie: "session=ok; a=" + new string('x', 300), path: "/page", rawTarget: "/page?q=1")));
        IClientAddressSource addresses = new TrustedProxyClientAddressSource();
        var remembered = Context(cookie: "session=ok; a=" + new string('x', 300), path: "/page", rawTarget: "/page?q=1");
        var unknown = Context(cookie: "session=nope");
        var longTarget = Context(cookie: "session=ok; a=" + new string('z', 3000));

        Assert.Equal(GateAction.Forward, GateMiddleware.Decide(remembered, snapshot, addresses, null, default(NoGateTracer)).Action);
        AllocAssert.Zero((snapshot, addresses, remembered), static s => _ = GateMiddleware.Decide(s.remembered, s.snapshot, s.addresses, null, default(NoGateTracer)), "a remembered allow");

        Assert.Equal(GateAction.AwaitPolicy, GateMiddleware.Decide(unknown, snapshot, addresses, null, default(NoGateTracer)).Action);
        AllocAssert.Zero((snapshot, addresses, unknown), static s => _ = GateMiddleware.Decide(s.unknown, s.snapshot, s.addresses, null, default(NoGateTracer)), "the verdict that defers");

        // A request whose hashed fields (here a 3 KB cookie) don't fit the stack buffer borrows an array from the pool: still nothing allocated.
        Assert.Equal(GateAction.AwaitPolicy, GateMiddleware.Decide(longTarget, snapshot, addresses, null, default(NoGateTracer)).Action);
        AllocAssert.Zero((snapshot, addresses, longTarget), static s => _ = GateMiddleware.Decide(s.longTarget, s.snapshot, s.addresses, null, default(NoGateTracer)), "a request too large for the stack buffer");
    }

    [Fact]
    public void TheClientsOwnCopiesOfTheProvidersHeadersAreRemovedAndOnlyTheProvidersRemain()
    {
        var provider = Provider(["Remote-User", "Remote-Groups", "X-authentik-*"]);
        using HttpRequestMessage upstream = new();
        upstream.Headers.TryAddWithoutValidation("Remote-User", "root");
        upstream.Headers.TryAddWithoutValidation("remote-groups", "admins");
        upstream.Headers.TryAddWithoutValidation("X-Authentik-Uid", "forged");
        upstream.Headers.TryAddWithoutValidation("x-authentik-meta-app", "forged");
        upstream.Headers.TryAddWithoutValidation("X-Other", "kept");
        upstream.Headers.TryAddWithoutValidation("Cookie", "kept");

        ForwardedHeadersTransform.ApplyForwardAuth(
            upstream.Headers, [provider], [new ForwardAuthHeader("Remote-User", "alice"), new ForwardAuthHeader("X-authentik-uid", "u-1")]);

        Assert.Equal(["alice"], upstream.Headers.GetValues("Remote-User"));
        Assert.Equal(["u-1"], upstream.Headers.GetValues("X-authentik-uid"));
        Assert.False(upstream.Headers.Contains("Remote-Groups"));
        Assert.False(upstream.Headers.Contains("X-authentik-meta-app"));
        Assert.Equal(["kept"], upstream.Headers.GetValues("X-Other"));
        Assert.Equal(["kept"], upstream.Headers.GetValues("Cookie"));
    }

    [Fact]
    public void AnAllowWithNoHeadersStillRemovesWhatTheClientForged()
    {
        var provider = Provider(["Remote-User"]);
        using HttpRequestMessage upstream = new();
        upstream.Headers.TryAddWithoutValidation("Remote-User", "root");

        ForwardedHeadersTransform.ApplyForwardAuth(upstream.Headers, [provider], []);

        Assert.False(upstream.Headers.Contains("Remote-User"));
    }

    [Fact]
    public void AHeaderIsOwnedByExactNameOrPrefixIgnoringCase()
    {
        var provider = Provider(["Remote-User", "X-authentik-*"]);

        Assert.True(provider.Owns("remote-user"));
        Assert.True(provider.Owns("X-AUTHENTIK-groups"));
        Assert.False(provider.Owns("Remote-Users"));
        Assert.False(provider.Owns("X-other"));
    }

    [Theory]
    [InlineData("Remote_User")]
    [InlineData("REMOTE_USER")]
    [InlineData("remote_groups")]
    [InlineData("X_authentik_username")]
    [InlineData("X-authentik_Meta-App")]
    [InlineData("x_AUTHENTIK-uid")]
    public void UnderscoreAndHyphenAreTheSameCharacterWhenDecidingWhatTheProviderOwns(string name)
    {
        var provider = Provider(["Remote-User", "Remote-Groups", "X-authentik-*"]);

        Assert.True(provider.Owns(name));
    }

    [Theory]
    [InlineData("Remote_Users")]
    [InlineData("Remote.User")]
    [InlineData("X_authentic_uid")]
    [InlineData("X_authentik")]
    public void ANameThatOnlyLooksLikeAnOwnedOneIsNot(string name)
    {
        var provider = Provider(["Remote-User", "Remote-Groups", "X-authentik-*"]);

        Assert.False(provider.Owns(name));
    }

    [Fact]
    public void AnOwnedPrefixWrittenWithUnderscoresMatchesHyphenatedHeaders()
    {
        var provider = Provider(["Remote_User", "X_auth_*"]);

        Assert.True(provider.Owns("Remote-User"));
        Assert.True(provider.Owns("X-Auth-Name"));
        Assert.True(provider.Owns("x_auth_name"));
    }

    [Fact]
    public void TheClientsUnderscoreCopiesOfTheProvidersHeadersAreRemovedToo()
    {
        var provider = Provider(["Remote-User", "Remote-Groups", "X-authentik-*"]);
        using HttpRequestMessage upstream = new();
        upstream.Headers.TryAddWithoutValidation("Remote_Groups", "admins");
        upstream.Headers.TryAddWithoutValidation("X_authentik_username", "root");
        upstream.Headers.TryAddWithoutValidation("X-Authentik_Uid", "forged");
        upstream.Headers.TryAddWithoutValidation("X_Other", "kept");

        ForwardedHeadersTransform.ApplyForwardAuth(upstream.Headers, [provider], [new ForwardAuthHeader("Remote-User", "alice")]);

        Assert.Equal(["alice"], upstream.Headers.GetValues("Remote-User"));
        Assert.False(upstream.Headers.Contains("Remote_Groups"));
        Assert.False(upstream.Headers.Contains("X_authentik_username"));
        Assert.False(upstream.Headers.Contains("X-Authentik_Uid"));
        Assert.Equal(["kept"], upstream.Headers.GetValues("X_Other"));
    }

    [Fact]
    public void EveryProviderOfTheHostHasItsHeadersRemoved()
    {
        var first = Provider(["Remote-User"]);
        var second = Provider(["X-Auth-Request-*"]);
        using HttpRequestMessage upstream = new();
        upstream.Headers.TryAddWithoutValidation("Remote-User", "root");
        upstream.Headers.TryAddWithoutValidation("X-Auth-Request-Email", "root@example.com");

        ForwardedHeadersTransform.ApplyForwardAuth(upstream.Headers, [first, second], null);

        Assert.False(upstream.Headers.Contains("Remote-User"));
        Assert.False(upstream.Headers.Contains("X-Auth-Request-Email"));
    }

    [Fact]
    public void EveryRouteOfAHostWithAProviderStripsItsIdentityHeadersAndSoDoesAnotherRoutesProvider()
    {
        var spec = Spec("http://127.0.0.1:1/auth");
        var other = Spec("http://127.0.0.1:2/auth", b => b.ResponseHeaders = ["X-Other-*"]) with { Id = TestIds.AuthProvider("other") };
        var model = new ProxyConfigModel
        {
            Upstreams = [TestSnapshots.Upstream("web", "http://127.0.0.1:1")],
            AuthProviders = [spec, other],
            Hosts =
            [
                // The host's provider, a route with sign-in off (the provider's own pages) and one that names another provider.
                TestSnapshots.ProxyHost("app", "app.test", TestSnapshots.Route("guarded", "web") with { AuthProvider = spec.Id }, TestSnapshots.Route("open", "web", "/open") with { AuthProvider = null })
                    with { AuthProvider = new HostAuthProviderSpec { Enabled = true, AuthProviderId = spec.Id } },
                // No host-level provider, one route with one and another with sign-in off.
                TestSnapshots.ProxyHost("mixed", "mixed.test", TestSnapshots.Route("mixed-guarded", "web", "/a") with { AuthProvider = other.Id }, TestSnapshots.Route("mixed-open", "web", "/b")),
                TestSnapshots.ProxyHost("plain", "plain.test", TestSnapshots.Route("plain-root", "web")),
            ],
        };

        var snapshot = TestSnapshots.Compile(model, new SnapshotCompilerOptions { ForwardAuth = new ForwardAuthRuntime() });

        Assert.Equal([spec.Id], snapshot.HostsByIndex[0].IdentityProviders.Select(static p => p.Id));
        Assert.Equal([other.Id], snapshot.HostsByIndex[1].IdentityProviders.Select(static p => p.Id));
        Assert.Empty(snapshot.HostsByIndex[2].IdentityProviders);
        Assert.Null(snapshot.HostsByIndex[0].Routes.Single(static r => r.Path == "/open").AuthProvider);
    }

    private static CompiledAuthProvider Provider(string[] responseHeaders)
    {
        var spec = Spec("http://127.0.0.1:1/auth", b => b.ResponseHeaders = responseHeaders);
        return new CompiledAuthProvider(spec, new ForwardAuthRuntime().Bind(spec));
    }
}
