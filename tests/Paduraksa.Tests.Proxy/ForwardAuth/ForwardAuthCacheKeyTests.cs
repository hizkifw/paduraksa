using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.ForwardAuth;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Net;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Proxy.ForwardAuth.ForwardAuthFixtures;

namespace Paduraksa.Tests.Proxy.ForwardAuth;

/// <summary>What the decision cache may remember and for whom: an allow that names no one only for the request it was given for, and no allow for one request
/// that stands in for another that differs in anything the endpoint is sent to decide on.</summary>
public sealed class ForwardAuthCacheKeyTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // authentik's unauthenticated paths: /public is open to anyone, everything else needs the session.
    private static Func<HttpContext, Task> PublicPathOpen() => context =>
    {
        var uri = context.Request.Headers["X-Forwarded-Uri"].ToString();
        if (uri.StartsWith("/public", StringComparison.Ordinal) || context.Request.Headers.Cookie.ToString().Contains("session=ok", StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };

    // The same, but the session also gets an identity: what authentik and oauth2-proxy answer a signed-in user.
    private static Func<HttpContext, Task> PublicPathOpenAndSessionIsAlice() => context =>
    {
        if (context.Request.Headers.Cookie.ToString().Contains("session=ok", StringComparison.Ordinal))
        {
            context.Response.Headers["Remote-User"] = "alice";
            return Task.CompletedTask;
        }

        var uri = context.Request.Headers["X-Forwarded-Uri"].ToString();
        if (!uri.StartsWith("/public", StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        }

        return Task.CompletedTask;
    };

    private static Func<HttpContext, Task> AllowsApiKey(string key = "secret") => context =>
    {
        if (context.Request.Headers["X-Api-Key"] != key)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        }

        return Task.CompletedTask;
    };

    [Theory]
    [InlineData(ForwardAuthCacheScope.Host)]
    [InlineData(ForwardAuthCacheScope.Request)]
    public async Task AnAllowThatNamesNoOneIsRememberedOnlyForThePathItWasGivenFor(ForwardAuthCacheScope scope)
    {
        await using var auth = await FakeAuthServer.StartAsync(Cancel);
        auth.Handler = PublicPathOpen();
        var runtime = new ForwardAuthRuntime();
        var gate = GateFor(runtime, Model(Spec(auth.Address, b => b.Scope = scope)), out _);

        Assert.Equal("401", await RunAsync(gate, Context(path: "/admin")));
        Assert.Equal("pass", await RunAsync(gate, Context(path: "/public")));

        // The same address, host and (no) credentials, another path: the endpoint is asked, and says no.
        Assert.Equal("401", await RunAsync(gate, Context(path: "/admin")));
        Assert.Equal(3, auth.CallCount);

        // The open path itself is remembered, at request grain: no call, and the other path is still asked.
        Assert.Equal("pass", await RunAsync(gate, Context(path: "/public")));
        Assert.Equal(3, auth.CallCount);
        Assert.Equal(1, runtime.CountersFor(Provider).CacheHits);
        Assert.Equal("401", await RunAsync(gate, Context(path: "/admin")));
        Assert.Equal(4, auth.CallCount);
    }

    [Theory]
    [InlineData("session=anything")]
    [InlineData("x=1")]
    [InlineData("_ga=GA1.2.123456789.1700000000")]
    public async Task AnyCookieAnAnonymousVisitorSendsDoesntTurnItsAllowIntoOneForTheWholeHost(string cookie)
    {
        await using var auth = await FakeAuthServer.StartAsync(Cancel);
        auth.Handler = PublicPathOpen();
        var gate = GateFor(new ForwardAuthRuntime(), Model(Spec(auth.Address, b => b.Scope = ForwardAuthCacheScope.Host)), out _);

        // authentik's unauthenticated path lets a visitor with some cookie through, with no identity; it says nothing about /admin.
        Assert.Equal("pass", await RunAsync(gate, Context(path: "/public", cookie: cookie)));
        Assert.Equal("401", await RunAsync(gate, Context(path: "/admin", cookie: cookie)));
        Assert.Equal(2, auth.CallCount);
    }

    [Fact]
    public async Task AnyHeaderTheProviderReadsSentByAnAnonymousVisitorDoesntMakeItsAllowHostWideEither()
    {
        await using var auth = await FakeAuthServer.StartAsync(Cancel);
        auth.Handler = PublicPathOpen();
        var gate = GateFor(new ForwardAuthRuntime(), Model(Spec(auth.Address, b =>
        {
            b.Scope = ForwardAuthCacheScope.Host;
            b.RequestHeaders = ["X-Api-Key"];
        })), out _);

        var open = Context(path: "/public");
        open.Request.Headers["X-Api-Key"] = "junk";
        Assert.Equal("pass", await RunAsync(gate, open));
        var admin = Context(path: "/admin");
        admin.Request.Headers["X-Api-Key"] = "junk";
        Assert.Equal("401", await RunAsync(gate, admin));
        Assert.Equal(2, auth.CallCount);

        // And the open path is still remembered for that very request.
        var again = Context(path: "/public");
        again.Request.Headers["X-Api-Key"] = "junk";
        Assert.Equal("pass", await RunAsync(gate, again));
        Assert.Equal(2, auth.CallCount);
    }

    [Fact]
    public async Task AnAllowThatNamesSomeoneIsStillRememberedAcrossPathsInHostScopeAndOneThatDoesntIsNot()
    {
        await using var auth = await FakeAuthServer.StartAsync(Cancel);
        auth.Handler = PublicPathOpenAndSessionIsAlice();
        var runtime = new ForwardAuthRuntime();
        var gate = GateFor(runtime, Model(Spec(auth.Address, b => b.Scope = ForwardAuthCacheScope.Host)), out _);

        Assert.Equal("pass", await RunAsync(gate, Context(path: "/public", cookie: "session=ok")));
        Assert.Equal("pass", await RunAsync(gate, Context(path: "/admin", cookie: "session=ok")));
        Assert.Equal("pass", await RunAsync(gate, Context(path: "/anything/else", cookie: "session=ok")));
        Assert.Equal(1, auth.CallCount);
        Assert.Equal(2, runtime.CountersFor(Provider).CacheHits);
    }

    [Fact]
    public async Task EmptyCredentialHeadersAreNoCredentialsEither()
    {
        await using var auth = await FakeAuthServer.StartAsync(Cancel);
        auth.Handler = PublicPathOpen();
        var gate = GateFor(new ForwardAuthRuntime(), Model(Spec(auth.Address, b => b.Scope = ForwardAuthCacheScope.Host)), out _);

        var anonymous = Context(path: "/public", cookie: " ");
        Assert.Equal("pass", await RunAsync(gate, anonymous));
        Assert.Equal("401", await RunAsync(gate, Context(path: "/admin", cookie: " ")));
    }

    [Fact]
    public async Task APathBlockedForOneCookieIsNotOpenedByAnotherVisitorsAllowInHostScope()
    {
        await using var auth = await FakeAuthServer.StartAsync(Cancel);
        auth.Handler = PublicPathOpen();
        var gate = GateFor(new ForwardAuthRuntime(), Model(Spec(auth.Address, b => b.Scope = ForwardAuthCacheScope.Host)), out _);

        Assert.Equal("pass", await RunAsync(gate, Context(path: "/admin", cookie: "session=ok")));

        // Another cookie from the same address and host is another key.
        Assert.Equal("401", await RunAsync(gate, Context(path: "/admin", cookie: "session=guess")));
    }

    [Fact]
    public async Task AnExtraRequestHeaderTheEndpointDecidesOnIsPartOfTheCacheKey()
    {
        await using var auth = await FakeAuthServer.StartAsync(Cancel);
        auth.Handler = AllowsApiKey();
        var gate = GateFor(new ForwardAuthRuntime(), Model(Spec(auth.Address, b => b.RequestHeaders = ["X-Api-Key"])), out _);

        var withKey = Context(path: "/data");
        withKey.Request.Headers["X-Api-Key"] = "secret";
        Assert.Equal("pass", await RunAsync(gate, withKey));

        // The same address and path without the key, and with another: the endpoint decides, neither is served from the cache.
        Assert.Equal("401", await RunAsync(gate, Context(path: "/data")));
        var wrongKey = Context(path: "/data");
        wrongKey.Request.Headers["X-Api-Key"] = "other";
        Assert.Equal("401", await RunAsync(gate, wrongKey));
        Assert.Equal(3, auth.CallCount);

        // The same key again is remembered.
        var again = Context(path: "/data");
        again.Request.Headers["X-Api-Key"] = "secret";
        Assert.Equal("pass", await RunAsync(gate, again));
        Assert.Equal(3, auth.CallCount);
    }

    [Fact]
    public async Task AnAllowForARequestWithOnlyTheProvidersOwnHeaderIsRememberedForThatValue()
    {
        await using var auth = await FakeAuthServer.StartAsync(Cancel);
        auth.Handler = AllowsApiKey();
        var gate = GateFor(new ForwardAuthRuntime(), Model(Spec(auth.Address, b => b.RequestHeaders = ["x-api-key"])), out _);

        for (var i = 0; i < 3; i++)
        {
            var context = Context(path: "/data");
            context.Request.Headers["X-Api-Key"] = "secret";
            Assert.Equal("pass", await RunAsync(gate, context));
        }

        Assert.Equal(1, auth.CallCount);
    }

    [Theory]
    [InlineData("User-Agent", ForwardAuthCacheScope.Request)]
    [InlineData("User-Agent", ForwardAuthCacheScope.Host)]
    [InlineData("Accept", ForwardAuthCacheScope.Request)]
    [InlineData("Accept", ForwardAuthCacheScope.Host)]
    [InlineData("Accept-Language", ForwardAuthCacheScope.Host)]
    [InlineData("X-Requested-With", ForwardAuthCacheScope.Host)]
    public async Task AnAllowIsNotServedToARequestThatDiffersInAHeaderTheProviderIsSent(string header, ForwardAuthCacheScope scope)
    {
        await using var auth = await FakeAuthServer.StartAsync(Cancel);

        // A provider that refuses scripted clients: what it decides on is what its call carries, so its allow can't stand in for another call.
        auth.Handler = context =>
        {
            if (context.Request.Headers.UserAgent.ToString().Contains("bad-bot", StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }

            return AllowsSessionOk()(context);
        };
        var gate = GateFor(new ForwardAuthRuntime(), Model(Spec(auth.Address, b => b.Scope = scope)), out _);

        DefaultHttpContext Request(string value)
        {
            var context = Context(cookie: "session=ok");
            context.Request.Headers[header] = value;
            return context;
        }

        Assert.Equal("pass", await RunAsync(gate, Request("friendly")));
        Assert.Equal("pass", await RunAsync(gate, Request("friendly")));
        Assert.Equal(1, auth.CallCount);

        Assert.Equal(header == "User-Agent" ? "401" : "pass", await RunAsync(gate, Request("bad-bot")));
        Assert.Equal(2, auth.CallCount);
    }

    [Fact]
    public async Task AnAllowOverHttpsIsNotServedToTheSameRequestOverPlainHttp()
    {
        await using var auth = await FakeAuthServer.StartAsync(Cancel);

        // The provider is told the scheme (X-Forwarded-Proto) and may refuse plain HTTP.
        auth.Handler = context =>
        {
            if (context.Request.Headers["X-Forwarded-Proto"] == "http")
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }

            return AllowsSessionOk()(context);
        };
        var gate = GateFor(new ForwardAuthRuntime(), Model(Spec(auth.Address, b => b.Scope = ForwardAuthCacheScope.Host)), out _);
        static DefaultHttpContext Request(string scheme)
        {
            var context = Context(cookie: "session=ok");
            context.Request.Scheme = scheme;
            return context;
        }

        Assert.Equal("pass", await RunAsync(gate, Request("https")));
        Assert.Equal("401", await RunAsync(gate, Request("http")));
        Assert.Equal(2, auth.CallCount);
    }

    [Fact]
    public void AnAbsentHeaderAndAnEmptyOneHaveDifferentKeys()
    {
        var spec = Spec("http://127.0.0.1:1/auth", b => b.RequestHeaders = ["X-Api-Key"]);
        var provider = new CompiledAuthProvider(spec, new ForwardAuthRuntime().Bind(spec));
        var secret = new byte[32];
        var state = new GateRequestState();

        var absent = Context(cookie: "a=b");
        var empty = Context(cookie: "a=b");
#pragma warning disable ASP0019 // The indexer and Append treat an empty value as no header; Add keeps it, as a parsed "X-Api-Key:" line does.
        empty.Request.Headers.Add("X-Api-Key", new StringValues(string.Empty));
#pragma warning restore ASP0019
        var oneLine = Context(cookie: "a=b");
        oneLine.Request.Headers["X-Api-Key"] = "ab";
        var twoLines = Context(cookie: "a=b");
        twoLines.Request.Headers["X-Api-Key"] = new StringValues(["a", "b"]);

        var keys = new[] { absent, empty, oneLine, twoLines }
            .Select(context => ForwardAuthKey.Compute(secret, ForwardAuthCacheScope.Request, context, state, provider.KeyHeaders))
            .ToHashSet();
        Assert.Equal(4, keys.Count);
    }

    [Theory]
    [InlineData(ForwardAuthCacheScope.Request)]
    [InlineData(ForwardAuthCacheScope.Host)]
    public async Task ARememberedAllowWithTheProvidersOwnHeadersAndTheDeferringVerdictAllocateNothing(ForwardAuthCacheScope scope)
    {
        await using var auth = await FakeAuthServer.StartAsync(Cancel);
        auth.Handler = AllowsApiKey();
        var gate = GateFor(new ForwardAuthRuntime(), Model(Spec(auth.Address, b =>
        {
            b.Scope = scope;
            b.RequestHeaders = ["X-Api-Key", "X-Tenant"];
        })), out var snapshot);

        static DefaultHttpContext Keyed(string key)
        {
            var context = Context(path: "/data", rawTarget: "/data?q=1");
            context.Request.Headers["X-Api-Key"] = key;
            context.Request.Headers["X-Tenant"] = "t-" + new string('x', 200);
            return context;
        }

        Assert.Equal("pass", await RunAsync(gate, Keyed("secret")));
        IClientAddressSource addresses = new TrustedProxyClientAddressSource();
        var remembered = Keyed("secret");
        var wrong = Keyed("nope");
        var anonymous = Context(path: "/data");

        Assert.Equal(GateAction.Forward, GateMiddleware.Decide(remembered, snapshot, addresses, null, default(NoGateTracer)).Action);
        AllocAssert.Zero((snapshot, addresses, remembered), static s => _ = GateMiddleware.Decide(s.remembered, s.snapshot, s.addresses, null, default(NoGateTracer)), "a remembered allow keyed on extra headers");

        Assert.Equal(GateAction.AwaitPolicy, GateMiddleware.Decide(wrong, snapshot, addresses, null, default(NoGateTracer)).Action);
        AllocAssert.Zero((snapshot, addresses, wrong), static s => _ = GateMiddleware.Decide(s.wrong, s.snapshot, s.addresses, null, default(NoGateTracer)), "a request with another key");

        Assert.Equal(GateAction.AwaitPolicy, GateMiddleware.Decide(anonymous, snapshot, addresses, null, default(NoGateTracer)).Action);
        AllocAssert.Zero((snapshot, addresses, anonymous), static s => _ = GateMiddleware.Decide(s.anonymous, s.snapshot, s.addresses, null, default(NoGateTracer)), "a request with no credentials");
    }

    [Fact]
    public async Task UnderHostScopeAHitAtHostGrainAndOneAtRequestGrainAllocateNothingAndTheMissesProbeBoth()
    {
        await using var auth = await FakeAuthServer.StartAsync(Cancel);
        auth.Handler = PublicPathOpenAndSessionIsAlice();
        var gate = GateFor(new ForwardAuthRuntime(), Model(Spec(auth.Address, b => b.Scope = ForwardAuthCacheScope.Host)), out var snapshot);

        // One allow that names alice (stored at host grain) and one that names no one (stored at request grain).
        Assert.Equal("pass", await RunAsync(gate, Context(path: "/docs", cookie: "session=ok")));
        Assert.Equal("pass", await RunAsync(gate, Context(path: "/public", cookie: "x=1")));
        IClientAddressSource addresses = new TrustedProxyClientAddressSource();
        var hostGrain = Context(path: "/elsewhere", cookie: "session=ok");
        var requestGrain = Context(path: "/public", cookie: "x=1");
        var miss = Context(path: "/admin", cookie: "x=1");

        Assert.Equal(GateAction.Forward, GateMiddleware.Decide(hostGrain, snapshot, addresses, null, default(NoGateTracer)).Action);
        AllocAssert.Zero((snapshot, addresses, hostGrain), static s => _ = GateMiddleware.Decide(s.hostGrain, s.snapshot, s.addresses, null, default(NoGateTracer)), "a hit at host grain");

        Assert.Equal(GateAction.Forward, GateMiddleware.Decide(requestGrain, snapshot, addresses, null, default(NoGateTracer)).Action);
        AllocAssert.Zero((snapshot, addresses, requestGrain), static s => _ = GateMiddleware.Decide(s.requestGrain, s.snapshot, s.addresses, null, default(NoGateTracer)), "a hit at request grain under host scope");

        Assert.Equal(GateAction.AwaitPolicy, GateMiddleware.Decide(miss, snapshot, addresses, null, default(NoGateTracer)).Action);
        AllocAssert.Zero((snapshot, addresses, miss), static s => _ = GateMiddleware.Decide(s.miss, s.snapshot, s.addresses, null, default(NoGateTracer)), "a miss that probes both grains");
    }
}
