using System.Net;
using System.Text;

using Microsoft.AspNetCore.Http;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Hosting;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Integration.Proxy.PublicProxyTests;

namespace Paduraksa.Tests.Integration.Proxy;

/// <summary>A forward-auth provider through the whole public host and a real socket, with a fake auth endpoint and an upstream that echoes
/// what it was sent: what an unauthenticated browser and a script are told, what the upstream gets when the provider allows (and that a
/// client's own identity headers never reach it), what an unreachable or slow endpoint does, and that a route that turns authentication off
/// is open.</summary>
public sealed class PublicForwardAuthTests : IAsyncLifetime
{
    private static readonly AuthProviderId Sso = TestIds.AuthProvider("sso");

    private EdgeUpstream? _upstream;
    private FakeAuthServer? _auth;

    private EdgeUpstream Upstream => _upstream!;

    private FakeAuthServer Auth => _auth!;

    public async ValueTask InitializeAsync()
    {
        _upstream = await EdgeUpstream.StartAsync();
        _auth = await FakeAuthServer.StartAsync(TestContext.Current.CancellationToken);
        Auth.Handler = AllowsSession;
    }

    public async ValueTask DisposeAsync()
    {
        if (_upstream is not null)
        {
            await _upstream.DisposeAsync();
        }

        if (_auth is not null)
        {
            await _auth.DisposeAsync();
        }
    }

