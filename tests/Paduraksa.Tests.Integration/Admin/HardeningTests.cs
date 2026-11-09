using System.Globalization;
using System.Net;
using System.Text;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using Paduraksa.Admin.Auth;
using Paduraksa.Application.Certificates;
using Paduraksa.Application.Commands;
using Paduraksa.Core.Model;
using Paduraksa.Server.Configuration;
using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Hosting;

namespace Paduraksa.Tests.Integration.Admin;

/// <summary>Security headers, the request body limit, the source allowlist, the loopback fallback listener, and the caller contract.</summary>
public sealed class HardeningTests(SharedOwnerSession shared, SharedSmallBodyHost smallBody)
    : IClassFixture<SharedOwnerSession>, IClassFixture<SharedSmallBodyHost>
{
    [Fact]
    public async Task EveryAdminResponseCarriesTheSecurityHeaders()
    {
        AdminHarness h = shared.Harness;

        foreach (string path in new[] { "/api/v1/auth/me", "/api/v1/setup", "/healthz", "/", "/api/v1/missing" })
        {
            // Reached by name, as a LAN client would: HSTS applies. By a loopback name it does not (see below).
            using AdminResponse response = await h.SendAsync(HttpMethod.Get, path, headers: new Dictionary<string, string> { ["Host"] = "admin.example.test" });

            AssertSecurityHeaders(response, path.StartsWith("/api/", StringComparison.Ordinal));
            Assert.StartsWith("max-age=", response.Header("Strict-Transport-Security"), StringComparison.Ordinal);
        }

        foreach (string host in new[] { "localhost:9443", "127.0.0.1:9443", "[::1]:9443" })
        {
            using AdminResponse loopback = await h.SendAsync(HttpMethod.Get, "/healthz", headers: new Dictionary<string, string> { ["Host"] = host });
            Assert.Null(loopback.Header("Strict-Transport-Security"));
        }
    }

    private static void AssertSecurityHeaders(AdminResponse response, bool api)
    {
        Assert.Equal("nosniff", response.Header("X-Content-Type-Options"));
        Assert.Equal("no-referrer", response.Header("Referrer-Policy"));
        Assert.Equal("DENY", response.Header("X-Frame-Options"));
        Assert.Equal("default-src 'none'; frame-ancestors 'none'", response.Header("Content-Security-Policy"));
        if (api)
        {
            Assert.Equal("no-store", response.Header("Cache-Control"));
        }
    }

    [Fact]
    public async Task TheShortCircuitResponsesCarryTheSecurityHeadersToo()
    {
        await using AdminHarness gated = await AdminHarness.StartAsync(new Dictionary<string, string?> { ["Paduraksa:Admin:RateLimit:PermitsPerIp"] = "2" });
        using (AdminResponse closed = await gated.GetAsync("/api/v1/users"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, closed.Status);
            AssertSecurityHeaders(closed, api: true);
        }

        for (int i = 0; i < 2; i++)
        {
            using AdminResponse wrong = await gated.PostAsync("/api/v1/setup", Body.Setup("wrong", "user" + i, AdminHarness.Password));
            Assert.Equal(HttpStatusCode.Forbidden, wrong.Status);
        }

        using (AdminResponse limited = await gated.PostAsync("/api/v1/setup", Body.Setup("wrong", "user3", AdminHarness.Password)))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.Status);
            AssertSecurityHeaders(limited, api: true);
        }

        await using AdminHarness open = await AdminHarness.StartAsync();
        string cookie = await open.SetUpOwnerAsync();
        using (AdminResponse unauthorized = await open.GetAsync("/api/v1/auth/me"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.Status);
            Assert.Equal("Bearer", unauthorized.Header("WWW-Authenticate"));
            AssertSecurityHeaders(unauthorized, api: true);
        }

        using (AdminResponse crossSite = await open.SendAsync(HttpMethod.Post, "/api/v1/me/password", "{}", cookie, headers: new Dictionary<string, string> { ["Sec-Fetch-Site"] = "cross-site" }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, crossSite.Status);
            AssertSecurityHeaders(crossSite, api: true);
        }

        using (AdminResponse requireAuth = await open.GetAsync("/api/v1/users"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, requireAuth.Status); // the permission requirement's 401 has the same shape and challenge
            Assert.Equal("Bearer", requireAuth.Header("WWW-Authenticate"));
            Assert.Equal("auth.required", requireAuth.Json.GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task ARequestBodyOverTheLimitIsRefusedBeforeItIsRead()
    {
        AdminHarness h = smallBody.Harness;

        using AdminResponse big = await h.PostAsync("/api/v1/auth/login", Body.Login("owner", new string('x', 4096)));
        using AdminResponse fine = await h.PostAsync("/api/v1/auth/login", Body.Login("owner", AdminHarness.Password));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, big.Status);
        Assert.Equal(HttpStatusCode.OK, fine.Status);
    }

    [Fact]
    public async Task ABodyOverTheLimitIsRefusedWhateverItsLengthIsDeclared_OverHttp1AndHttp2()
    {
        AdminHarness h = smallBody.Harness;
        string big = Body.Login("owner", new string('x', 4096));

        foreach (Version version in new[] { HttpVersion.Version11, HttpVersion.Version20 })
        {
            using AdminResponse declared = await h.SendAsync(HttpMethod.Post, "/api/v1/auth/login", big, version: version);
            using AdminResponse streamed = await h.SendAsync(HttpMethod.Post, "/api/v1/auth/login", big, version: version, streamed: true);
            using AdminResponse fine = await h.SendAsync(HttpMethod.Post, "/api/v1/auth/login", Body.Login("owner", AdminHarness.Password), version: version, streamed: true);

            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, declared.Status);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, streamed.Status);
            Assert.Equal(HttpStatusCode.OK, fine.Status);
        }
    }

    [Fact]
    public async Task UnknownPropertiesAndMissingMembersInABodyAreRefused()
    {
        AdminHarness h = shared.Harness;

        using AdminResponse unknown = await h.PostAsync("/api/v1/auth/login", """{"username":"owner","password":"x","admin":true}""");
        using AdminResponse missing = await h.PostAsync("/api/v1/auth/login", """{"username":"owner"}""");
        using AdminResponse notJson = await h.PostAsync("/api/v1/auth/login", "nope");
        using AdminResponse nulls = await h.PostAsync("/api/v1/auth/login", """{"username":"owner","password":null}""");

        Assert.Equal(HttpStatusCode.BadRequest, unknown.Status);
        Assert.Equal(HttpStatusCode.BadRequest, missing.Status);
        Assert.Equal(HttpStatusCode.BadRequest, notJson.Status);
        Assert.Equal(HttpStatusCode.BadRequest, nulls.Status);
    }

    [Fact]
    public async Task AClientOutsideTheAllowlistIsRefusedAtTheConnection_AndLoopbackIsNotImplied()
    {
        // The test client connects from 127.0.0.1, which these blocks do not include.
        await using AdminHarness outside = await AdminHarness.StartAsync(new Dictionary<string, string?>
        {
            ["Paduraksa:Listen:AdminAllowedSources:0"] = "203.0.113.0/24",
            ["Paduraksa:Listen:AdminAllowedSources:1"] = "2001:db8::/32",
        });
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => outside.GetAsync("/healthz"));

        await using AdminHarness listed = await AdminHarness.StartAsync(new Dictionary<string, string?>
        {
            ["Paduraksa:Listen:AdminAllowedSources:0"] = "203.0.113.0/24",
            ["Paduraksa:Listen:AdminAllowedSources:1"] = "127.0.0.1/32",
        });
        using AdminResponse response = await listed.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, response.Status);
    }

    [Fact]
    public async Task TheLoopbackListenerIsNotSubjectToTheAllowlist()
    {
        (AdminHarness started, int port) = await StartWithLoopbackListenerAsync(new Dictionary<string, string?>
        {
            ["Paduraksa:Listen:AdminAllowedSources:0"] = "203.0.113.0/24",
        });
        await using AdminHarness h = started;
        using HttpClient http = new(new SocketsHttpHandler { UseCookies = false }) { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        using HttpResponseMessage response = await http.GetAsync("/healthz", AdminHarness.Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TheLoopbackListenerAnswersOnlyToALoopbackHostName()
    {
        (AdminHarness started, int port) = await StartWithLoopbackListenerAsync();
        await using AdminHarness h = started;
        using HttpClient http = new(new SocketsHttpHandler { UseCookies = false }) { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        foreach ((string host, HttpStatusCode expected) in new[]
        {
            ($"localhost:{port}", HttpStatusCode.OK),
            ($"127.0.0.1:{port}", HttpStatusCode.OK),
            ($"[::1]:{port}", HttpStatusCode.OK),
            ($"LOCALHOST:{port}", HttpStatusCode.OK),
            ($"evil.example:{port}", HttpStatusCode.BadRequest), // DNS rebinding: a page's own name pointed at 127.0.0.1
            ($"localhost.evil.example:{port}", HttpStatusCode.BadRequest),
            ("127.0.0.1", HttpStatusCode.BadRequest), // no port is port 80
            ($"127.0.0.1:{port + 1}", HttpStatusCode.BadRequest),
        })
        {
            using HttpRequestMessage request = new(HttpMethod.Get, "/healthz");
            request.Headers.Host = host;
            using HttpResponseMessage response = await http.SendAsync(request, AdminHarness.Ct);
            Assert.True(expected == response.StatusCode, $"{host}: {response.StatusCode}");
        }

        // Only that listener: the HTTPS one serves any host name (a LAN name, an IP).
        using AdminResponse https = await h.SendAsync(HttpMethod.Get, "/healthz", headers: new Dictionary<string, string> { ["Host"] = "evil.example" });
        Assert.Equal(HttpStatusCode.OK, https.Status);
    }

    [Fact]
    public async Task TheLoopbackFallbackListenerServesTheSameAppOverPlainHttp()
    {
        (AdminHarness started, int port) = await StartWithLoopbackListenerAsync();
        await using AdminHarness h = started;
        string token = await h.SetupTokenAsync();
        using HttpClient http = new(new SocketsHttpHandler { UseCookies = false }) { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        // The listener is bound to loopback only.
        IReadOnlyList<string> addresses = h.Host.GetListenAddresses(HostRole.Admin);
        Assert.Contains(addresses, a => a.StartsWith("https://", StringComparison.Ordinal));
        Assert.All(addresses.Where(a => a.StartsWith("http://", StringComparison.Ordinal)), a =>
            Assert.Matches(@"^http://(localhost|127\.0\.0\.1|\[::1\]):" + port + "$", a));

        using (HttpResponseMessage health = await http.GetAsync("/healthz", AdminHarness.Ct))
        {
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            Assert.False(health.Headers.Contains("Strict-Transport-Security")); // HSTS is for HTTPS responses
            Assert.Equal("nosniff", health.Headers.GetValues("X-Content-Type-Options").Single());
        }

        // Setup works over it, and the cookie is still Secure (Chromium and Firefox accept that for loopback http).
        using StringContent setup = new(Body.Setup(token, "owner", AdminHarness.Password), Encoding.UTF8, "application/json");
        using HttpResponseMessage created = await http.PostAsync("/api/v1/setup", setup, AdminHarness.Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        string line = created.Headers.GetValues("Set-Cookie").Single();
        Assert.StartsWith("__Host-paduraksa-session=", line, StringComparison.Ordinal);
        Assert.Contains("secure", line, StringComparison.OrdinalIgnoreCase);
        string cookie = line.Split(';')[0];

        // The same-origin check uses the request's own scheme: http://127.0.0.1:<port> is its own origin there.
        using HttpRequestMessage me = new(HttpMethod.Get, "/api/v1/auth/me");
        me.Headers.TryAddWithoutValidation("Cookie", cookie);
        using HttpResponseMessage meResponse = await http.SendAsync(me, AdminHarness.Ct);
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);

        using HttpRequestMessage logout = new(HttpMethod.Post, "/api/v1/auth/logout");
        logout.Headers.TryAddWithoutValidation("Cookie", cookie);
        logout.Headers.TryAddWithoutValidation("Origin", $"http://127.0.0.1:{port}");
        using HttpResponseMessage logoutResponse = await http.SendAsync(logout, AdminHarness.Ct);
        Assert.Equal(HttpStatusCode.NoContent, logoutResponse.StatusCode);

        using HttpRequestMessage forged = new(HttpMethod.Post, "/api/v1/auth/logout");
        forged.Headers.TryAddWithoutValidation("Cookie", cookie);
        forged.Headers.TryAddWithoutValidation("Origin", $"https://127.0.0.1:{port}");
        using HttpResponseMessage forgedResponse = await http.SendAsync(forged, AdminHarness.Ct);
        Assert.Equal(HttpStatusCode.Forbidden, forgedResponse.StatusCode);
    }

    [Fact]
    public async Task AuthenticationNeverClearsOrReplacesACallerSetAheadOfIt()
    {
        UserIdHolder holder = new();
        await using AdminHarness h = await AdminHarness.StartAsync(configureServices: (role, services) =>
        {
            if (role == HostRole.Admin)
            {
                services.AddSingleton<IStartupFilter>(new PresetCallerFilter(holder));
            }
        });
        string cookie = await h.SetUpOwnerAsync();
        using (AdminResponse me = await h.GetAsync("/api/v1/auth/me", cookie))
        {
            holder.User = me.Json.GetProperty("user").GetProperty("id").GetString();
        }

        // No credentials at all, and a bad bearer token: the preset caller stands in both cases.
        using AdminResponse anonymous = await h.GetAsync("/api/v1/auth/me");
        using AdminResponse badBearer = await h.GetAsync("/api/v1/auth/me", bearer: "pdk_garbage");

        Assert.Equal(HttpStatusCode.OK, anonymous.Status);
        Assert.Equal(HttpStatusCode.OK, badBearer.Status);
        Assert.Equal("owner", anonymous.Json.GetProperty("user").GetProperty("username").GetString());
    }

    [Fact]
    public void TheValidatorBoundsTheNewSettings()
    {
        PaduraksaOptions options = new();
        options.Listen.AdminAllowedSources.Add("10.0.0.0/8");
        options.Listen.AdminAllowedSources.Add("fd00::1");
        Assert.True(new PaduraksaOptionsValidator().Validate(null, options).Succeeded);

        PaduraksaOptions bad = new()
        {
            Listen = { AdminLoopbackHttp = 70000 },
            Admin = { RateLimit = { PermitsPerIp = 0, PermitsPerUser = 5000, Window = TimeSpan.Zero }, MaxRequestBodyBytes = 10 },
        };
        bad.Listen.AdminAllowedSources.Add("not-a-cidr");
        bad.Listen.AdminAllowedSources.Add("10.0.0.0/99");

        string[] failures = [.. new PaduraksaOptionsValidator().Validate(null, bad).Failures!];
        Assert.Contains(failures, f => f.Contains("AdminLoopbackHttp", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.Contains("RateLimit:PermitsPerIp", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.Contains("RateLimit:PermitsPerUser", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.Contains("RateLimit:Window", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.Contains("MaxRequestBodyBytes", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.Contains("'not-a-cidr'", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.Contains("'10.0.0.0/99'", StringComparison.Ordinal));

        PaduraksaOptions mapped = new();
        mapped.Listen.AdminAllowedSources.Add("::ffff:10.0.0.0/104");
        Assert.Contains(new PaduraksaOptionsValidator().Validate(null, mapped).Failures!, f => f.Contains("IPv4", StringComparison.Ordinal));

        PaduraksaOptions longWindow = new() { Admin = { RateLimit = { Window = TimeSpan.FromHours(2) } } };
        Assert.Contains(new PaduraksaOptionsValidator().Validate(null, longWindow).Failures!, f => f.Contains("RateLimit:Window", StringComparison.Ordinal));

        PaduraksaOptions clash = new() { Listen = { AdminLoopbackHttp = 9443 } };
        Assert.Contains(new PaduraksaOptionsValidator().Validate(null, clash).Failures!, f => f.Contains("distinct", StringComparison.Ordinal));
    }

    [Fact]
    public void TheValidatorBoundsTheAdminCertificateSettings()
    {
        PaduraksaOptions valid = new();
        valid.Admin.CertificateNames.Add("admin.home.lan");
        valid.Admin.CertificateNames.Add("192.0.2.10");
        Assert.True(new PaduraksaOptionsValidator().Validate(null, valid).Succeeded);

        PaduraksaOptions bad = new();
        bad.Admin.CertificateNames.Add("*.home.lan");       // a wildcard: the admin certificate covers exact names
        bad.Admin.CertificateNames.Add("fe80::1%eth0");     // a zone/scope id is not a certificate name
        bad.Admin.CertificateNames.Add("   ");              // empty
        bad.Admin.CertificateNames.Add("not a name!");
        string[] failures = [.. new PaduraksaOptionsValidator().Validate(null, bad).Failures!];
        Assert.Contains(failures, f => f.Contains("'*.home.lan'", StringComparison.Ordinal) && f.Contains("wildcard", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.Contains("'fe80::1%eth0'", StringComparison.Ordinal) && f.Contains("zone", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.Contains("entry 2", StringComparison.Ordinal) && f.Contains("empty", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.Contains("'not a name!'", StringComparison.Ordinal));

        // One name more than the cap leaves no room for the process's own names, so it is refused rather than truncated.
        PaduraksaOptions tooMany = new();
        for (int i = 0; i <= AdminCertificateNames.MaxConfiguredNames; i++)
        {
            tooMany.Admin.CertificateNames.Add($"admin{i}.lan");
        }

        Assert.Contains(new PaduraksaOptionsValidator().Validate(null, tooMany).Failures!, f => f.Contains("at most 95", StringComparison.Ordinal));

        PaduraksaOptions impatient = new() { Admin = { CertificateStartupTimeout = TimeSpan.FromHours(1) } };
        Assert.Contains(new PaduraksaOptionsValidator().Validate(null, impatient).Failures!, f => f.Contains("CertificateStartupTimeout", StringComparison.Ordinal));
    }

    /// <summary>
    /// Starts a host with the optional loopback listener on a fixed port: that listener treats 0 as "off", so the OS cannot
    /// pick the port itself. <see cref="TestPorts.StartOnFreePortAsync"/> probes a port and retries the whole start with a new
    /// one when another test's host takes it between the probe and Kestrel's bind.
    /// </summary>
    private static async Task<(AdminHarness Harness, int Port)> StartWithLoopbackListenerAsync(Dictionary<string, string?>? settings = null)
    {
        (PaduraksaHost host, int port) = await TestPorts.StartOnFreePortAsync(port =>
        {
            Dictionary<string, string?> config = new(StringComparer.OrdinalIgnoreCase);
            foreach ((string key, string? value) in settings ?? [])
            {
                config[key] = value;
            }

            config["Paduraksa:Listen:AdminLoopbackHttp"] = port.ToString(CultureInfo.InvariantCulture);
            return TestHost.Create(settings: config);
        });

        return (AdminHarness.For(host), port);
    }

    private sealed class UserIdHolder
    {
        public string? User { get; set; }
    }

    /// <summary>Sets a caller for the Owner on every request, ahead of the admin pipeline, as the other branch's tests do.</summary>
    private sealed class PresetCallerFilter(UserIdHolder holder) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (holder.User is { } text && UserId.TryParse(text, out UserId id))
                {
                    context.SetCaller(new AdminCaller(new CommandOrigin(Actor.ForUser(id), Channel.Api, null)) { User = id });
                }

                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
