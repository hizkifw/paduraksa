using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using Paduraksa.Admin.Auth;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Identity;
using Paduraksa.Contracts.Api;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Hosting;

namespace Paduraksa.Tests.Integration.Admin;

/// <summary>The API's global conventions, through the real host over HTTPS: problem details everywhere, no leaks, JSON only, typed-ID routes, the description.</summary>
public sealed class ApiConventionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnUnknownRouteUnderApiIsAProblemNotAnEmptyBody()
    {
        await using AdminApiHarness api = await AdminApiHarness.StartAsync(Ct);

        using HttpResponseMessage response = await api.GetAsync("/api/v1/no-such-thing");
        ApiProblem problem = await AdminApiHarness.ProblemAsync(response, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(404, problem.Status);
        Assert.Equal("Not found", problem.Title);
        Assert.False(string.IsNullOrEmpty(problem.Trace));
    }

    [Fact]
    public async Task AWrongMethodIs405WithTheAllowHeaderAndAProblem()
    {
        await using AdminApiHarness api = await AdminApiHarness.StartAsync(Ct);

        using HttpResponseMessage response = await api.SendAsync(HttpMethod.Patch, "/api/v1/hosts");
        ApiProblem problem = await AdminApiHarness.ProblemAsync(response, Ct);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Contains("GET", response.Content.Headers.Allow);
        Assert.Equal(405, problem.Status);
    }

    [Fact]
    public async Task AMalformedTypedIdInTheRouteDoesNotMatch()
    {
        await using AdminApiHarness api = await AdminApiHarness.StartAsync(Ct);

        foreach (string path in new[] { "/api/v1/hosts/not-an-id", "/api/v1/hosts/upstream_0000000000000000000000", "/api/v1/revisions/host_0000000000000000000000" })
        {
            using HttpResponseMessage response = await api.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(404, (await AdminApiHarness.ProblemAsync(response, Ct)).Status);
        }
    }

    [Fact]
    public async Task AnExceptionInAnEndpointIsLoggedWithATraceIdAndNeverLeaksIntoTheResponse()
    {
        LogCapture logs = new();
        await using AdminApiHarness api = await AdminApiHarness.StartAsync(Ct, logs, services => services.AddSingleton<IPermissionEvaluator>(new ThrowingPermissions()));

        using HttpResponseMessage response = await api.GetAsync("/api/v1/hosts");
        string body = await response.Content.ReadAsStringAsync(Ct);
        ApiProblem problem = JsonSerializer.Deserialize(body, Paduraksa.Admin.Json.AdminJsonContext.Default.ApiProblem)!;

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("TOP-SECRET", body, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", body, StringComparison.Ordinal);
        Assert.DoesNotContain("vault.db", body, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(problem.Trace));
        LogEntry entry = Assert.Single(logs.Entries, e => e.Exception is InvalidOperationException);
        Assert.Contains(problem.Trace!, entry.Message, StringComparison.Ordinal);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Error, entry.Level);
        AssertSecurityHeaders(response);
    }

    [Fact]
    public async Task AnExceptionInsideTheAuthPipelineGetsTheProblemMiddlewaresAnswerBecauseItIsFirst()
    {
        LogCapture logs = new();
        await using AdminApiHarness api = await AdminApiHarness.StartAsync(Ct, logs);
        // The setup gate (auth pipeline, behind the problem middleware) walks this list on every request that isn't a health or setup one.
        AdminPipelineHooks hooks = api.Host.GetServices(HostRole.Admin).GetRequiredService<AdminPipelineHooks>();
        hooks.SetupAllowedPathPrefixes.Add("not-a-path"); // a PathString must start with a slash: building one from this throws

        using HttpResponseMessage response = await api.GetAsync("/api/v1/hosts");
        string body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.False(string.IsNullOrEmpty(JsonSerializer.Deserialize(body, Paduraksa.Admin.Json.AdminJsonContext.Default.ApiProblem)!.Trace));
        Assert.DoesNotContain("ArgumentException", body, StringComparison.Ordinal);
        Assert.Contains(logs.Entries, e => e.Exception is ArgumentException && e.Category.Contains("ApiPipelineMiddleware", StringComparison.Ordinal));
        AssertSecurityHeaders(response);
    }

    [Fact]
    public async Task AFailedResponseKeepsTheSecurityHeaders()
    {
        await using AdminApiHarness api = await AdminApiHarness.StartAsync(Ct);

        using HttpResponseMessage tooLarge = await api.SendAsync(HttpMethod.Put, "/api/v1/settings", ifMatch: "\"0\"", content: AdminApiHarness.JsonBody($$"""{"version":0,"padding":"{{new string('x', 300 * 1024)}}"}"""));
        using HttpResponseMessage unknown = await api.GetAsync("/api/v1/nothing-here");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        AssertSecurityHeaders(tooLarge);
        AssertSecurityHeaders(unknown);
    }

    private static void AssertSecurityHeaders(HttpResponseMessage response)
    {
        Assert.Equal("nosniff", string.Join(',', response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("no-referrer", string.Join(',', response.Headers.GetValues("Referrer-Policy")));
        Assert.Equal("DENY", string.Join(',', response.Headers.GetValues("X-Frame-Options")));
        Assert.Contains("default-src 'none'", string.Join(',', response.Headers.GetValues("Content-Security-Policy")), StringComparison.Ordinal);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task BodiesAreJsonOnlyAndStrictAndTheErrorsNameNoClrTypes()
    {
        await using AdminApiHarness api = await AdminApiHarness.StartAsync(Ct);

        using HttpResponseMessage text = await api.SendAsync(HttpMethod.Put, "/api/v1/settings", ifMatch: "\"0\"", content: new StringContent("{}", Encoding.UTF8, "text/plain"));
        using HttpResponseMessage malformed = await api.SendAsync(HttpMethod.Put, "/api/v1/settings", ifMatch: "\"0\"", content: AdminApiHarness.JsonBody("{ not json"));
        using HttpResponseMessage unknown = await api.SendAsync(HttpMethod.Put, "/api/v1/settings", ifMatch: "\"0\"", content: AdminApiHarness.JsonBody("""{"version":0,"thisIsATypo":1}"""));
        using HttpResponseMessage empty = await api.SendAsync(HttpMethod.Put, "/api/v1/settings", ifMatch: "\"0\"", content: AdminApiHarness.JsonBody(string.Empty));
        using HttpResponseMessage wrongType = await api.SendAsync(HttpMethod.Put, "/api/v1/settings", ifMatch: "\"0\"", content: AdminApiHarness.JsonBody("""{"version":"zero"}"""));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, text.StatusCode);
        Assert.Equal(415, (await AdminApiHarness.ProblemAsync(text, Ct)).Status);
        foreach (HttpResponseMessage bad in new[] { malformed, unknown, empty, wrongType })
        {
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            ApiProblem problem = await AdminApiHarness.ProblemAsync(bad, Ct);
            Assert.Equal(400, problem.Status);
            Assert.DoesNotContain("System.", problem.Detail!, StringComparison.Ordinal);
            Assert.DoesNotContain("Paduraksa.", problem.Detail!, StringComparison.Ordinal);
        }

        Assert.Contains("$.version", (await AdminApiHarness.ProblemAsync(wrongType, Ct)).Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingPolymorphicDiscriminatorIs400NotAnUnhandledException()
    {
        await using AdminApiHarness api = await AdminApiHarness.StartAsync(Ct);
        const string Request = """{"url":"http://a.test/","clientIp":"203.0.113.9"}""";

        foreach (string body in new[] { "{\"request\":" + Request + ",\"target\":{}}", "{\"request\":" + Request + ",\"target\":{\"kind\":\"nonsense\"}}" })
        {
            using HttpResponseMessage response = await api.SendAsync(HttpMethod.Post, "/api/v1/simulate", content: AdminApiHarness.JsonBody(body));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(400, (await AdminApiHarness.ProblemAsync(response, Ct)).Status);
        }
    }

    [Fact]
    public async Task AJsonBodyPastTheDefaultLimitIs413()
    {
        await using AdminApiHarness api = await AdminApiHarness.StartAsync(Ct);

        string huge = $$"""{"version":0,"padding":"{{new string('x', 300 * 1024)}}"}""";
        using HttpResponseMessage response = await api.SendAsync(HttpMethod.Put, "/api/v1/settings", ifMatch: "\"0\"", content: AdminApiHarness.JsonBody(huge));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(413, (await AdminApiHarness.ProblemAsync(response, Ct)).Status);
    }

    private static List<RouteEndpoint> ApiEndpoints(AdminApiHarness api) =>
    [.. ((IEndpointRouteBuilder)api.Host.Admin).DataSources
        .SelectMany(d => d.Endpoints)
        .OfType<RouteEndpoint>()
        .Where(e => e.RoutePattern.RawText is { } text && (text.Equals("/api", StringComparison.OrdinalIgnoreCase) || text.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)))];

    [Fact]
    public async Task EveryMappedApiEndpointRequiresAPermissionOrASignInOrIsAnonymousOnPurpose()
    {
        await using AdminApiHarness api = await AdminApiHarness.StartAsync(Ct);

        List<RouteEndpoint> endpoints = ApiEndpoints(api);

        Assert.True(endpoints.Count > 50, $"only {endpoints.Count} endpoints under /api");
        List<string> undeclared = [.. endpoints
            .Where(e => e.Metadata.GetMetadata<RequiredPermissionMetadata>() is null
                && e.Metadata.GetMetadata<AdminAnonymousMetadata>() is null
                && e.Metadata.GetMetadata<AdminAuthenticatedMetadata>() is null)
            .Select(Describe)];
        Assert.Empty(undeclared);
        Assert.Contains(endpoints, e => e.RoutePattern.RawText == "/api/v1/openapi.json" && e.Metadata.GetMetadata<AdminAnonymousMetadata>() is not null);
    }

    private static string Describe(RouteEndpoint endpoint) =>
        $"{string.Join(',', endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [])} {endpoint.RoutePattern.RawText}";

    [Fact]
    public async Task EveryResourceRouteHasExactlyTheExpectedPermission()
    {
        await using AdminApiHarness api = await AdminApiHarness.StartAsync(Ct);
        (string Route, Permission Permission)[] expected =
        [
            ("GET /api/v1/config", Permission.ConfigRead),
            ("GET /api/v1/settings", Permission.ConfigRead),
            ("PUT /api/v1/settings", Permission.ConfigWrite),
            ("GET /api/v1/hosts", Permission.ConfigRead),
            ("POST /api/v1/hosts", Permission.ConfigWrite),
            ("GET /api/v1/hosts/{id:HostId}", Permission.ConfigRead),
            ("PUT /api/v1/hosts/{id:HostId}", Permission.ConfigWrite),
            ("DELETE /api/v1/hosts/{id:HostId}", Permission.ConfigWrite),
            ("GET /api/v1/hosts/{hostId:HostId}/routes", Permission.ConfigRead),
            ("GET /api/v1/hosts/{hostId:HostId}/routes/{routeId:RouteId}", Permission.ConfigRead),
            ("PUT /api/v1/hosts/{hostId:HostId}/routes/{routeId:RouteId}", Permission.ConfigWrite),
            ("DELETE /api/v1/hosts/{hostId:HostId}/routes/{routeId:RouteId}", Permission.ConfigWrite),
            ("GET /api/v1/upstreams", Permission.ConfigRead),
            ("POST /api/v1/upstreams", Permission.ConfigWrite),
            ("GET /api/v1/upstreams/{id:UpstreamId}", Permission.ConfigRead),
            ("PUT /api/v1/upstreams/{id:UpstreamId}", Permission.ConfigWrite),
            ("DELETE /api/v1/upstreams/{id:UpstreamId}", Permission.ConfigWrite),
            ("GET /api/v1/profiles", Permission.ConfigRead),
            ("POST /api/v1/profiles", Permission.ConfigWrite),
            ("GET /api/v1/profiles/{id:ProfileId}", Permission.ConfigRead),
            ("PUT /api/v1/profiles/{id:ProfileId}", Permission.ConfigWrite),
            ("DELETE /api/v1/profiles/{id:ProfileId}", Permission.ConfigWrite),
            ("GET /api/v1/revisions", Permission.ConfigRead),
            ("GET /api/v1/revisions/diff", Permission.ConfigRead),
            ("GET /api/v1/revisions/{id:ConfigRevisionId}", Permission.ConfigRead),
            ("POST /api/v1/revisions/{id:ConfigRevisionId}/rollback", Permission.ConfigWrite),
            ("GET /api/v1/certificates", Permission.CertificatesRead),
            ("GET /api/v1/certificates/{id:CertificateId}", Permission.CertificatesRead),
            ("POST /api/v1/certificates", Permission.CertificatesWrite),
            ("PUT /api/v1/certificates/{id:CertificateId}", Permission.CertificatesWrite),
            ("POST /api/v1/certificates/self-signed", Permission.CertificatesWrite),
            ("POST /api/v1/certificates/local-ca", Permission.CertificatesWrite),
            ("POST /api/v1/certificates/local-ca/leaf", Permission.CertificatesWrite),
            ("POST /api/v1/certificates/{id:CertificateId}/reissue", Permission.CertificatesWrite),
            ("DELETE /api/v1/certificates/{id:CertificateId}", Permission.CertificatesWrite),
            ("POST /api/v1/certificates/acme", Permission.CertificatesWrite),
            ("POST /api/v1/certificates/{id:CertificateId}/renew", Permission.CertificatesWrite),
            ("GET /api/v1/certificates/{id:CertificateId}/order", Permission.CertificatesRead),
            ("GET /api/v1/acme-orders", Permission.CertificatesRead),
            ("GET /api/v1/acme-accounts", Permission.ProvidersRead),
            ("GET /api/v1/acme-accounts/{id:AcmeAccountId}", Permission.ProvidersRead),
            ("POST /api/v1/acme-accounts", Permission.ProvidersWrite),
            ("DELETE /api/v1/acme-accounts/{id:AcmeAccountId}", Permission.ProvidersWrite),
            ("GET /api/v1/dns-providers", Permission.ProvidersRead),
            ("GET /api/v1/dns-providers/{id:DnsProviderId}", Permission.ProvidersRead),
            ("POST /api/v1/dns-providers", Permission.ProvidersWrite),
            ("PUT /api/v1/dns-providers/{id:DnsProviderId}", Permission.ProvidersWrite),
            ("DELETE /api/v1/dns-providers/{id:DnsProviderId}", Permission.ProvidersWrite),
            ("POST /api/v1/dns-providers/{id:DnsProviderId}/test", Permission.ProvidersWrite),
            ("GET /api/v1/audit", Permission.AuditRead),
            ("GET /api/v1/audit/verify", Permission.AuditRead),
            ("GET /api/v1/logs", Permission.LogsRead),
            ("GET /api/v1/logs/{name}/tail", Permission.LogsRead),
            ("GET /api/v1/logs/{name}", Permission.LogsRead),
            ("POST /api/v1/simulate", Permission.Simulate),
        ];

        Dictionary<string, Permission?> actual = ApiEndpoints(api)
            .SelectMany(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? []).Select(method => (Key: $"{method} {e.RoutePattern.RawText!.TrimEnd('/')}", Permission: e.Metadata.GetMetadata<RequiredPermissionMetadata>()?.Permission)))
            .ToDictionary(x => x.Key, x => x.Permission, StringComparer.Ordinal);

        foreach ((string route, Permission permission) in expected)
        {
            Assert.True(actual.TryGetValue(route, out Permission? found), $"{route} is not mapped");
            Assert.Equal(permission, found);
        }

        // Nothing resource-like is mapped that this table doesn't know (the identity endpoints are the auth branch's, the CA
        // endpoints are anonymous by design, and both are checked by their own rules).
        string[] anonymousPrefixes = ["/api/v1/setup", "/api/v1/auth", "/api/v1/me", "/api/v1/sessions", "/api/v1/tokens", "/api/v1/users", "/api/v1/ca", "/api/v1/openapi.json", "/api/docs"];
        IEnumerable<string> unknown = actual.Keys.Where(k => !expected.Any(e => e.Route == k) && !anonymousPrefixes.Any(prefix => k[(k.IndexOf(' ', StringComparison.Ordinal) + 1)..].StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
        Assert.Empty(unknown);
    }

    [Fact]
    public async Task TheOpenApiDocumentIsAnonymousOpenApi31WithTypedIdSchemasAndEveryOperationDescribed()
    {
        await using AdminApiHarness api = await AdminApiHarness.StartAsync(Ct);

        using HttpResponseMessage response = await api.GetAsync("/api/v1/openapi.json", caller: null);
        string text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, text);
        using HttpResponseMessage again = await api.GetAsync("/api/v1/openapi.json", caller: null);
        Assert.Equal(text, await again.Content.ReadAsStringAsync(Ct)); // cached: byte for byte the same, example IDs included
        JsonObject document = JsonNode.Parse(text)!.AsObject();

        Assert.StartsWith("3.1", document["openapi"]!.GetValue<string>(), StringComparison.Ordinal);
        JsonObject paths = document["paths"]!.AsObject();
        Assert.Contains("/api/v1/hosts/{id}", paths.Select(p => p.Key));
        Assert.Contains("/api/v1/auth/login", paths.Select(p => p.Key));
        int operations = 0;
        foreach ((string path, JsonNode? item) in paths)
        {
            foreach ((string method, JsonNode? operation) in item!.AsObject())
            {
                operations++;
                Assert.False(string.IsNullOrWhiteSpace(operation!["summary"]?.GetValue<string>()), $"{method} {path} has no summary");
                bool permission = operation["x-required-permission"] is not null;
                bool secured = operation["security"] is JsonArray { Count: > 0 };
                bool anonymous = operation["security"] is JsonArray { Count: 0 };
                Assert.True(permission || secured || anonymous, $"{method} {path} declares neither a permission, a sign-in nor anonymous access");
                if (permission || secured)
                {
                    Assert.NotNull(operation["responses"]!["401"]);
                }

                if (permission)
                {
                    Assert.NotNull(operation["responses"]!["403"]);
                }
            }
        }

        Assert.True(operations > 50, $"{operations} operations");
        JsonObject schemas = document["components"]!["schemas"]!.AsObject();
        JsonObject hostId = schemas.Single(s => s.Key.Contains("HostId", StringComparison.Ordinal) && s.Value!["pattern"] is not null).Value!.AsObject();
        Assert.Equal("^host_[0-9A-Za-z]{22}$", hostId["pattern"]!.GetValue<string>());
        string example = hostId["examples"]![0]!.GetValue<string>();
        Assert.Matches("^host_[0-9A-Za-z]{22}$", example);
        Assert.NotEqual("host_0000000000000000000000", example);
        Assert.Contains("ApiProblem", schemas.Select(s => s.Key));
        JsonNode idParameter = paths["/api/v1/hosts/{id}"]!["get"]!["parameters"]![0]!;
        JsonNode idSchema = idParameter["schema"]!;
        Assert.True(
            idSchema["$ref"]?.GetValue<string>().EndsWith("/HostId", StringComparison.Ordinal) == true || idSchema["pattern"]?.GetValue<string>() == "^host_[0-9A-Za-z]{22}$",
            "the path parameter is described as a HostId: " + idSchema.ToJsonString());
        Assert.Contains("config.publishFailed", paths["/api/v1/hosts/{id}"]!["put"]!["responses"]!["500"]!["description"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("If-Match", document["info"]!["description"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheReferenceUiIsAnonymousServesItsScriptLocallyAndNeedsNothingFromOutside()
    {
        await using AdminApiHarness api = await AdminApiHarness.StartAsync(Ct);

        using HttpResponseMessage page = await api.GetAsync("/api/docs", caller: null);
        string html = await page.Content.ReadAsStringAsync(Ct);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.StartsWith("text/html", page.Content.Headers.ContentType?.MediaType, StringComparison.Ordinal);

        // The docs' own CSP wins over the default one of the security-headers middleware.
        string csp = string.Join(' ', page.Headers.GetValues("Content-Security-Policy"));
        Assert.Single(page.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("default-src 'none'", csp, StringComparison.Ordinal);
        Match cspNonce = Regex.Match(csp, "script-src 'self' 'nonce-([^']+)'");
        Assert.True(cspNonce.Success, csp);
        Assert.DoesNotContain("http", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("'unsafe-eval'", csp, StringComparison.Ordinal);

        // Every script tag, the inline module included, carries the policy's nonce. The HTML encoder escapes characters
        // like '+' (the nonce is base64), so the assertion decodes the page first.
        string decoded = System.Net.WebUtility.HtmlDecode(html);
        MatchCollection scripts = Regex.Matches(decoded, "<script[^>]*>");
        Assert.NotEmpty(scripts);
        Assert.All(scripts, s => Assert.Contains($"nonce=\"{cspNonce.Groups[1].Value}\"", s.Value, StringComparison.Ordinal));

        // Nothing is fetched from another origin.
        Assert.DoesNotMatch(@"(src|href)\s*=\s*[""']https?://", html);
        Assert.DoesNotContain("cdn.jsdelivr.net", html, StringComparison.Ordinal);
        Assert.DoesNotContain("fonts.googleapis.com", html, StringComparison.Ordinal);
        Assert.DoesNotContain("proxy.scalar.com", html, StringComparison.Ordinal);

        // The script it names is served by this app, and needs no 'unsafe-eval': the bundle neither evals nor builds functions from strings.
        Match script = Regex.Match(html, @"<script[^>]*\ssrc=""([^""]+)""");
        Assert.True(script.Success, "the page has no external script reference to check");
        Uri scriptUri = new(page.RequestMessage!.RequestUri!, script.Groups[1].Value);
        Assert.Equal(api.BaseAddress.Authority, scriptUri.Authority);
        Assert.StartsWith("/api/docs/", scriptUri.PathAndQuery, StringComparison.Ordinal);
        using HttpResponseMessage js = await api.GetAsync(scriptUri.PathAndQuery, caller: null);
        Assert.Equal(HttpStatusCode.OK, js.StatusCode);
        string bundle = await js.Content.ReadAsStringAsync(Ct);
        Assert.True(bundle.Length > 100_000);
        Assert.DoesNotMatch(@"\beval\s*\(", bundle);
        Assert.DoesNotMatch(@"new\s+Function\s*\(", bundle);
    }

    [Fact]
    public async Task AnonymousRequestsToProtectedEndpointsAre401Problems()
    {
        await using AdminApiHarness api = await AdminApiHarness.StartAsync(Ct);

        using HttpResponseMessage response = await api.GetAsync("/api/v1/config", caller: null);
        ApiProblem problem = await AdminApiHarness.ProblemAsync(response, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(401, problem.Status);
        Assert.Equal("auth.required", problem.Code);
    }

    [Fact]
    public async Task HealthEndpointsStayAsTheyAre()
    {
        await using AdminApiHarness api = await AdminApiHarness.StartAsync(Ct);

        using HttpResponseMessage live = await api.GetAsync("/healthz", caller: null);
        using HttpResponseMessage ready = await api.GetAsync("/readyz", caller: null);

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal("text/plain", live.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public async Task ASettingsReadCarriesItsVersionAsETag()
    {
        await using AdminApiHarness api = await AdminApiHarness.StartAsync(Ct);

        using HttpResponseMessage response = await api.GetAsync("/api/v1/settings");
        GlobalSettings settings = await AdminApiHarness.ReadAsync(response, Paduraksa.Admin.Json.AdminJsonContext.Default.GlobalSettings, Ct);

        Assert.Equal("\"0\"", AdminApiHarness.ETag(response));
        Assert.Equal(0, settings.Version);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Theory]
    [InlineData("0", 400)] // unquoted: the tags this API issues are quoted
    [InlineData("\"03\"", 400)] // a leading zero is not the version
    [InlineData("W/\"0\"", 412)] // a weak tag never matches a strong comparison
    [InlineData("*", 400)]
    [InlineData("\"0\", \"1\"", 400)]
    [InlineData("\"abc\"", 400)]
    public async Task IfMatchTakesOneQuotedStrongTag(string ifMatch, int expected)
    {
        await using AdminApiHarness api = await AdminApiHarness.StartAsync(Ct);
        const string Body = """{"version":0,"unknownHost":"misdirectedRequest"}""";

        using HttpResponseMessage response = await api.SendAsync(HttpMethod.Put, "/api/v1/settings", ifMatch: ifMatch, content: AdminApiHarness.JsonBody(Body));
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.Equal(expected, (await AdminApiHarness.ProblemAsync(response, Ct)).Status);

        using HttpResponseMessage good = await api.SendAsync(HttpMethod.Put, "/api/v1/settings", ifMatch: "\"0\"", content: AdminApiHarness.JsonBody(Body));
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
    }

    private sealed class ThrowingPermissions : IPermissionEvaluator
    {
        public ValueTask<IReadOnlySet<Permission>> GetPermissionsAsync(Actor actor, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("TOP-SECRET internal detail at /var/lib/paduraksa/vault.db");

        public ValueTask<bool> IsAllowedAsync(CommandOrigin origin, Permission permission, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("TOP-SECRET internal detail at /var/lib/paduraksa/vault.db");
    }
}
