using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Paduraksa.Contracts.Config;
using Paduraksa.Core.Hosting;
using Paduraksa.Core.Model;
using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Hosting;
using Paduraksa.Tests.Shared;

namespace Paduraksa.Tests.Integration.Admin;

/// <summary>
/// End to end: an admin host on the public HTTPS listener serves the real admin app through its internal listener,
/// with the public certificate, the public origin's CSRF, the real client IP and the session cookie; and the lock-out guard
/// refuses to cut the caller off from it unless the request says so.
/// </summary>
public sealed class AdminThroughProxyTests(SharedAdminProxy shared) : IClassFixture<SharedAdminProxy>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheAdminHostServesACookieSessionThroughThePublicProxy()
    {
        var harness = shared.Harness;

        using var me = await harness.SendAsync(
            HttpMethod.Get,
            "/api/v1/auth/me",
            cookie: harness.SessionHeader,
            origin: "https://" + AdminProxyHarness.AdminHost,
            headers: new Dictionary<string, string> { [AdminInternalListener.HeaderName] = "forged-by-the-client" });
        Assert.True(me.StatusCode == HttpStatusCode.OK, $"{(int)me.StatusCode}: {await me.Content.ReadAsStringAsync(Ct)}");

        using var json = JsonDocument.Parse(await me.Content.ReadAsStringAsync(Ct));
        Assert.Equal(AdminHarness.OwnerName, json.RootElement.GetProperty("user").GetProperty("username").GetString());
        Assert.Equal("session", json.RootElement.GetProperty("authKind").GetString());

        // The admin's own response reaches the browser: the sign-in that started this harness set the session cookie.
        Assert.NotNull(harness.AdminCookie);
    }

    [Fact]
    public async Task CsrfUsesThePublicOriginAndRefusesAForeignOne()
    {
        var harness = shared.Harness;
        // A session of its own: the test signs out, and the class's other tests keep using the harness's.
        var cookie = await shared.NewSessionAsync();

        // Origin only (no Sec-Fetch-Site): it must equal the scheme/host/port the public client used.
        using var foreign = await harness.SendAsync(
            HttpMethod.Post, "/api/v1/auth/logout", cookie: cookie, origin: "https://evil.test");
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        using (var problem = JsonDocument.Parse(await foreign.Content.ReadAsStringAsync(Ct)))
        {
            Assert.Equal("csrf.refused", problem.RootElement.GetProperty("code").GetString());
        }

        using var sameOrigin = await harness.SendAsync(
            HttpMethod.Post, "/api/v1/auth/logout", cookie: cookie, origin: "https://" + AdminProxyHarness.AdminHost);
        Assert.Equal(HttpStatusCode.NoContent, sameOrigin.StatusCode);
    }

    [Fact]
    public async Task TheSessionCookieReachesTheAdminAndNoOtherUpstreamAndCookieTossingIsStripped()
    {
        var (upstream, address) = await HeaderEchoUpstreamHost.StartAsync(
            static response => response.Headers["Set-Cookie"] = "__Host-paduraksa-session=tossed; Path=/; Secure",
            Ct);
        using var upstreamHost = upstream;
        await using var harness = await AdminProxyHarness.StartAsync(upstreamAddress: address);

        // The upstream sees every cookie but the admin session cookie, and its own Set-Cookie for that name is removed. The
        // internal hop header a client sends is removed too; it never reaches an upstream.
        using var other = await harness.SendAsync(
            HttpMethod.Get,
            "/",
            host: AdminProxyHarness.OtherHost,
            headers: new Dictionary<string, string>
            {
                ["Cookie"] = "__Host-paduraksa-session=live; theme=dark",
                [AdminInternalListener.HeaderName] = "forged-by-the-client",
            });
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        var seen = HeaderEchoUpstreamHost.Parse(await other.Content.ReadAsStringAsync(Ct));
        Assert.Equal("theme=dark", seen["Cookie"]);
        Assert.DoesNotContain("live", string.Join('|', seen.Values), StringComparison.Ordinal);
        Assert.False(seen.ContainsKey(AdminInternalListener.HeaderName));
        Assert.False(other.Headers.TryGetValues("Set-Cookie", out _));

        // The admin route carries the cookie through.
        using var admin = await harness.SendAsync(
            HttpMethod.Get, "/api/v1/auth/me", cookie: harness.SessionHeader, origin: "https://" + AdminProxyHarness.AdminHost);
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
    }

    [Fact]
    public async Task TheThrottleSeesTheForwardedClientSoTwoClientsAreSeparate()
    {
        await using var harness = await AdminProxyHarness.StartAsync(
            settings: new Dictionary<string, string?>
            {
                ["Paduraksa:Admin:RateLimit:PermitsPerIp"] = "1",
                ["Paduraksa:Admin:RateLimit:PermitsPerUser"] = "100",
            },
            trustedProxies: new TrustedProxySettings(TrustedProxyMode.Generic, ["127.0.0.1/32"], TrustForwardedProto: true));

        var body = Body.Login(AdminHarness.OwnerName, AdminHarness.Password);
        using var first = await harness.SendAsync(
            HttpMethod.Post, "/api/v1/auth/login", body, origin: "https://" + AdminProxyHarness.AdminHost,
            headers: new Dictionary<string, string> { ["X-Forwarded-For"] = "203.0.113.1" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var sameClient = await harness.SendAsync(
            HttpMethod.Post, "/api/v1/auth/login", body, origin: "https://" + AdminProxyHarness.AdminHost,
            headers: new Dictionary<string, string> { ["X-Forwarded-For"] = "203.0.113.1" });
        Assert.Equal(HttpStatusCode.TooManyRequests, sameClient.StatusCode);

        using var otherClient = await harness.SendAsync(
            HttpMethod.Post, "/api/v1/auth/login", body, origin: "https://" + AdminProxyHarness.AdminHost,
            headers: new Dictionary<string, string> { ["X-Forwarded-For"] = "203.0.113.2" });
        Assert.Equal(HttpStatusCode.OK, otherClient.StatusCode);
    }

    [Fact]
    public async Task ClientSentForwardedHeadersAreOverwritten()
    {
        var harness = shared.Harness;

        // If the admin trusted the client's spoofed proto/host, this Origin wouldn't match; it matches the public request.
        using var accepted = await harness.SendAsync(
            HttpMethod.Post,
            "/api/v1/auth/login",
            Body.Login(AdminHarness.OwnerName, AdminHarness.Password),
            origin: "https://" + AdminProxyHarness.AdminHost,
            headers: new Dictionary<string, string>
            {
                ["X-Forwarded-For"] = "198.51.100.9",
                ["X-Forwarded-Proto"] = "http",
                ["X-Forwarded-Host"] = "evil.test",
            });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        // The spoofed Origin is refused: the overwritten host/proto, not the client's, decide.
        using var refused = await harness.SendAsync(
            HttpMethod.Post,
            "/api/v1/auth/logout",
            cookie: harness.SessionHeader,
            origin: "https://evil.test");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        // The client IP the admin recorded is the public listener's peer, not the spoofed address.
        var logins = await harness.AuditAsync("auth.login");
        Assert.NotEmpty(logins);
        Assert.All(logins, entry => Assert.Equal("127.0.0.1", entry.ClientIp));
    }

    [Fact]
    public async Task TheInternalSocketIsPrivateAndReachableOnlyThroughTheAdminHost()
    {
        var harness = shared.Harness;

        var internalListener = harness.Host.Shared.AdminInternal;
        Assert.True(internalListener.Enabled);
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(internalListener.UsesUnixSocket, "this platform should bind the Unix socket");
            var socket = internalListener.SocketPath!;
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(socket));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.GetDirectoryName(socket)!));
        }

        // Only the admin host's name reaches it: an unknown name is the default 404, not the admin app's problem.
        using var unknown = await harness.SendAsync(
            HttpMethod.Get, "/api/v1/auth/me", host: "not-the-admin.test", origin: "https://not-the-admin.test");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        // And the configured admin host does reach it (an anonymous call is the admin API's own 401 problem).
        using var admin = await harness.SendAsync(
            HttpMethod.Get, "/api/v1/auth/me", origin: "https://" + AdminProxyHarness.AdminHost);
        Assert.Equal(HttpStatusCode.Unauthorized, admin.StatusCode);
        using var problem = JsonDocument.Parse(await admin.Content.ReadAsStringAsync(Ct));
        Assert.Equal("auth.required", problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ARequestWithoutTheInternalHopSecretIsRefused()
    {
        var harness = shared.Harness;
        var listener = harness.Host.Shared.AdminInternal;

        var (status, headers, body) = await InternalAsync(
            listener, "GET", "/healthz", secret: null, xff: "203.0.113.9", proto: "https");

        Assert.Equal(HttpStatusCode.Forbidden, (HttpStatusCode)status);
        Assert.Contains("\"code\":\"admin.internalHop\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Strict-Transport-Security", headers, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheAllowlistAppliesPerRequestToTheForwardedClient()
    {
        await using var harness = await AdminProxyHarness.StartAsync(
            settings: new Dictionary<string, string?>
            {
                ["Paduraksa:Listen:AdminAllowedSources:0"] = "127.0.0.1/32",
            },
            trustedProxies: new TrustedProxySettings(TrustedProxyMode.Generic, ["127.0.0.1/32"], TrustForwardedProto: true));

        using var denied = await harness.SendAsync(
            HttpMethod.Get,
            "/healthz",
            headers: new Dictionary<string, string> { ["X-Forwarded-For"] = "203.0.113.9" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var problem = JsonDocument.Parse(await denied.Content.ReadAsStringAsync(Ct)))
        {
            Assert.Equal("admin.sourceDenied", problem.RootElement.GetProperty("code").GetString());
        }

        // A missing X-Forwarded-For is unknown, not loopback: the same restricted list refuses it.
        var (status, _, body) = await InternalAsync(
            harness.Host.Shared.AdminInternal, "GET", "/healthz", secret: harness.Host.Shared.AdminInternal.Secret, xff: null, proto: "https");
        Assert.Equal(HttpStatusCode.Forbidden, (HttpStatusCode)status);
        Assert.Contains("\"code\":\"admin.sourceDenied\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HstsFollowsTheForwardedScheme()
    {
        var harness = shared.Harness;
        var listener = harness.Host.Shared.AdminInternal;

        var (httpsStatus, httpsHeaders, _) = await InternalAsync(
            listener, "GET", "/healthz", listener.Secret, xff: "127.0.0.1", proto: "https");
        Assert.Equal(HttpStatusCode.OK, (HttpStatusCode)httpsStatus);
        Assert.Contains("Strict-Transport-Security", httpsHeaders, StringComparison.OrdinalIgnoreCase);

        var (httpStatus, httpHeaders, _) = await InternalAsync(
            listener, "GET", "/healthz", listener.Secret, xff: "127.0.0.1", proto: "http");
        Assert.Equal(HttpStatusCode.OK, (HttpStatusCode)httpStatus);
        Assert.DoesNotContain("Strict-Transport-Security", httpHeaders, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheAdminHostsOwnHstsSettingReplacesTheApps()
    {
        // The admin app sets its own header on the internal listener; the host's resolved HSTS, when it has one, is the
        // one the browser sees. The class's own host is the one with no setting of its own.
        var plain = shared.Harness;
        using (var untouched = await plain.SendAsync(HttpMethod.Get, "/api/v1/auth/me", cookie: plain.SessionHeader))
        {
            Assert.Equal(HttpStatusCode.OK, untouched.StatusCode);
            Assert.Equal("max-age=31536000", Assert.Single(untouched.Headers.GetValues("Strict-Transport-Security")));
        }

        var configured = shared.Configured;
        using var ours = await configured.SendAsync(HttpMethod.Get, "/api/v1/auth/me", cookie: configured.SessionHeader);
        Assert.Equal(HttpStatusCode.OK, ours.StatusCode);
        Assert.Equal("max-age=600", Assert.Single(ours.Headers.GetValues("Strict-Transport-Security")));
    }

    [Fact]
    public async Task TheAdminHostsOwnServerHeaderSettingWinsThroughTheProxy()
    {
        // An admin host through the proxy follows its own resolved Server setting, like any other host.
        var plain = shared.Harness;
        using (var untouched = await plain.SendAsync(HttpMethod.Get, "/api/v1/auth/me", cookie: plain.SessionHeader))
        {
            Assert.Equal(HttpStatusCode.OK, untouched.StatusCode);
            Assert.Equal("Paduraksa", Assert.Single(untouched.Headers.GetValues("Server")));
        }

        // The admin app's own listener doesn't add Kestrel's either; the host's header is the one on the public response.
        var listener = plain.Host.Shared.AdminInternal;
        var (status, headers, _) = await InternalAsync(listener, "GET", "/healthz", listener.Secret, xff: "127.0.0.1", proto: "https");
        Assert.Equal(HttpStatusCode.OK, (HttpStatusCode)status);
        Assert.DoesNotContain("Server:", headers, StringComparison.OrdinalIgnoreCase);

        var configured = shared.Configured;
        using var ours = await configured.SendAsync(HttpMethod.Get, "/api/v1/auth/me", cookie: configured.SessionHeader);
        Assert.Equal(HttpStatusCode.OK, ours.StatusCode);
        Assert.Equal("admin-edge/1.0", Assert.Single(ours.Headers.GetValues("Server")));
    }

    [Fact]
    public async Task SetupIsRefusedThroughTheInternalListener()
    {
        var harness = shared.Harness;

        using var get = await harness.SendAsync(
            HttpMethod.Get, "/api/v1/setup", origin: "https://" + AdminProxyHarness.AdminHost);
        Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);
        using var problem = JsonDocument.Parse(await get.Content.ReadAsStringAsync(Ct));
        Assert.Equal("setup.viaProxy", problem.RootElement.GetProperty("code").GetString());

        using var post = await harness.SendAsync(
            HttpMethod.Post,
            "/api/v1/setup",
            """{"setupToken":"x","username":"x","password":"x"}""",
            origin: "https://" + AdminProxyHarness.AdminHost);
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        using var postProblem = JsonDocument.Parse(await post.Content.ReadAsStringAsync(Ct));
        Assert.Equal("setup.viaProxy", postProblem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task TheTcpFallbackWorksEndToEndAndStillRequiresTheHopSecret()
    {
        var dataDirectory = Path.Combine(TestDataDirectories.NewPath(), new string('d', 120), "data");
        LogCapture logs = new();
        await using var harness = await AdminProxyHarness.StartAsync(
            settings: new Dictionary<string, string?> { ["Paduraksa:DataDirectory"] = dataDirectory },
            logs: logs);

        var listener = harness.Host.Shared.AdminInternal;
        Assert.False(listener.UsesUnixSocket);
        Assert.NotNull(listener.LoopbackEndPoint);
        Assert.NotNull(listener.FallbackReason);
        Assert.Contains(logs.Entries, entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning
            && entry.Message.Contains("TCP fallback", StringComparison.Ordinal));

        // The proxied path works over the fallback.
        using var me = await harness.SendAsync(
            HttpMethod.Get, "/api/v1/auth/me", cookie: harness.SessionHeader, origin: "https://" + AdminProxyHarness.AdminHost);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        // A local process on the fallback port can't forge forwarded headers without the secret.
        var (status, _, body) = await InternalAsync(
            listener, "GET", "/healthz", secret: null, xff: "203.0.113.9", proto: "https");
        Assert.Equal(HttpStatusCode.Forbidden, (HttpStatusCode)status);
        Assert.Contains("\"code\":\"admin.internalHop\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWebSocketUpgradePassesThroughTheAdminPipeline()
    {
        await using var harness = await AdminProxyHarness.StartAsync(
            configureServices: static (role, services) =>
            {
                if (role == HostRole.Admin)
                {
                    services.AddSingleton<IStartupFilter, AdminWebSocketEcho>();
                }
            });

        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var response = await UpgradeAsync(harness, "/ws-echo", key);
        Assert.StartsWith("HTTP/1.1 101", response, StringComparison.Ordinal);
        Assert.Contains("Sec-WebSocket-Accept:", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheDashboardAndItsCircuitWorkThroughTheProxy()
    {
        var harness = shared.Harness;

        // The page, as a browser on the public name opens it: the dashboard's policy, and HSTS for the public HTTPS name.
        using (var page = await harness.SendAsync(
            HttpMethod.Get, "/", cookie: harness.SessionHeader, headers: new Dictionary<string, string> { ["Accept"] = "text/html" }))
        {
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Equal(Paduraksa.Dashboard.DashboardSecurity.ContentSecurityPolicy, string.Join(",", page.Headers.GetValues("Content-Security-Policy")));
            Assert.True(page.Headers.Contains("Strict-Transport-Security"));
        }

        // The circuit: negotiate, then the WebSocket upgrade, both through the public listener and the hop-checked internal one.
        string connectionToken;
        using (var negotiate = await harness.SendAsync(
            HttpMethod.Post, "/_blazor/negotiate?negotiateVersion=1", cookie: harness.SessionHeader, fetchSite: "same-origin"))
        {
            Assert.Equal(HttpStatusCode.OK, negotiate.StatusCode);
            using var body = JsonDocument.Parse(await negotiate.Content.ReadAsStringAsync(Ct));
            connectionToken = body.RootElement.GetProperty("connectionToken").GetString()!;
        }

        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var upgraded = await UpgradeAsync(harness, $"/_blazor?id={Uri.EscapeDataString(connectionToken)}", key, harness.SessionHeader);
        Assert.StartsWith("HTTP/1.1 101", upgraded, StringComparison.Ordinal);

        // Without the session the hub refuses the upgrade.
        var refused = await UpgradeAsync(harness, $"/_blazor?id={Uri.EscapeDataString(connectionToken)}", key);
        Assert.StartsWith("HTTP/1.1 401", refused, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSetupPageIsNotServedThroughTheProxy()
    {
        var harness = shared.Harness;

        foreach (var (method, path) in new[] { (HttpMethod.Get, "/setup"), (HttpMethod.Post, "/auth/setup") })
        {
            using var response = await harness.SendAsync(method, path, fetchSite: "same-origin");
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Contains("\"code\":\"setup.viaProxy\"", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TheLockoutGuardRefusesThroughTheProxyAndForceAllows()
    {
        // Its own host: the forced delete removes the admin host from the config.
        await using var harness = await AdminProxyHarness.StartAsync();
        var path = $"/api/v1/hosts/{AdminProxyHarness.AdminHostId}";

        using var refused = await harness.SendAsync(
            HttpMethod.Delete, path, cookie: harness.SessionHeader, origin: "https://" + AdminProxyHarness.AdminHost,
            headers: new Dictionary<string, string> { ["If-Match"] = "\"1\"" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        using (var problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync(Ct)))
        {
            Assert.Contains(
                problem.RootElement.GetProperty("errors").EnumerateArray(),
                error => error.GetProperty("code").GetString() == "host.admin.lockout");
        }

        using var forced = await harness.SendAsync(
            HttpMethod.Delete, path + "?force=true", cookie: harness.SessionHeader, origin: "https://" + AdminProxyHarness.AdminHost,
            headers: new Dictionary<string, string> { ["If-Match"] = "\"1\"" });
        Assert.Equal(HttpStatusCode.NoContent, forced.StatusCode);
    }

    [Fact]
    public async Task TheAdminHostCanBeDeletedOverTheNormalAdminPort()
    {
        // Its own host: the delete removes the admin host from the config.
        await using var harness = await AdminProxyHarness.StartAsync();

        // The ordinary admin port is not the admin host, so the guard doesn't apply there.
        using var deleted = await harness.Admin.SendAsync(
            HttpMethod.Delete,
            $"/api/v1/hosts/{AdminProxyHarness.AdminHostId}",
            cookie: harness.OwnerCookie,
            headers: new Dictionary<string, string> { ["If-Match"] = "\"1\"" });
        Assert.Equal(HttpStatusCode.NoContent, deleted.Status);
    }

    [Fact]
    public async Task ClientSentForwardedHeadersAreIgnoredOnTheAdminPort()
    {
        var harness = shared.Harness;

        // A spoofed Host/Proto on the admin listener must not turn the admin into another origin: the CSRF check refuses.
        using var refused = await harness.Admin.SendAsync(
            HttpMethod.Post,
            "/api/v1/auth/logout",
            cookie: harness.OwnerCookie,
            headers: new Dictionary<string, string>
            {
                ["Origin"] = "https://evil.test",
                ["X-Forwarded-Host"] = "evil.test",
                ["X-Forwarded-Proto"] = "https",
            },
            browser: false);
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
    }

    private static async Task<(int Status, string Headers, string Body)> InternalAsync(
        AdminInternalListener listener,
        string method,
        string path,
        string? secret,
        string? xff,
        string? proto)
    {
        using var socket = listener.UsesUnixSocket
            ? new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
            : new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        EndPoint endpoint = listener.UsesUnixSocket
            ? new UnixDomainSocketEndPoint(listener.SocketPath!)
            : listener.LoopbackEndPoint!;
        await socket.ConnectAsync(endpoint, Ct).ConfigureAwait(false);
        await using NetworkStream stream = new(socket, ownsSocket: true);
        StringBuilder request = new();
        request.Append($"{method} {path} HTTP/1.1\r\nHost: {AdminProxyHarness.AdminHost}\r\nConnection: close\r\n");
        request.Append($"X-Forwarded-Host: {AdminProxyHarness.AdminHost}\r\n");
        if (secret is not null)
        {
            request.Append($"{AdminInternalListener.HeaderName}: {secret}\r\n");
        }

        if (proto is not null)
        {
            request.Append($"X-Forwarded-Proto: {proto}\r\n");
        }

        if (xff is not null)
        {
            request.Append($"X-Forwarded-For: {xff}\r\n");
        }

        request.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request.ToString()), Ct).ConfigureAwait(false);
        using StreamReader reader = new(stream, Encoding.ASCII);
        var response = await reader.ReadToEndAsync(Ct).ConfigureAwait(false);
        var split = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var headers = split < 0 ? response : response[..split];
        var body = split < 0 ? string.Empty : response[(split + 4)..];
        var status = int.Parse(headers.Split("\r\n", 2)[0].Split(' ')[1], CultureInfo.InvariantCulture);
        return (status, headers, body);
    }

    private static async Task<string> UpgradeAsync(AdminProxyHarness harness, string path, string key, string? cookie = null)
    {
        var endpoint = TestHost.Endpoint(harness.Host, HostRole.Public, "https");
        using TcpClient tcp = new();
        await tcp.ConnectAsync(IPAddress.Loopback, endpoint.Port, Ct);
        await using SslStream tls = new(tcp.GetStream());
        await tls.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost = AdminProxyHarness.AdminHost,
                ApplicationProtocols = [SslApplicationProtocol.Http11],
                RemoteCertificateValidationCallback = static (_, certificate, _, _) => certificate is not null,
            },
            Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var request = $"GET {path} HTTP/1.1\r\nHost: {AdminProxyHarness.AdminHost}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                      + $"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\nOrigin: https://{AdminProxyHarness.AdminHost}\r\n"
                      + (cookie is null ? string.Empty : $"Cookie: {cookie}\r\n")
                      + "\r\n";
        await tls.WriteAsync(Encoding.ASCII.GetBytes(request), Ct);
        using StreamReader reader = new(tls, Encoding.ASCII);
        StringBuilder head = new();
        while (true)
        {
            var line = await reader.ReadLineAsync(Ct);
            if (line is null || line.Length == 0)
            {
                break;
            }

            head.Append(line).Append("\r\n");
        }

        return head.ToString();
    }
}

/// <summary>
/// A test-only endpoint on the admin app: accepts a WebSocket upgrade at <c>/ws-echo</c> and closes it. It is registered after
/// the admin middleware, so the upgrade passes the forwarded-header hop check, the source allowlist and the hop secret, not
/// just YARP. The Blazor dashboard's circuit hub is the same case.
/// </summary>
internal sealed class AdminWebSocketEcho : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        next(app);
        app.UseWebSockets();
        app.Use(async (context, nextMiddleware) =>
        {
            if (!context.Request.Path.Equals("/ws-echo", StringComparison.Ordinal))
            {
                await nextMiddleware(context);
                return;
            }

            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "ok", context.RequestAborted);
        });
    };
}