    // Authelia's shape: an unauthenticated browser is redirected to the portal, anything else is told 401; a session is Remote-* headers.
    private static Task AllowsSession(HttpContext context)
    {
        if (context.Request.Headers.Cookie.ToString().Contains("session=alice", StringComparison.Ordinal))
        {
            context.Response.Headers["Remote-User"] = "alice";
            context.Response.Headers["Remote-Groups"] = "admins,dev";
            return Task.CompletedTask;
        }

        if (context.Request.Headers.Accept.ToString().Contains("text/html", StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status302Found;
            context.Response.Headers.Location = "https://auth.example.com/?rd=" + Uri.EscapeDataString("https://" + context.Request.Headers["X-Forwarded-Host"] + context.Request.Headers["X-Forwarded-Uri"]);
            context.Response.Headers.Append("Set-Cookie", "portal_state=abc; Path=/; HttpOnly");
            return Task.CompletedTask;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return context.Response.WriteAsync("unauthorized");
    }

    [Fact]
    public async Task ABrowserWithoutASessionIsSentToThePortalAndTheUpstreamNeverSeesTheRequest()
    {
        await using var host = await StartedAsync();

        var (response, upstream) = await SendAsync(host, "app.test", "/docs/a%20b?x=1", request => request.Headers.Accept.ParseAdd("text/html"));

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("https://auth.example.com/?rd=https%3A%2F%2Fapp.test%2Fdocs%2Fa%2520b%3Fx%3D1", response.Headers.Location!.ToString());
        Assert.Contains("portal_state=abc; path=/; httponly", response.Headers.GetValues("Set-Cookie").Single(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(upstream);
        var call = Assert.Single(Auth.Calls);
        Assert.Equal("GET", call.Method);
        Assert.Equal("app.test", call.Header("X-Forwarded-Host"));
        Assert.Equal("/docs/a%20b?x=1", call.Header("X-Forwarded-Uri"));
    }

    [Fact]
    public async Task AScriptWithoutASessionIsTold401WithTheProvidersOwnBody()
    {
        await using var host = await StartedAsync();

        var (response, _) = await SendAsync(host, "app.test", "/api/items", request => request.Headers.Accept.ParseAdd("application/json"), readBody: true);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("unauthorized", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ASessionLetsTheRequestThroughWithTheProvidersIdentityAndNeverTheClients()
    {
        await using var host = await StartedAsync();

        var (response, upstream) = await SendAsync(host, "app.test", "/docs", request =>
        {
            request.Headers.TryAddWithoutValidation("Cookie", "session=alice; theme=dark");
            // What a client would forge to be someone else: the provider's headers by name, in another case, and a header it doesn't own.
            request.Headers.TryAddWithoutValidation("Remote-User", "root");
            request.Headers.TryAddWithoutValidation("remote-groups", "wheel");
            request.Headers.TryAddWithoutValidation("X-Other", "kept");
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("alice", upstream["Remote-User"]);
        Assert.Equal("admins,dev", upstream["Remote-Groups"]);
        Assert.Equal("session=alice; theme=dark", upstream["Cookie"]);
        Assert.Equal("/docs", upstream["target"]);
        var call = Assert.Single(Auth.Calls);
        Assert.Equal("session=alice; theme=dark", call.Header("Cookie"));
        Assert.Null(call.Header("Remote-User"));
        Assert.Null(call.Header("X-Other"));
    }

    [Fact]
    public async Task AnIdentityValueWithNonAsciiTextReachesTheUpstreamAsUtf8InsteadOfAnErrorForTheWholeRequest()
    {
        await using var host = await StartedAsync();
        Auth.Handler = context =>
        {
            context.Response.Headers["Remote-User"] = "José \u03A9mega";
            context.Response.Headers["Remote-Groups"] = "admins";
            return Task.CompletedTask;
        };

        // The same twice: the second is the remembered allow's headers.
        for (var i = 0; i < 2; i++)
        {
            var (response, upstream) = await SendAsync(host, "app.test", "/docs", request => request.Headers.TryAddWithoutValidation("Cookie", "session=alice"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("José \u03A9mega", upstream["Remote-User"]);
            Assert.Equal("admins", upstream["Remote-Groups"]);
        }

        Assert.Single(Auth.Calls);
    }

    [Fact]
    public async Task AForgedIdentityWithoutASessionIsStillRefusedAndNothingReachesTheUpstream()
    {
        await using var host = await StartedAsync();

        var (response, upstream) = await SendAsync(host, "app.test", "/admin", request =>
        {
            request.Headers.TryAddWithoutValidation("Remote-User", "root");
            request.Headers.TryAddWithoutValidation("Remote-Groups", "admins");
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(upstream);
        Assert.Null(Assert.Single(Auth.Calls).Header("Remote-User"));
    }

    [Fact]
    public async Task AnAllowIsRememberedSoASecondRequestCostsNoCallAndAnotherSessionDoes()
    {
        await using var host = await StartedAsync();
        static void Alice(HttpRequestMessage request) => request.Headers.TryAddWithoutValidation("Cookie", "session=alice");

        await SendAsync(host, "app.test", "/docs", Alice);
        var (second, upstream) = await SendAsync(host, "app.test", "/docs", Alice);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("alice", upstream["Remote-User"]);
        Assert.Equal(1, Auth.CallCount);
        var counters = host.Shared.PublicListeners.CompilerOptions.ForwardAuth!.CountersFor(Sso);
        Assert.Equal((2, 1), (counters.Allowed, counters.CacheHits));

        // The endpoint revokes the session; the proxy honours the allow for its remembered time and no longer.
        var (other, _) = await SendAsync(host, "app.test", "/docs", request => request.Headers.TryAddWithoutValidation("Cookie", "session=bob"));
        Assert.Equal(HttpStatusCode.Unauthorized, other.StatusCode);
        Assert.Equal(2, Auth.CallCount);
    }

    [Fact]
    public async Task ARouteThatTurnsAuthenticationOffIsOpenAndAskedNothing()
    {
        await using var host = await StartedAsync();

        var (response, upstream) = await SendAsync(host, "app.test", "/oauth2/callback?code=1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/oauth2/callback?code=1", upstream["target"]);
        Assert.Equal(0, Auth.CallCount);
    }

    [Fact]
    public async Task ARouteThatTurnsAuthenticationOffStillNeverPassesAForgedIdentityUpstream()
    {
        await using var host = await StartedAsync();

        var (response, upstream) = await SendAsync(host, "app.test", "/oauth2/callback?code=1", request =>
        {
            request.Headers.TryAddWithoutValidation("Remote-User", "root");
            request.Headers.TryAddWithoutValidation("remote_groups", "wheel");
            request.Headers.TryAddWithoutValidation("X-Other", "kept");
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(upstream.ContainsKey("Remote-User"));
        Assert.False(upstream.ContainsKey("Remote_Groups"));
        Assert.False(upstream.ContainsKey("Remote-Groups"));
        Assert.Equal("kept", upstream["X-Other"]);
        Assert.Equal(0, Auth.CallCount);
    }

    [Fact]
    public async Task AnUnderscoreCopyOfAnIdentityHeaderNeverReachesTheUpstreamOnAGuardedRoute()
    {
        await using var host = await StartedAsync();

        var (response, upstream) = await SendAsync(host, "app.test", "/docs", request =>
        {
            request.Headers.TryAddWithoutValidation("Cookie", "session=alice");
            request.Headers.TryAddWithoutValidation("Remote_User", "root");
            request.Headers.TryAddWithoutValidation("REMOTE_GROUPS", "wheel");
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("alice", upstream["Remote-User"]);
        Assert.False(upstream.ContainsKey("Remote_User"));
        Assert.False(upstream.ContainsKey("Remote_Groups"));
        Assert.Equal("admins,dev", upstream["Remote-Groups"]);
    }

    [Fact]
    public async Task ASignInUrlSendsABrowserToTheProvidersStartPageWithTheOriginalAddress()
    {
        await using var host = await StartedAsync(signIn: ("/oauth2/start", "rd"));
        Auth.Handler = static context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };

        var (response, upstream) = await SendAsync(host, "app.test", "/docs?x=1", request => request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml"));

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/oauth2/start?rd=http%3A%2F%2Fapp.test%2Fdocs%3Fx%3D1", response.Headers.Location!.OriginalString);
        Assert.Empty(upstream);
    }

    [Fact]
    public async Task AnUnreachableEndpointRefusesWith502AndThePage()
    {
        await using var host = await StartedAsync(endpoint: "http://127.0.0.1:1/");

        var (response, upstream) = await SendAsync(host, "app.test", "/docs", request =>
        {
            request.Headers.TryAddWithoutValidation("Cookie", "session=alice");
            request.Headers.Accept.ParseAdd("text/html");
        }, readBody: true);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("<h1>502 Bad Gateway</h1>", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Empty(upstream);
    }

    [Fact]
    public async Task ASlowEndpointRefusesWith504AndAnEndpointErrorWith502()
    {
        await using var host = await StartedAsync(timeout: TimeSpan.FromMilliseconds(400));
        // A slow endpoint: the 400 ms timeout of the host is what ends the request.
        Auth.Handler = context => Task.Delay(TimeSpan.FromSeconds(10), context.RequestAborted);

        var (slow, slowUpstream) = await SendAsync(host, "app.test", "/docs", request => request.Headers.TryAddWithoutValidation("Cookie", "session=alice"));

        Assert.Equal(HttpStatusCode.GatewayTimeout, slow.StatusCode);
        Assert.Empty(slowUpstream);

        Auth.Handler = static context =>
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Task.CompletedTask;
        };
        var (broken, brokenUpstream) = await SendAsync(host, "app.test", "/docs", request => request.Headers.TryAddWithoutValidation("Cookie", "session=alice"));

        Assert.Equal(HttpStatusCode.BadGateway, broken.StatusCode);
        Assert.Empty(brokenUpstream);
    }

    [Fact]
    public async Task ABodyIsNeverSentToTheEndpointAndReachesTheUpstreamOnlyWhenAllowed()
    {
        await using var host = await StartedAsync();
        var payload = new string('x', 100_000);

        var (denied, _) = await SendAsync(host, "app.test", "/upload", request =>
        {
            request.Method = HttpMethod.Post;
            request.Content = new StringContent(payload, Encoding.UTF8, "text/plain");
        });
        var (allowed, _) = await SendAsync(host, "app.test", "/upload", request =>
        {
            request.Method = HttpMethod.Post;
            request.Headers.TryAddWithoutValidation("Cookie", "session=alice");
            request.Content = new StringContent(payload, Encoding.UTF8, "text/plain");
        });

        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.All(Auth.Calls, call =>
        {
            Assert.Equal("GET", call.Method);
            Assert.Equal("POST", call.Header("X-Forwarded-Method"));
            Assert.Null(call.Header("Content-Length"));
            Assert.Null(call.Header("Content-Type"));
        });
    }

    private async Task<PaduraksaHost> StartedAsync(
        string? endpoint = null, TimeSpan? timeout = null, (string Url, string Parameter)? signIn = null)
    {
        var host = TestHost.Create();
        await host.StartAsync(TestContext.Current.CancellationToken);
        AuthProviderSpec provider = new()
        {
            Id = Sso,
            Name = "sso",
            Url = endpoint ?? Auth.Address + "/",
            Timeout = timeout ?? TimeSpan.FromSeconds(5),
            ResponseHeaders = ["Remote-Groups", "Remote-User"],
            SignInUrl = signIn?.Url,
            SignInReturnParameter = signIn?.Parameter,
        };
        ProxyConfigModel model = new()
        {
            Upstreams = [new UpstreamSpec { Id = TestIds.Upstream("echo"), Destinations = [new(TestIds.Destination("echo-1"), Upstream.Address)] }],
            AuthProviders = [provider],
            Hosts =
            [
                new HostSpec
                {
                    Id = TestIds.Host("app"),
                    Names = ["app.test"],
                    Routes =
                    [
                        new RouteSpec { Id = TestIds.Route("callback"), Upstream = TestIds.Upstream("echo"), Match = RouteMatchKind.Prefix, Path = "/oauth2" },
                        new RouteSpec { Id = TestIds.Route("root"), Upstream = TestIds.Upstream("echo"), AuthProvider = Sso },
                    ],
                },
            ],
        };
        var published = await host.Shared.SnapshotPublisher.PublishAsync(Compile(host, model), TestContext.Current.CancellationToken);
        Assert.True(published.Succeeded, string.Join("; ", published.Errors));
        return host;
    }

    private static async Task<(HttpResponseMessage Response, Dictionary<string, string> Upstream)> SendAsync(
        PaduraksaHost host, string hostHeader, string path, Action<HttpRequestMessage>? configure = null, bool readBody = false)
    {
        using var client = EdgeClient.Create(out _);
        using var request = EdgeClient.Request(host, "http", hostHeader, path, null);
        configure?.Invoke(request);
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        if (readBody)
        {
            response.Content = new StringContent(body, Encoding.UTF8, response.Content.Headers.ContentType?.MediaType);
        }

        return (response, response.IsSuccessStatusCode ? EdgeUpstream.Parse(body) : []);
    }
}
