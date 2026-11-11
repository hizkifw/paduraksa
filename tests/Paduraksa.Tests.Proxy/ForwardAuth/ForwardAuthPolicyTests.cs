using Microsoft.AspNetCore.Http;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.ForwardAuth;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.RateLimiting;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Proxy.ForwardAuth.ForwardAuthFixtures;

namespace Paduraksa.Tests.Proxy.ForwardAuth;

/// <summary>A route's forward-auth policy through the real gate against a fake endpoint: what is asked, what each answer does, what is
/// remembered and for whom, and that every failure refuses the request.</summary>
public sealed class ForwardAuthPolicyTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnAllowedRequestGoesOnAndTheEndpointIsAskedWithTheOriginalRequest()
    {
        await using var auth = await FakeAuthServer.StartAsync(Cancel);
        auth.Handler = AllowsSessionOk();
        var runtime = new ForwardAuthRuntime();
        var gate = GateFor(runtime, Model(Spec(auth.Address)), out _);
        var context = Context(client: "198.51.100.7", cookie: "session=ok; theme=dark", path: "/a b", method: "POST", rawTarget: "/a%20b?x=1&y=%2F");
        context.Request.Headers.Authorization = "Bearer token";
        context.Request.Headers["X-Custom"] = "not forwarded";
        context.Request.Headers["X-Forwarded-For"] = "10.9.9.9";
        context.Request.Headers["X-Forwarded-Uri"] = "/spoofed";
        context.Request.Headers.UserAgent = "TestAgent/1";

        Assert.Equal("pass", await RunAsync(gate, context));

        var call = Assert.Single(auth.Calls);
        Assert.Equal("GET", call.Method);
        Assert.Equal("/", call.Target);
        Assert.Equal("POST", call.Header("X-Forwarded-Method"));
        Assert.Equal("http", call.Header("X-Forwarded-Proto"));
        Assert.Equal("app.test", call.Header("X-Forwarded-Host"));
        Assert.Equal("/a%20b?x=1&y=%2F", call.Header("X-Forwarded-Uri"));
        Assert.Equal("198.51.100.7", call.Header("X-Forwarded-For"));
        Assert.Equal("session=ok; theme=dark", call.Header("Cookie"));
        Assert.Equal("Bearer token", call.Header("Authorization"));
        Assert.Equal("TestAgent/1", call.Header("User-Agent"));
        Assert.Null(call.Header("X-Custom"));
        var state = context.Features.Get<IGateFeature>()!.State;
        Assert.Equal([new ForwardAuthHeader("Remote-User", "alice")], state.ForwardAuthHeaders!);
    }

    [Fact]
    public async Task OnlyTheStandardHeadersAndTheProvidersOwnAreSentNeverTheRestOfTheClientsHeaders()
    {
        await using var auth = await FakeAuthServer.StartAsync(Cancel);
        auth.Handler = AllowsSessionOk();
        var gate = GateFor(new ForwardAuthRuntime(), Model(Spec(auth.Address, b => b.RequestHeaders = ["X-Tenant"])), out _);
        var context = Context(cookie: "session=ok");
        context.Request.Headers["X-Tenant"] = "acme";
        context.Request.Headers["Proxy-Authorization"] = "Basic abc";
        context.Request.Headers["X-Requested-With"] = "XMLHttpRequest";
        context.Request.Headers["Connection"] = "keep-alive, X-Hop";
        context.Request.Headers["X-Hop"] = "1";
        context.Request.Headers["Content-Length"] = "10";
        context.Request.Headers["Referer"] = "https://elsewhere.test/";

        await RunAsync(gate, context);

        var call = Assert.Single(auth.Calls);
        Assert.Equal("acme", call.Header("X-Tenant"));
        Assert.Equal("Basic abc", call.Header("Proxy-Authorization"));
        Assert.Equal("XMLHttpRequest", call.Header("X-Requested-With"));
        Assert.Null(call.Header("X-Hop"));
        Assert.Null(call.Header("Referer"));
        Assert.Null(call.Header("Content-Length"));
        Assert.Null(call.Header("Connection") is { } c && c.Contains("X-Hop", StringComparison.Ordinal) ? c : null);
    }

    [Fact]
    public async Task ASecondRequestWithTheSameCredentialsIsAnsweredFromTheCacheWithoutACall()
    {
        await using var auth = await FakeAuthServer.StartAsync(Cancel);
        auth.Handler = AllowsSessionOk();
        var runtime = new ForwardAuthRuntime();
        var gate = GateFor(runtime, Model(Spec(auth.Address)), out var snapshot);
        var counters = runtime.CountersFor(Provider);

        Assert.Equal("pass", await RunAsync(gate, Context(cookie: "session=ok")));
        var second = Context(cookie: "session=ok");
        Assert.Equal("pass", await RunAsync(gate, second));

        Assert.Equal(1, auth.CallCount);
        Assert.Equal((2, 1, 1), (counters.Allowed, counters.CacheHits, counters.Calls));
        // The cached allow carries the provider's headers to the upstream, as a call's would.
        Assert.Equal([new ForwardAuthHeader("Remote-User", "alice")], second.Features.Get<IGateFeature>()!.State.ForwardAuthHeaders!);
        Assert.Equal(1, snapshot.HostsByIndex[0].Routes[0].AuthProvider!.State.CachedAllows);
    }

    [Fact]
    public async Task ACacheKeyCoversTheCredentialsTheClientAddressAndTheHost()
    {
        await using var auth = await FakeAuthServer.StartAsync(Cancel);
        auth.Handler = AllowsSessionOk();
        var other = TestSnapshots.ProxyHost("other", "other.test", TestSnapshots.Route("other-root", "web") with { AuthProvider = Provider });
        var gate = GateFor(new ForwardAuthRuntime(), Model(Spec(auth.Address), other), out _);

        await RunAsync(gate, Context(cookie: "session=ok"));
        Assert.Equal(1, auth.CallCount);

        // Another cookie, another address, another host and a different Authorization header each need their own answer.
        await RunAsync(gate, Context(cookie: "session=ok; extra=1"));
        Assert.Equal(2, auth.CallCount);
        await RunAsync(gate, Context(client: "192.0.2.77", cookie: "session=ok"));
        Assert.Equal(3, auth.CallCount);
        await RunAsync(gate, Context(host: "other.test", cookie: "session=ok"));
        Assert.Equal(4, auth.CallCount);
        var bearer = Context(cookie: "session=ok");
        bearer.Request.Headers.Authorization = "Bearer x";
        await RunAsync(gate, bearer);
        Assert.Equal(5, auth.CallCount);

        // And the first one is still remembered.
        await RunAsync(gate, Context(cookie: "session=ok"));
        Assert.Equal(5, auth.CallCount);
    }

    [Fact]
    public async Task ARequestScopedAnswerDoesNotCoverAnotherPathOrMethodButAHostScopedOneDoes()
    {
        await using var auth = await FakeAuthServer.StartAsync(Cancel);
        auth.Handler = AllowsSessionOk();

        var perRequest = GateFor(new ForwardAuthRuntime(), Model(Spec(auth.Address, b => b.Scope = ForwardAuthCacheScope.Request)), out _);
        await RunAsync(perRequest, Context(cookie: "session=ok", path: "/public"));
        await RunAsync(perRequest, Context(cookie: "session=ok", path: "/admin"));
        await RunAsync(perRequest, Context(cookie: "session=ok", path: "/admin", method: "DELETE"));
        await RunAsync(perRequest, Context(cookie: "session=ok", path: "/admin", rawTarget: "/admin?x=1"));
        Assert.Equal(4, auth.CallCount);
        await RunAsync(perRequest, Context(cookie: "session=ok", path: "/admin"));
        Assert.Equal(4, auth.CallCount);

        var perHost = GateFor(new ForwardAuthRuntime(), Model(Spec(auth.Address, b => b.Scope = ForwardAuthCacheScope.Host)), out _);
        await RunAsync(perHost, Context(cookie: "session=ok", path: "/public"));
        await RunAsync(perHost, Context(cookie: "session=ok", path: "/admin", method: "DELETE"));
        await RunAsync(perHost, Context(cookie: "session=ok", path: "/x", rawTarget: "/x?y=2"));
        Assert.Equal(5, auth.CallCount);
    }

    [Fact]
    public async Task AnAllowIsRememberedOnlyForItsTimeToLive()
    {
        await using var auth = await FakeAuthServer.StartAsync(Cancel);
        auth.Handler = AllowsSessionOk();
        ManualTime time = new();
        var gate = GateFor(new ForwardAuthRuntime(time, null), Model(Spec(auth.Address, b => b.CacheTtl = TimeSpan.FromSeconds(10))), out _);

        await RunAsync(gate, Context(cookie: "session=ok"));
        time.Advance(TimeSpan.FromSeconds(9));
        await RunAsync(gate, Context(cookie: "session=ok"));
        Assert.Equal(1, auth.CallCount);

        time.Advance(TimeSpan.FromSeconds(2));
        await RunAsync(gate, Context(cookie: "session=ok"));
        Assert.Equal(2, auth.CallCount);
    }

    [Fact]
    public async Task ZeroSecondsRemembersNothing()
    {
        await using var auth = await FakeAuthServer.StartAsync(Cancel);
        auth.Handler = AllowsSessionOk();
        var gate = GateFor(new ForwardAuthRuntime(), Model(Spec(auth.Address, b => b.CacheTtl = TimeSpan.Zero)), out _);

        await RunAsync(gate, Context(cookie: "session=ok"));
        await RunAsync(gate, Context(cookie: "session=ok"));

        Assert.Equal(2, auth.CallCount);
    }

    [Fact]
    public async Task ARefusalIsNeverRememberedSoASignedInUserIsNotKeptAtTheLoginPage()
    {
        await using var auth = await FakeAuthServer.StartAsync(Cancel);
        auth.Handler = AllowsSessionOk();
        var gate = GateFor(new ForwardAuthRuntime(), Model(Spec(auth.Address)), out _);

        Assert.Equal("401", await RunAsync(gate, Context()));
        Assert.Equal("401", await RunAsync(gate, Context()));
        Assert.Equal(2, auth.CallCount);

        // The same address, host and path with the session the user just got.
        Assert.Equal("pass", await RunAsync(gate, Context(cookie: "session=ok")));
    }

    [Fact]
    public async Task AFourOhOneIsRelayedWithItsBodyItsChallengeAndItsCookie()
    {
        await using var auth = await FakeAuthServer.StartAsync(Cancel);
        auth.Handler = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.WWWAuthenticate = "Basic realm=\"sso\"";
            context.Response.Headers.Append("Set-Cookie", "state=abc; Path=/; HttpOnly");
            context.Response.Headers.Append("Set-Cookie", "other=1; Path=/");
            context.Response.Headers["X-Internal"] = "secret";
            return context.Response.WriteAsync("<h1>Sign in</h1>");
        };
        var runtime = new ForwardAuthRuntime();
        var gate = GateFor(runtime, Model(Spec(auth.Address)), out _);
        var context = Context();

        Assert.Equal("401", await RunAsync(gate, context));

        Assert.Equal("<h1>Sign in</h1>", await BodyOfAsync(context));
        Assert.Equal("text/html; charset=utf-8", context.Response.Headers.ContentType.ToString());
        Assert.Equal("Basic realm=\"sso\"", context.Response.Headers.WWWAuthenticate.ToString());
        Assert.Equal(["state=abc; Path=/; HttpOnly", "other=1; Path=/"], context.Response.Headers.SetCookie.ToArray());
        Assert.False(context.Response.Headers.ContainsKey("X-Internal"));
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        Assert.Equal((0, 1, 0), (runtime.CountersFor(Provider).Allowed, runtime.CountersFor(Provider).Denied, runtime.CountersFor(Provider).Redirected));
    }

    [Theory]
    [InlineData(302)]
    [InlineData(303)]
    [InlineData(307)]
    public async Task ARedirectIsRelayedToTheClientAsItIs(int status)
    {
        await using var auth = await FakeAuthServer.StartAsync(Cancel);
        auth.Handler = context =>
        {
            context.Response.StatusCode = status;
            context.Response.Headers.Location = "https://auth.example.com/?rd=https%3A%2F%2Fapp.test%2F";
            return Task.CompletedTask;
        };
        var runtime = new ForwardAuthRuntime();
        var gate = GateFor(runtime, Model(Spec(auth.Address)), out _);
        var context = Context();

        Assert.Equal(status.ToString(System.Globalization.CultureInfo.InvariantCulture), await RunAsync(gate, context));

        Assert.Equal("https://auth.example.com/?rd=https%3A%2F%2Fapp.test%2F", context.Response.Headers.Location.ToString());
        Assert.Equal((0, 0, 1), (runtime.CountersFor(Provider).Allowed, runtime.CountersFor(Provider).Denied, runtime.CountersFor(Provider).Redirected));
    }

    [Fact]
    public async Task ARedirectWithoutALocationRefusesWithAnError()
    {
        await using var auth = await FakeAuthServer.StartAsync(Cancel);
        auth.Handler = context =>
        {
            context.Response.StatusCode = StatusCodes.Status302Found;
            return Task.CompletedTask;
        };
        var gate = GateFor(new ForwardAuthRuntime(), Model(Spec(auth.Address)), out _);

        Assert.Equal("502", await RunAsync(gate, Context()));
    }

    [Theory]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(404)]
    [InlineData(400)]
    [InlineData(429)]
    public async Task AnyOtherStatusFromTheEndpointRefusesTheRequestWithABadGateway(int status)
    {
        await using var auth = await FakeAuthServer.StartAsync(Cancel);
        auth.Handler = context =>
        {
            context.Response.StatusCode = status;
            return Task.CompletedTask;
        };
        var runtime = new ForwardAuthRuntime();
        var gate = GateFor(runtime, Model(Spec(auth.Address)), out _);
        var context = Context(accept: "text/html");

        Assert.Equal("502", await RunAsync(gate, context));

        Assert.Equal(1, runtime.CountersFor(Provider).Errors);
        Assert.Equal("text/html; charset=utf-8", context.Response.Headers.ContentType.ToString());
        Assert.Contains("502 Bad Gateway", await BodyOfAsync(context), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEndpointThatCannotBeReachedRefusesTheRequest()
    {
        var gate = GateFor(new ForwardAuthRuntime(), Model(Spec("http://127.0.0.1:1/auth")), out _);

        Assert.Equal("502", await RunAsync(gate, Context(cookie: "session=ok")));
    }

    [Fact]
    public async Task AnEndpointThatAnswersTooSlowlyRefusesTheRequestWithAGatewayTimeout()
    {
        await using var auth = await FakeAuthServer.StartAsync(Cancel);
        auth.Handler = async context =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), context.RequestAborted);
        };
        var runtime = new ForwardAuthRuntime();
        var gate = GateFor(runtime, Model(Spec(auth.Address, b => b.Timeout = TimeSpan.FromMilliseconds(300))), out _);

        Assert.Equal("504", await RunAsync(gate, Context(cookie: "session=ok")));
        Assert.Equal(1, runtime.CountersFor(Provider).Timeouts);
    }

    [Fact]
    public async Task AClientThatGoesAwayWhileTheEndpointThinksIsDroppedAndCountsNoFailure()
    {
        await using var auth = await FakeAuthServer.StartAsync(Cancel);
        auth.Handler = context => Task.Delay(TimeSpan.FromSeconds(10), context.RequestAborted);
        var runtime = new ForwardAuthRuntime();
        var gate = GateFor(runtime, Model(Spec(auth.Address)), out _);
        using CancellationTokenSource aborted = new();
        var context = Context(cookie: "session=ok");
        context.RequestAborted = aborted.Token;

        var running = RunAsync(gate, context);
        await aborted.CancelAsync();

        Assert.Equal("drop", await running);
        Assert.Equal((0, 0), (runtime.CountersFor(Provider).Errors, runtime.CountersFor(Provider).Timeouts));
    }

    [Fact]
    public async Task AnEndpointBodyOverTheRelayLimitRefusesInsteadOfRelayingAHalfPage()
    {
        await using var auth = await FakeAuthServer.StartAsync(Cancel);
        auth.Handler = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return context.Response.WriteAsync(new string('x', ForwardAuthLimits.MaxRelayedBodyBytes + 1));
        };
        var gate = GateFor(new ForwardAuthRuntime(), Model(Spec(auth.Address)), out _);

        Assert.Equal("502", await RunAsync(gate, Context()));
    }

    [Fact]
    public async Task AllowHeadersWithAControlCharacterRefuseInsteadOfReachingTheUpstream()
    {
        await using var auth = await FakeAuthServer.StartAsync(Cancel);
        // Kestrel itself refuses a line break in a response header, so the endpoint can only try the other control characters.
        auth.Handler = context =>
        {
            context.Response.Headers["Remote-User"] = "ali\u0001ce";
            return Task.CompletedTask;
        };
        var gate = GateFor(new ForwardAuthRuntime(), Model(Spec(auth.Address)), out _);

        Assert.Equal("502", await RunAsync(gate, Context()));
    }

    [Fact]
    public async Task ASignInUrlSendsABrowserNavigationToItWithTheOriginalAddressAndRelaysEverythingElse()
    {
        await using var auth = await FakeAuthServer.StartAsync(Cancel);
        auth.Handler = AllowsSessionOk();
        var runtime = new ForwardAuthRuntime();
        var gate = GateFor(runtime, Model(Spec(auth.Address, b =>
        {
            b.SignInUrl = "/oauth2/start";
            b.SignInReturnParameter = "rd";
        })), out _);

        var page = Context(accept: "text/html,application/xhtml+xml", rawTarget: "/docs/a%20b?x=1&y=2", path: "/docs/a b");
        Assert.Equal("302", await RunAsync(gate, page));
        Assert.Equal("/oauth2/start?rd=http%3A%2F%2Fapp.test%2Fdocs%2Fa%2520b%3Fx%3D1%26y%3D2", page.Response.Headers.Location.ToString());
        Assert.Equal((0, 0, 1), (runtime.CountersFor(Provider).Allowed, runtime.CountersFor(Provider).Denied, runtime.CountersFor(Provider).Redirected));

        // A script, a fetch and a form post are told 401 and left to handle it.
        Assert.Equal("401", await RunAsync(gate, Context(accept: "application/json")));
        Assert.Equal("401", await RunAsync(gate, Context()));
        Assert.Equal("401", await RunAsync(gate, Context(accept: "text/html", method: "POST")));
        var xhr = Context(accept: "text/html");
        xhr.Request.Headers["X-Requested-With"] = "XMLHttpRequest";
        Assert.Equal("401", await RunAsync(gate, xhr));
    }

    [Fact]
    public async Task ASignInUrlWithAQueryGetsTheReturnParameterAppended()
    {
        await using var auth = await FakeAuthServer.StartAsync(Cancel);
        var gate = GateFor(new ForwardAuthRuntime(), Model(Spec(auth.Address, b =>
        {
            b.SignInUrl = "https://login.example.com/start?client=web";
            b.SignInReturnParameter = "next";
        })), out _);
        var context = Context(accept: "text/html");
        context.Request.Scheme = "https";
        context.Request.IsHttps = true;

        Assert.Equal("302", await RunAsync(gate, context));
        Assert.Equal("https://login.example.com/start?client=web&next=https%3A%2F%2Fapp.test%2F", context.Response.Headers.Location.ToString());
    }

    [Fact]
    public async Task ASimulationMakesNoCallTouchesNoCounterAndWritesNoCache()
    {
        await using var auth = await FakeAuthServer.StartAsync(Cancel);
        auth.Handler = AllowsSessionOk();
        var runtime = new ForwardAuthRuntime();
        var gate = GateFor(runtime, Model(Spec(auth.Address)), out var snapshot);
        var policy = Assert.IsType<ForwardAuthPolicy>(snapshot.HostsByIndex[0].Routes[0].Policies[0]);
        var (action, sentence, state) = Simulate(policy, Context(cookie: "session=ok"));

        Assert.Equal(PolicyAction.Continue, action);
        Assert.Contains("would call provider 'sso'", sentence, StringComparison.Ordinal);
        Assert.Equal(0, auth.CallCount);
        var counters = runtime.CountersFor(Provider);
        Assert.Equal((0, 0, 0, 0, 0), (counters.Allowed, counters.Denied, counters.CacheHits, counters.Calls, counters.Errors));
        Assert.Equal(0, policy.Provider.State.CachedAllows);
        Assert.Null(state.ForwardAuthProvider);

        // A real request fills the cache; the simulation then says it would be answered from it, and still counts nothing.
        await RunAsync(gate, Context(cookie: "session=ok"));
        var hitsBefore = counters.CacheHits;
        var (againAction, againSentence, _) = Simulate(policy, Context(cookie: "session=ok"));
        Assert.Equal(PolicyAction.Continue, againAction);
        Assert.Contains("allowed these credentials a moment ago", againSentence, StringComparison.Ordinal);
        Assert.Equal(hitsBefore, counters.CacheHits);
        Assert.Equal(1, auth.CallCount);
    }

    private static (PolicyAction Action, string Sentence, GateRequestState State) Simulate(ForwardAuthPolicy policy, DefaultHttpContext context)
    {
        GateRequestState state = new() { ClientIp = new ClientIpResolution(IpKey.Parse("192.0.2.1"), ClientIpSource.Socket, ClientIpOutcome.PeerNotTrusted) };
        RequestView simulated = new(context, state, isSimulation: true);
        var verdict = policy.Evaluate(in simulated);
        return (verdict.Action, ((IRequestPolicy)policy).Explain(in simulated, verdict), state);
    }

    [Fact]
    public void AProviderThatNothingChangedKeepsItsCacheAcrossCompilesAndAChangeStartsAnEmptyOne()
    {
        var runtime = new ForwardAuthRuntime();
        var spec = Spec("http://127.0.0.1:1/auth");

        var first = runtime.Bind(spec, publish: true);
        // A rename isn't a change to what the endpoint is asked or how its answer is read.
        var renamed = runtime.Bind(Spec("http://127.0.0.1:1/auth", b => b.Name = "renamed"), publish: true);
        Assert.Same(first, renamed);

        foreach (var changed in new Action<AuthProviderSpecBuilder>[]
        {
            b => b.Scope = ForwardAuthCacheScope.Host,
            b => b.CacheTtl = TimeSpan.FromSeconds(11),
            b => b.Timeout = TimeSpan.FromSeconds(6),
            b => b.ResponseHeaders = ["Remote-User"],
            b => b.RequestHeaders = ["X-Tenant"],
            b => b.SignInUrl = "/start",
            b => b.SignInReturnParameter = "next",
        })
        {
            var specChanged = Spec("http://127.0.0.1:1/auth", b =>
            {
                b.SignInReturnParameter = b.SignInUrl = null;
                changed(b);
                if (b.SignInUrl is not null && b.SignInReturnParameter is null)
                {
                    b.SignInReturnParameter = "rd";
                }
            });
            Assert.NotSame(first, runtime.Bind(specChanged, publish: false));
        }

        Assert.NotSame(first, runtime.Bind(Spec("http://127.0.0.1:2/auth"), publish: false));
    }

    [Fact]
    public void ACompileThatWillNeverGoLiveLeavesTheProcessesProviderStatesAlone()
    {
        var runtime = new ForwardAuthRuntime();
        var spec = Spec("http://127.0.0.1:1/auth");
        var live = runtime.Bind(spec, publish: true);

        var draft = runtime.Bind(Spec("http://127.0.0.1:2/auth"), publish: false);

        Assert.NotSame(live, draft);
        Assert.Same(live, runtime.StateOf(Provider));
    }

    [Fact]
    public async Task APublishThatKeepsTheProviderKeepsItsCachedAllows()
    {
        await using var auth = await FakeAuthServer.StartAsync(Cancel);
        auth.Handler = AllowsSessionOk();
        var runtime = new ForwardAuthRuntime();
        var options = new SnapshotCompilerOptions { ForwardAuth = runtime };
        var model = Model(Spec(auth.Address));
        SnapshotStore store = new();
        store.Publish(TestSnapshots.Bound(model, options: options));
        GateMiddleware gate = new(store, new TrustedProxyClientAddressSource());

        await RunAsync(gate, Context(cookie: "session=ok"));

        // An unrelated change (another host) and a new revision: the provider is the same.
        var again = model with { Hosts = [.. model.Hosts, TestSnapshots.ProxyHost("extra", "extra.test", TestSnapshots.Route("extra-root", "web"))] };
        store.Publish(TestSnapshots.Bound(again, generation: 2, options: options));
        await RunAsync(gate, Context(cookie: "session=ok"));
        Assert.Equal(1, auth.CallCount);

        // A changed endpoint setting: nothing remembered applies to it.
        var changed = again with { AuthProviders = [Spec(auth.Address, b => b.Scope = ForwardAuthCacheScope.Host)] };
        store.Publish(TestSnapshots.Bound(changed, generation: 3, options: options));
        await RunAsync(gate, Context(cookie: "session=ok"));
        Assert.Equal(2, auth.CallCount);
    }

    [Fact]
    public void ACallPlaceIsTakenAndGivenBackAndTheLastOneRefuses()
    {
        var state = new ForwardAuthRuntime().Bind(Spec("http://127.0.0.1:1/auth"), publish: false);

        for (var i = 0; i < ForwardAuthLimits.MaxConcurrentCalls; i++)
        {
            Assert.True(state.TryEnter());
        }

        Assert.False(state.TryEnter());
        state.Exit();
        Assert.True(state.TryEnter());
    }

    [Fact]
    public async Task EveryCallPlaceIsGivenBackWhateverTheCallDid()
    {
        await using var auth = await FakeAuthServer.StartAsync(Cancel);
        auth.Handler = context =>
        {
            context.Response.StatusCode = context.Request.Headers.Cookie.ToString() switch { "a" => 500, "b" => 401, _ => 200 };
            return Task.CompletedTask;
        };
        var runtime = new ForwardAuthRuntime();
        var gate = GateFor(runtime, Model(Spec(auth.Address)), out var snapshot);

        foreach (var cookie in new[] { "a", "b", "c" })
        {
            await RunAsync(gate, Context(cookie: cookie));
        }

        var state = snapshot.HostsByIndex[0].Routes[0].AuthProvider!.State;
        for (var i = 0; i < ForwardAuthLimits.MaxConcurrentCalls; i++)
        {
            Assert.True(state.TryEnter(), $"place {i} was never given back");
        }
    }

    [Fact]
    public void ABadEndpointOrHeaderListIsACompileErrorNotAProviderThatFailsClosedLater()
    {
        foreach (var bad in new[]
        {
            Spec("ftp://auth.test/"),
            Spec("not a url"),
            Spec("http://user:pass@auth.test/"),
            Spec("http://auth.test/#frag"),
            Spec("http://auth.test/", b => b.ResponseHeaders = ["Host"]),
            Spec("http://auth.test/", b => b.ResponseHeaders = ["X-Forwarded-For"]),
            Spec("http://auth.test/", b => b.ResponseHeaders = ["a b"]),
            Spec("http://auth.test/", b => b.ResponseHeaders = ["X*"]),
            Spec("http://auth.test/", b => b.RequestHeaders = ["Content-Length"]),
            Spec("http://auth.test/", b => b.Timeout = TimeSpan.Zero),
            Spec("http://auth.test/", b => b.Timeout = TimeSpan.FromSeconds(31)),
            Spec("http://auth.test/", b => b.CacheTtl = TimeSpan.FromSeconds(301)),
            Spec("http://auth.test/", b => { b.SignInUrl = "//evil.test/x"; b.SignInReturnParameter = "rd"; }),
            Spec("http://auth.test/", b => { b.SignInUrl = "javascript:alert(1)"; b.SignInReturnParameter = "rd"; }),
            Spec("http://auth.test/", b => { b.SignInUrl = "/x"; b.SignInReturnParameter = "a&b"; }),
        })
        {
            var result = SnapshotCompiler.Compile(Model(bad), new SnapshotCompilerOptions { ForwardAuth = new ForwardAuthRuntime() });

            Assert.False(result.Succeeded, bad.Url + " " + string.Join(",", bad.ResponseHeaders));
        }
    }

    [Fact]
    public void ARouteThatNamesAProviderTheDocumentLacksIsACompileError()
    {
        ProxyConfigModel model = new()
        {
            Upstreams = [TestSnapshots.Upstream("web", "http://127.0.0.1:1")],
            Hosts = [TestSnapshots.ProxyHost("app", "app.test", TestSnapshots.Route("app-root", "web") with { AuthProvider = TestIds.AuthProvider("missing") })],
        };

        var result = SnapshotCompiler.Compile(model);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Message.Contains("doesn't exist", StringComparison.Ordinal));
    }

    [Fact]
    public void TheProviderSitsAfterTheRateLimitAndBeforeProofOfWork()
    {
        var model = Model(Spec("http://127.0.0.1:1/auth")) with
        {
            RateLimitPolicies = [new RateLimitPolicySpec { Id = TestIds.RateLimitPolicy("rl"), Name = "rl", Parameters = new RateLimitParameters(10, 10) }],
        };
        var host = model.Hosts[0] with
        {
            RateLimit = new HostRateLimitSpec { Enabled = true, PolicyId = TestIds.RateLimitPolicy("rl") },
            ProofOfWork = new HostProofOfWorkSpec { Enabled = true },
            Routes = [model.Hosts[0].Routes[0] with { RateLimit = TestIds.RateLimitPolicy("rl") }],
        };

        var snapshot = TestSnapshots.Compile(model with { Hosts = [host] }, new SnapshotCompilerOptions { ForwardAuth = new ForwardAuthRuntime() });

        Assert.Equal(
            ["RateLimitPolicy", "ForwardAuthPolicy", "ProofOfWorkPolicy"],
            snapshot.HostsByIndex[0].Routes[0].Policies.Select(static p => p.GetType().Name));
    }
}

/// <summary>A clock a test moves by hand.</summary>
internal sealed class ManualTime : TimeProvider
{
    private long _ticks = 1_000_000_000;

    public override long TimestampFrequency => 1_000_000_000;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, (long)(by.TotalSeconds * TimestampFrequency));
}
