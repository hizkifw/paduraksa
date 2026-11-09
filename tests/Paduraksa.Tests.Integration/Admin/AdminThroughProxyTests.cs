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
using Microsoft.AspNetCore.WebSockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Paduraksa.Contracts.Config;
using Paduraksa.Core.Hosting;
using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Hosting;
using Paduraksa.Tests.Shared;

namespace Paduraksa.Tests.Integration.Admin;

/// <summary>
/// PLAN §3.1 end to end: an admin host on the public HTTPS listener serves the real admin app through its internal listener,
/// with the public certificate, the public origin's CSRF, the real client IP and the session cookie; and the lock-out guard
/// refuses to cut the caller off from it unless the request says so.
/// </summary>
public sealed class AdminThroughProxyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheAdminHostServesACookieSessionThroughThePublicProxy()
    {
        await using AdminProxyHarness harness = await AdminProxyHarness.StartAsync();

        using HttpResponseMessage me = await harness.SendAsync(
            HttpMethod.Get,
            "/api/v1/auth/me",
            cookie: harness.SessionHeader,
            origin: "https://" + AdminProxyHarness.AdminHost,
            headers: new Dictionary<string, string> { [AdminInternalListener.HeaderName] = "forged-by-the-client" });
        Assert.True(me.StatusCode == HttpStatusCode.OK, $"{(int)me.StatusCode}: {await me.Content.ReadAsStringAsync(Ct)}");

        using JsonDocument json = JsonDocument.Parse(await me.Content.ReadAsStringAsync(Ct));
        Assert.Equal(AdminHarness.OwnerName, json.RootElement.GetProperty("user").GetProperty("username").GetString());
        Assert.Equal("session", json.RootElement.GetProperty("authKind").GetString());

        // The admin's own response reaches the browser: the sign-in that started this harness set the session cookie.
        Assert.NotNull(harness.AdminCookie);
    }

    [Fact]
    public async Task CsrfUsesThePublicOriginAndRefusesAForeignOne()
    {
        await using AdminProxyHarness harness = await AdminProxyHarness.StartAsync();

        // Origin only (no Sec-Fetch-Site): it must equal the scheme/host/port the public client used.
        using HttpResponseMessage foreign = await harness.SendAsync(
            HttpMethod.Post, "/api/v1/auth/logout", cookie: harness.SessionHeader, origin: "https://evil.test");
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        using (JsonDocument problem = JsonDocument.Parse(await foreign.Content.ReadAsStringAsync(Ct)))
        {
            Assert.Equal("csrf.refused", problem.RootElement.GetProperty("code").GetString());
        }

        using HttpResponseMessage sameOrigin = await harness.SendAsync(
            HttpMethod.Post, "/api/v1/auth/logout", cookie: harness.SessionHeader, origin: "https://" + AdminProxyHarness.AdminHost);
        Assert.Equal(HttpStatusCode.NoContent, sameOrigin.StatusCode);
    }

    [Fact]
    public async Task TheSessionCookieReachesTheAdminAndNoOtherUpstreamAndCookieTossingIsStripped()
    {
        (IHost upstream, string address) = await HeaderEchoUpstreamHost.StartAsync(
            static response => response.Headers["Set-Cookie"] = "__Host-paduraksa-session=tossed; Path=/; Secure",
            Ct);
        using IHost upstreamHost = upstream;
        await using AdminProxyHarness harness = await AdminProxyHarness.StartAsync(upstreamAddress: address);

        // The upstream sees every cookie but the admin session cookie, and its own Set-Cookie for that name is removed. The
        // internal hop header a client sends is removed too; it never reaches an upstream.
        using HttpResponseMessage other = await harness.SendAsync(
            HttpMethod.Get,
            "/",
            host: AdminProxyHarness.OtherHost,
            headers: new Dictionary<string, string>
            {
                ["Cookie"] = "__Host-paduraksa-session=live; theme=dark",
                [AdminInternalListener.HeaderName] = "forged-by-the-client",
            });
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        Dictionary<string, string> seen = HeaderEchoUpstreamHost.Parse(await other.Content.ReadAsStringAsync(Ct));
        Assert.Equal("theme=dark", seen["Cookie"]);
        Assert.DoesNotContain("live", string.Join('|', seen.Values), StringComparison.Ordinal);
        Assert.False(seen.ContainsKey(AdminInternalListener.HeaderName));
        Assert.False(other.Headers.TryGetValues("Set-Cookie", out _));

        // The admin route carries the cookie through.
        using HttpResponseMessage admin = await harness.SendAsync(
            HttpMethod.Get, "/api/v1/auth/me", cookie: harness.SessionHeader, origin: "https://" + AdminProxyHarness.AdminHost);
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
    }

    [Fact]
    public async Task TheThrottleSeesTheForwardedClientSoTwoClientsAreSeparate()
    {
        await using AdminProxyHarness harness = await AdminProxyHarness.StartAsync(
            settings: new Dictionary<string, string?>
            {
                ["Paduraksa:Admin:RateLimit:PermitsPerIp"] = "1",
                ["Paduraksa:Admin:RateLimit:PermitsPerUser"] = "100",
            },
            trustedProxies: new TrustedProxySettings(TrustedProxyMode.Generic, ["127.0.0.1/32"], TrustForwardedProto: true));

        string body = Body.Login(AdminHarness.OwnerName, AdminHarness.Password);
        using HttpResponseMessage first = await harness.SendAsync(
            HttpMethod.Post, "/api/v1/auth/login", body, origin: "https://" + AdminProxyHarness.AdminHost,
            headers: new Dictionary<string, string> { ["X-Forwarded-For"] = "203.0.113.1" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using HttpResponseMessage sameClient = await harness.SendAsync(
            HttpMethod.Post, "/api/v1/auth/login", body, origin: "https://" + AdminProxyHarness.AdminHost,
            headers: new Dictionary<string, string> { ["X-Forwarded-For"] = "203.0.113.1" });
        Assert.Equal(HttpStatusCode.TooManyRequests, sameClient.StatusCode);

        using HttpResponseMessage otherClient = await harness.SendAsync(
            HttpMethod.Post, "/api/v1/auth/login", body, origin: "https://" + AdminProxyHarness.AdminHost,
            headers: new Dictionary<string, string> { ["X-Forwarded-For"] = "203.0.113.2" });
        Assert.Equal(HttpStatusCode.OK, otherClient.StatusCode);
    }

    [Fact]
    public async Task ClientSentForwardedHeadersAreOverwritten()
    {
        await using AdminProxyHarness harness = await AdminProxyHarness.StartAsync();

        // If the admin trusted the client's spoofed proto/host, this Origin wouldn't match; it matches the public request.
        using HttpResponseMessage accepted = await harness.SendAsync(
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
        using HttpResponseMessage refused = await harness.SendAsync(
            HttpMethod.Post,
            "/api/v1/auth/logout",
            cookie: harness.SessionHeader,
            origin: "https://evil.test");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        // The client IP the admin recorded is the public listener's peer, not the spoofed address.
        IReadOnlyList<AuditRecord> logins = await harness.AuditAsync("auth.login");
        Assert.NotEmpty(logins);
        Assert.All(logins, entry => Assert.Equal("127.0.0.1", entry.ClientIp));
    }

    [Fact]
    public async Task TheInternalSocketIsPrivateAndReachableOnlyThroughTheAdminHost()
    {
        await using AdminProxyHarness harness = await AdminProxyHarness.StartAsync();

        AdminInternalListener internalListener = harness.Host.Shared.AdminInternal;
        Assert.True(internalListener.Enabled);
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(internalListener.UsesUnixSocket, "this platform should bind the Unix socket");
            string socket = internalListener.SocketPath!;
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(socket));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.GetDirectoryName(socket)!));
        }

        // Only the admin host's name reaches it: an unknown name is the default 404, not the admin app's problem.
        using HttpResponseMessage unknown = await harness.SendAsync(
            HttpMethod.Get, "/api/v1/auth/me", host: "not-the-admin.test", origin: "https://not-the-admin.test");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        // And the configured admin host does reach it (an anonymous call is the admin API's own 401 problem).
        using HttpResponseMessage admin = await harness.SendAsync(
            HttpMethod.Get, "/api/v1/auth/me", origin: "https://" + AdminProxyHarness.AdminHost);
        Assert.Equal(HttpStatusCode.Unauthorized, admin.StatusCode);
        using JsonDocument problem = JsonDocument.Parse(await admin.Content.ReadAsStringAsync(Ct));
        Assert.Equal("auth.required", problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ARequestWithoutTheInternalHopSecretIsRefused()
    {
        await using AdminProxyHarness harness = await AdminProxyHarness.StartAsync();
        AdminInternalListener listener = harness.Host.Shared.AdminInternal;

        (int status, string headers, string body) = await InternalAsync(
            listener, "GET", "/healthz", secret: null, xff: "203.0.113.9", proto: "https");

        Assert.Equal(HttpStatusCode.Forbidden, (HttpStatusCode)status);
        Assert.Contains("\"code\":\"admin.internalHop\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Strict-Transport-Security", headers, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheAllowlistAppliesPerRequestToTheForwardedClient()
    {
        await using AdminProxyHarness harness = await AdminProxyHarness.StartAsync(
            settings: new Dictionary<string, string?>
            {
                ["Paduraksa:Listen:AdminAllowedSources:0"] = "127.0.0.1/32",
            },
            trustedProxies: new TrustedProxySettings(TrustedProxyMode.Generic, ["127.0.0.1/32"], TrustForwardedProto: true));

        using HttpResponseMessage denied = await harness.SendAsync(
            HttpMethod.Get,
            "/healthz",
            headers: new Dictionary<string, string> { ["X-Forwarded-For"] = "203.0.113.9" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (JsonDocument problem = JsonDocument.Parse(await denied.Content.ReadAsStringAsync(Ct)))
        {
            Assert.Equal("admin.sourceDenied", problem.RootElement.GetProperty("code").GetString());
        }

        // A missing X-Forwarded-For is unknown, not loopback: the same restricted list refuses it.
        (int status, _, string body) = await InternalAsync(
            harness.Host.Shared.AdminInternal, "GET", "/healthz", secret: harness.Host.Shared.AdminInternal.Secret, xff: null, proto: "https");
        Assert.Equal(HttpStatusCode.Forbidden, (HttpStatusCode)status);
        Assert.Contains("\"code\":\"admin.sourceDenied\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HstsFollowsTheForwardedScheme()
    {
        await using AdminProxyHarness harness = await AdminProxyHarness.StartAsync();
        AdminInternalListener listener = harness.Host.Shared.AdminInternal;

        (int httpsStatus, string httpsHeaders, _) = await InternalAsync(
            listener, "GET", "/healthz", listener.Secret, xff: "127.0.0.1", proto: "https");
        Assert.Equal(HttpStatusCode.OK, (HttpStatusCode)httpsStatus);
        Assert.Contains("Strict-Transport-Security", httpsHeaders, StringComparison.OrdinalIgnoreCase);

        (int httpStatus, string httpHeaders, _) = await InternalAsync(
            listener, "GET", "/healthz", listener.Secret, xff: "127.0.0.1", proto: "http");
        Assert.Equal(HttpStatusCode.OK, (HttpStatusCode)httpStatus);
        Assert.DoesNotContain("Strict-Transport-Security", httpHeaders, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheAdminHostsOwnHstsSettingReplacesTheApps()
    {
        // The admin app sets its own header on the internal listener; the host's resolved HSTS, when it has one, is the
        // one the browser sees (PLAN §3.1, §14 M6).
        await using AdminProxyHarness plain = await AdminProxyHarness.StartAsync();
        using (HttpResponseMessage untouched = await plain.SendAsync(HttpMethod.Get, "/api/v1/auth/me", cookie: plain.SessionHeader))
        {
            Assert.Equal(HttpStatusCode.OK, untouched.StatusCode);
            Assert.Equal("max-age=31536000", Assert.Single(untouched.Headers.GetValues("Strict-Transport-Security")));
        }

        await using AdminProxyHarness configured = await AdminProxyHarness.StartAsync(
            adminHostSettings: new HostSettings { Hsts = new HstsSettings { Enabled = true, MaxAge = 600 } });
        using HttpResponseMessage ours = await configured.SendAsync(HttpMethod.Get, "/api/v1/auth/me", cookie: configured.SessionHeader);
        Assert.Equal(HttpStatusCode.OK, ours.StatusCode);
        Assert.Equal("max-age=600", Assert.Single(ours.Headers.GetValues("Strict-Transport-Security")));
    }

    [Fact]
    public async Task SetupIsRefusedThroughTheInternalListener()
    {
        await using AdminProxyHarness harness = await AdminProxyHarness.StartAsync();

        using HttpResponseMessage get = await harness.SendAsync(
            HttpMethod.Get, "/api/v1/setup", origin: "https://" + AdminProxyHarness.AdminHost);
        Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);
        using JsonDocument problem = JsonDocument.Parse(await get.Content.ReadAsStringAsync(Ct));
        Assert.Equal("setup.viaProxy", problem.RootElement.GetProperty("code").GetString());

        using HttpResponseMessage post = await harness.SendAsync(
            HttpMethod.Post,
            "/api/v1/setup",
            """{"setupToken":"x","username":"x","password":"x"}""",
            origin: "https://" + AdminProxyHarness.AdminHost);
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        using JsonDocument postProblem = JsonDocument.Parse(await post.Content.ReadAsStringAsync(Ct));
        Assert.Equal("setup.viaProxy", postProblem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task TheTcpFallbackWorksEndToEndAndStillRequiresTheHopSecret()
    {
        string dataDirectory = Path.Combine(TestDataDirectories.NewPath(), new string('d', 120), "data");
        LogCapture logs = new();
        await using AdminProxyHarness harness = await AdminProxyHarness.StartAsync(
            settings: new Dictionary<string, string?> { ["Paduraksa:DataDirectory"] = dataDirectory },
            logs: logs);

        AdminInternalListener listener = harness.Host.Shared.AdminInternal;
        Assert.False(listener.UsesUnixSocket);
        Assert.NotNull(listener.LoopbackEndPoint);
        Assert.NotNull(listener.FallbackReason);
        Assert.Contains(logs.Entries, entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning
            && entry.Message.Contains("TCP fallback", StringComparison.Ordinal));

        // The proxied path works over the fallback.
        using HttpResponseMessage me = await harness.SendAsync(
            HttpMethod.Get, "/api/v1/auth/me", cookie: harness.SessionHeader, origin: "https://" + AdminProxyHarness.AdminHost);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        // A local process on the fallback port can't forge forwarded headers without the secret.
        (int status, _, string body) = await InternalAsync(
            listener, "GET", "/healthz", secret: null, xff: "203.0.113.9", proto: "https");
        Assert.Equal(HttpStatusCode.Forbidden, (HttpStatusCode)status);
        Assert.Contains("\"code\":\"admin.internalHop\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWebSocketUpgradePassesThroughTheAdminPipeline()
    {
        await using AdminProxyHarness harness = await AdminProxyHarness.StartAsync(
            configureServices: static (role, services) =>
            {
                if (role == HostRole.Admin)
                {
                    services.AddSingleton<IStartupFilter, AdminWebSocketEcho>();
                }
            });

        string key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        string response = await UpgradeAsync(harness, "/ws-echo", key);
        Assert.StartsWith("HTTP/1.1 101", response, StringComparison.Ordinal);
        Assert.Contains("Sec-WebSocket-Accept:", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheDashboardAndItsCircuitWorkThroughTheProxy()
    {
        await using AdminProxyHarness harness = await AdminProxyHarness.StartAsync();

        // The page, as a browser on the public name opens it: the dashboard's policy, and HSTS for the public HTTPS name.
        using (HttpResponseMessage page = await harness.SendAsync(
            HttpMethod.Get, "/", cookie: harness.SessionHeader, headers: new Dictionary<string, string> { ["Accept"] = "text/html" }))
        {
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Equal(Paduraksa.Dashboard.DashboardSecurity.ContentSecurityPolicy, string.Join(",", page.Headers.GetValues("Content-Security-Policy")));
            Assert.True(page.Headers.Contains("Strict-Transport-Security"));
        }

        // The circuit: negotiate, then the WebSocket upgrade, both through the public listener and the hop-checked internal one.
        string connectionToken;
        using (HttpResponseMessage negotiate = await harness.SendAsync(
            HttpMethod.Post, "/_blazor/negotiate?negotiateVersion=1", cookie: harness.SessionHeader, fetchSite: "same-origin"))
        {
            Assert.Equal(HttpStatusCode.OK, negotiate.StatusCode);
            using JsonDocument body = JsonDocument.Parse(await negotiate.Content.ReadAsStringAsync(Ct));
            connectionToken = body.RootElement.GetProperty("connectionToken").GetString()!;
        }

        string key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        string upgraded = await UpgradeAsync(harness, $"/_blazor?id={Uri.EscapeDataString(connectionToken)}", key, harness.SessionHeader);
        Assert.StartsWith("HTTP/1.1 101", upgraded, StringComparison.Ordinal);

        // Without the session the hub refuses the upgrade.
        string refused = await UpgradeAsync(harness, $"/_blazor?id={Uri.EscapeDataString(connectionToken)}", key);
        Assert.StartsWith("HTTP/1.1 401", refused, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSetupPageIsNotServedThroughTheProxy()
    {
        await using AdminProxyHarness harness = await AdminProxyHarness.StartAsync();

        foreach ((HttpMethod method, string path) in new[] { (HttpMethod.Get, "/setup"), (HttpMethod.Post, "/auth/setup") })
        {
            using HttpResponseMessage response = await harness.SendAsync(method, path, fetchSite: "same-origin");
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Contains("\"code\":\"setup.viaProxy\"", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TheLockoutGuardRefusesThroughTheProxyAndForceAllows()
    {
        await using AdminProxyHarness harness = await AdminProxyHarness.StartAsync();
        string path = $"/api/v1/hosts/{AdminProxyHarness.AdminHostId}";

        using HttpResponseMessage refused = await harness.SendAsync(
            HttpMethod.Delete, path, cookie: harness.SessionHeader, origin: "https://" + AdminProxyHarness.AdminHost,
            headers: new Dictionary<string, string> { ["If-Match"] = "\"1\"" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        using (JsonDocument problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync(Ct)))
        {
            Assert.Contains(
                problem.RootElement.GetProperty("errors").EnumerateArray(),
                error => error.GetProperty("code").GetString() == "host.admin.lockout");
        }

        using HttpResponseMessage forced = await harness.SendAsync(
            HttpMethod.Delete, path + "?force=true", cookie: harness.SessionHeader, origin: "https://" + AdminProxyHarness.AdminHost,
            headers: new Dictionary<string, string> { ["If-Match"] = "\"1\"" });
        Assert.Equal(HttpStatusCode.NoContent, forced.StatusCode);
    }

    [Fact]
    public async Task TheAdminHostCanBeDeletedOverTheNormalAdminPort()
    {
        await using AdminProxyHarness harness = await AdminProxyHarness.StartAsync();

        // The ordinary admin port is not the admin host, so the guard doesn't apply there.
        using AdminResponse deleted = await harness.Admin.SendAsync(
            HttpMethod.Delete,
            $"/api/v1/hosts/{AdminProxyHarness.AdminHostId}",
            cookie: harness.OwnerCookie,
            headers: new Dictionary<string, string> { ["If-Match"] = "\"1\"" });
        Assert.Equal(HttpStatusCode.NoContent, deleted.Status);
    }

    [Fact]
    public async Task ClientSentForwardedHeadersAreIgnoredOnTheAdminPort()
    {
        await using AdminProxyHarness harness = await AdminProxyHarness.StartAsync();

        // A spoofed Host/Proto on the admin listener must not turn the admin into another origin: the CSRF check refuses.
        using AdminResponse refused = await harness.Admin.SendAsync(
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
        using Socket socket = listener.UsesUnixSocket
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
        string response = await reader.ReadToEndAsync(Ct).ConfigureAwait(false);
        int split = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        string headers = split < 0 ? response : response[..split];
        string body = split < 0 ? string.Empty : response[(split + 4)..];
        int status = int.Parse(headers.Split("\r\n", 2)[0].Split(' ')[1], CultureInfo.InvariantCulture);
        return (status, headers, body);
    }

    private static async Task<string> UpgradeAsync(AdminProxyHarness harness, string path, string key, string? cookie = null)
    {
        Uri endpoint = TestHost.Endpoint(harness.Host, HostRole.Public, "https");
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
        string request = $"GET {path} HTTP/1.1\r\nHost: {AdminProxyHarness.AdminHost}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
            + $"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\nOrigin: https://{AdminProxyHarness.AdminHost}\r\n"
            + (cookie is null ? string.Empty : $"Cookie: {cookie}\r\n")
            + "\r\n";
        await tls.WriteAsync(Encoding.ASCII.GetBytes(request), Ct);
        using StreamReader reader = new(tls, Encoding.ASCII);
        StringBuilder head = new();
        while (true)
        {
            string? line = await reader.ReadLineAsync(Ct);
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
/// the admin middleware, so the upgrade passes the forwarded-header hop check, the source allowlist and the new hop secret, not
/// just YARP (PLAN §3.1). The Blazor dashboard needs the same path in M5.
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

            using System.Net.WebSockets.WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
            await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "ok", context.RequestAborted);
        });
    };
}
