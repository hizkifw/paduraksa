using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
public sealed class ApiConventionTests(SharedAdminApi shared) : IClassFixture<SharedAdminApi>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnUnknownRouteUnderApiIsAProblemNotAnEmptyBody()
    {
        var api = shared.Api;

        using var response = await api.GetAsync("/api/v1/no-such-thing");
        var problem = await AdminApiHarness.ProblemAsync(response, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(404, problem.Status);
        Assert.Equal("Not found", problem.Title);
        Assert.False(string.IsNullOrEmpty(problem.Trace));
    }

    [Fact]
    public async Task AWrongMethodIs405WithTheAllowHeaderAndAProblem()
    {
        var api = shared.Api;

        using var response = await api.SendAsync(HttpMethod.Patch, "/api/v1/hosts");
        var problem = await AdminApiHarness.ProblemAsync(response, Ct);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Contains("GET", response.Content.Headers.Allow);
        Assert.Equal(405, problem.Status);
    }

    [Fact]
    public async Task AMalformedTypedIdInTheRouteDoesNotMatch()
    {
        var api = shared.Api;

        foreach (var path in new[] { "/api/v1/hosts/not-an-id", "/api/v1/hosts/upstream_0000000000000000000000", "/api/v1/revisions/host_0000000000000000000000" })
        {
            using var response = await api.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(404, (await AdminApiHarness.ProblemAsync(response, Ct)).Status);
        }
    }

    [Fact]
    public async Task AnExceptionInAnEndpointIsLoggedWithATraceIdAndNeverLeaksIntoTheResponse()
    {
        LogCapture logs = new();
        await using var api = await AdminApiHarness.StartAsync(Ct, logs, services => services.AddSingleton<IPermissionEvaluator>(new ThrowingPermissions()));

        using var response = await api.GetAsync("/api/v1/hosts");
        var body = await response.Content.ReadAsStringAsync(Ct);
        var problem = JsonSerializer.Deserialize(body, Paduraksa.Admin.Json.AdminJsonContext.Default.ApiProblem)!;

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("TOP-SECRET", body, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", body, StringComparison.Ordinal);
        Assert.DoesNotContain("vault.db", body, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(problem.Trace));
        var entry = Assert.Single(logs.Entries, e => e.Exception is InvalidOperationException);
        Assert.Contains(problem.Trace!, entry.Message, StringComparison.Ordinal);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Error, entry.Level);
        AssertSecurityHeaders(response);
    }

    [Fact]
    public async Task AnExceptionInsideTheAuthPipelineGetsTheProblemMiddlewaresAnswerBecauseItIsFirst()
    {
        LogCapture logs = new();
        await using var api = await AdminApiHarness.StartAsync(Ct, logs);
        // The setup gate (auth pipeline, behind the problem middleware) walks this list on every request that isn't a health or setup one.
        var hooks = api.Host.GetServices(HostRole.Admin).GetRequiredService<AdminPipelineHooks>();
        hooks.SetupAllowedPathPrefixes.Add("not-a-path"); // a PathString must start with a slash: building one from this throws

        using var response = await api.GetAsync("/api/v1/hosts");
        var body = await response.Content.ReadAsStringAsync(Ct);

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
        var api = shared.Api;

        using var tooLarge = await api.SendAsync(
            HttpMethod.Put, "/api/v1/settings", ifMatch: "\"0\"", content: AdminApiHarness.JsonBody($$"""{"version":0,"padding":"{{new string('x', 300 * 1024)}}"}"""), expectContinue: true);
        using var unknown = await api.GetAsync("/api/v1/nothing-here");

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
        var api = shared.Api;

        using var text = await api.SendAsync(HttpMethod.Put, "/api/v1/settings", ifMatch: "\"0\"", content: new StringContent("{}", Encoding.UTF8, "text/plain"));
        using var malformed = await api.SendAsync(HttpMethod.Put, "/api/v1/settings", ifMatch: "\"0\"", content: AdminApiHarness.JsonBody("{ not json"));
        using var unknown = await api.SendAsync(HttpMethod.Put, "/api/v1/settings", ifMatch: "\"0\"", content: AdminApiHarness.JsonBody("""{"version":0,"thisIsATypo":1}"""));
        using var empty = await api.SendAsync(HttpMethod.Put, "/api/v1/settings", ifMatch: "\"0\"", content: AdminApiHarness.JsonBody(string.Empty));
        using var wrongType = await api.SendAsync(HttpMethod.Put, "/api/v1/settings", ifMatch: "\"0\"", content: AdminApiHarness.JsonBody("""{"version":"zero"}"""));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, text.StatusCode);
        Assert.Equal(415, (await AdminApiHarness.ProblemAsync(text, Ct)).Status);
        foreach (var bad in new[] { malformed, unknown, empty, wrongType })
        {
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            var problem = await AdminApiHarness.ProblemAsync(bad, Ct);
            Assert.Equal(400, problem.Status);
            Assert.DoesNotContain("System.", problem.Detail!, StringComparison.Ordinal);
            Assert.DoesNotContain("Paduraksa.", problem.Detail!, StringComparison.Ordinal);
        }

        Assert.Contains("$.version", (await AdminApiHarness.ProblemAsync(wrongType, Ct)).Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingPolymorphicDiscriminatorIs400NotAnUnhandledException()
    {
        var api = shared.Api;
        const string Request = """{"url":"http://a.test/","clientIp":"203.0.113.9"}""";

        foreach (var body in new[] { "{\"request\":" + Request + ",\"target\":{}}", "{\"request\":" + Request + ",\"target\":{\"kind\":\"nonsense\"}}" })
        {
            using var response = await api.SendAsync(HttpMethod.Post, "/api/v1/simulate", content: AdminApiHarness.JsonBody(body));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(400, (await AdminApiHarness.ProblemAsync(response, Ct)).Status);
        }
    }

    [Fact]
    public async Task AJsonBodyPastTheDefaultLimitIs413()
    {
        var api = shared.Api;

        var huge = $$"""{"version":0,"padding":"{{new string('x', 300 * 1024)}}"}""";
        using var response = await api.SendAsync(HttpMethod.Put, "/api/v1/settings", ifMatch: "\"0\"", content: AdminApiHarness.JsonBody(huge), expectContinue: true);

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
        var api = shared.Api;

        var endpoints = ApiEndpoints(api);

        Assert.True(endpoints.Count > 50, $"only {endpoints.Count} endpoints under /api");
        List<string> undeclared = [.. endpoints
            .Where(e => e.Metadata.GetMetadata<RequiredPermissionMetadata>() is null
                && e.Metadata.GetMetadata<AdminAnonymousMetadata>() is null
                && e.Metadata.GetMetadata<AdminAuthenticatedMetadata>() is null)
            .Select(Describe)];
        Assert.Empty(undeclared);
        Assert.Contains(endpoints, e => e.RoutePattern.RawText == "/api/v1/openapi.json" && e.Metadata.GetMetadata<AdminAnonymousMetadata>() is not null);
    }

    [Fact]
    public async Task EveryCallerRequirementAlsoHasItsFilter()
    {
        // The filter itself isn't exposed on RouteEndpoint, so the helpers attach AdminGuardFilterMetadata with it: an
        // endpoint that carries a caller requirement without that marker would let an anonymous request through binding
        // to AdminCaller.BindAsync's stand-in.
        var api = shared.Api;

        var endpoints = ApiEndpoints(api);
        List<string> unguarded = [.. endpoints
            .Where(e => (e.Metadata.GetMetadata<AdminAuthenticatedMetadata>() is not null
                    || e.Metadata.GetMetadata<RequiredPermissionMetadata>() is not null)
                && e.Metadata.GetMetadata<AdminGuardFilterMetadata>() is null)
            .Select(Describe)];
        Assert.Empty(unguarded);
    }

    private static string Describe(RouteEndpoint endpoint) =>
        $"{string.Join(',', endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [])} {endpoint.RoutePattern.RawText}";

    [Fact]
    public async Task EveryResourceRouteHasExactlyTheExpectedPermission()
    {
        var api = shared.Api;
        (string Route, Permission Permission)[] expected =
        [
            ("GET /api/v1/config", Permission.ConfigRead),
            ("GET /api/v1/config/export", Permission.ConfigRead),
            ("POST /api/v1/config/import", Permission.ConfigWrite),
            ("GET /api/v1/drafts", Permission.ConfigWrite),
            ("POST /api/v1/drafts", Permission.ConfigWrite),
            ("GET /api/v1/drafts/{id:DraftId}", Permission.ConfigWrite),
            ("GET /api/v1/drafts/{id:DraftId}/config", Permission.ConfigWrite),
            ("POST /api/v1/drafts/{id:DraftId}/entries", Permission.ConfigWrite),
            ("DELETE /api/v1/drafts/{id:DraftId}/entries/{number:int}", Permission.ConfigWrite),
            ("POST /api/v1/drafts/{id:DraftId}/review", Permission.ConfigWrite),
            ("POST /api/v1/drafts/{id:DraftId}/apply", Permission.ConfigWrite),
            ("DELETE /api/v1/drafts/{id:DraftId}", Permission.ConfigWrite),
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
            ("GET /api/v1/rate-limit-policies", Permission.ConfigRead),
            ("POST /api/v1/rate-limit-policies", Permission.ConfigWrite),
            ("GET /api/v1/rate-limit-policies/{id:RateLimitPolicyId}", Permission.ConfigRead),
            ("PUT /api/v1/rate-limit-policies/{id:RateLimitPolicyId}", Permission.ConfigWrite),
            ("DELETE /api/v1/rate-limit-policies/{id:RateLimitPolicyId}", Permission.ConfigWrite),
            ("GET /api/v1/access-lists", Permission.ConfigRead),
            ("POST /api/v1/access-lists", Permission.ConfigWrite),
            ("GET /api/v1/access-lists/{id:AccessListId}", Permission.ConfigRead),
            ("PUT /api/v1/access-lists/{id:AccessListId}", Permission.ConfigWrite),
            ("DELETE /api/v1/access-lists/{id:AccessListId}", Permission.ConfigWrite),
            ("GET /api/v1/rule-sets", Permission.ConfigRead),
            ("GET /api/v1/rule-sets/presets", Permission.ConfigRead),
            ("POST /api/v1/rule-sets", Permission.ConfigWrite),
            ("GET /api/v1/rule-sets/{id:RuleSetId}", Permission.ConfigRead),
            ("PUT /api/v1/rule-sets/{id:RuleSetId}", Permission.ConfigWrite),
            ("DELETE /api/v1/rule-sets/{id:RuleSetId}", Permission.ConfigWrite),
            ("GET /api/v1/auth-providers", Permission.ConfigRead),
            ("POST /api/v1/auth-providers", Permission.ConfigWrite),
            ("GET /api/v1/auth-providers/{id:AuthProviderId}", Permission.ConfigRead),
            ("PUT /api/v1/auth-providers/{id:AuthProviderId}", Permission.ConfigWrite),
            ("DELETE /api/v1/auth-providers/{id:AuthProviderId}", Permission.ConfigWrite),
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
            ("GET /api/v1/certificates/local-ca", Permission.CertificatesRead),
            ("GET /api/v1/certificates/{id:CertificateId}", Permission.CertificatesRead),
            ("POST /api/v1/certificates", Permission.CertificatesWrite),
            ("PUT /api/v1/certificates/{id:CertificateId}", Permission.CertificatesWrite),
            ("POST /api/v1/certificates/self-signed", Permission.CertificatesWrite),
            ("POST /api/v1/certificates/local-ca", Permission.CertificatesWrite),
            ("POST /api/v1/certificates/local-ca/leaf", Permission.CertificatesWrite),
            ("POST /api/v1/certificates/local-ca/rotate", Permission.CertificatesWrite),
            ("POST /api/v1/certificates/local-ca/reissue", Permission.CertificatesWrite),
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
            ("GET /api/v1/notification-channels", Permission.ProvidersRead),
            ("GET /api/v1/notification-channels/{id:NotificationChannelId}", Permission.ProvidersRead),
            ("POST /api/v1/notification-channels", Permission.ProvidersWrite),
            ("PUT /api/v1/notification-channels/{id:NotificationChannelId}", Permission.ProvidersWrite),
            ("DELETE /api/v1/notification-channels/{id:NotificationChannelId}", Permission.ProvidersWrite),
            ("POST /api/v1/notification-channels/{id:NotificationChannelId}/test", Permission.ProvidersWrite),
            ("GET /api/v1/notifications/history", Permission.ProvidersRead),
            ("POST /api/v1/notifications/deliveries/{id:NotificationDeliveryId}/retry", Permission.ProvidersWrite),
            ("POST /api/v1/notifications/events/{id:NotificationEventId}/retry", Permission.ProvidersWrite),
            ("GET /api/v1/upstreams/health", Permission.ConfigRead),
            ("GET /api/v1/upstreams/{id:UpstreamId}/health", Permission.ConfigRead),
            ("GET /api/v1/audit", Permission.AuditRead),
            ("GET /api/v1/audit/verify", Permission.AuditRead),
            ("GET /api/v1/logs", Permission.LogsRead),
            ("GET /api/v1/logs/tail", Permission.LogsRead),
            ("GET /api/v1/logs/recent", Permission.LogsRead),
            ("GET /api/v1/logs/{name}/tail", Permission.LogsRead),
            ("GET /api/v1/logs/{name}", Permission.LogsRead),
            ("GET /api/v1/security/overview", Permission.ConfigRead),
            ("GET /api/v1/metrics", Permission.MetricsRead),
            ("GET /api/v1/metrics/overview", Permission.MetricsRead),
            ("GET /api/v1/analytics", Permission.MetricsRead),
            ("POST /api/v1/simulate", Permission.Simulate),
            ("POST /api/v1/backup", Permission.BackupCreate),
            ("POST /api/v1/error-pages/preview", Permission.ConfigRead),
        ];

        var actual = ApiEndpoints(api)
            .SelectMany(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? []).Select(method => (Key: $"{method} {e.RoutePattern.RawText!.TrimEnd('/')}", Permission: e.Metadata.GetMetadata<RequiredPermissionMetadata>()?.Permission)))
            .ToDictionary(x => x.Key, x => x.Permission, StringComparer.Ordinal);

        foreach (var (route, permission) in expected)
        {
            Assert.True(actual.TryGetValue(route, out var found), $"{route} is not mapped");
            Assert.Equal(permission, found);
        }

        // Nothing resource-like is mapped that this table doesn't know (the identity endpoints are the auth branch's, the CA
        // endpoints are anonymous by design, and both are checked by their own rules).
        string[] anonymousPrefixes = ["/api/v1/setup", "/api/v1/auth", "/api/v1/me", "/api/v1/sessions", "/api/v1/tokens", "/api/v1/users", "/api/v1/ca", "/api/v1/openapi.json", "/api/docs"];
        var unknown = actual.Keys.Where(k => !expected.Any(e => e.Route == k) && !anonymousPrefixes.Any(prefix => k[(k.IndexOf(' ', StringComparison.Ordinal) + 1)..].StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
        Assert.Empty(unknown);
    }

    [Fact]
    public async Task EveryMutatingRouteRequiresWhatTheApplicationRequiresForItsOperation()
    {
        // The endpoints name their permission literally and the services enforce OperationPermissions on their own; this keeps the two
        // from drifting apart. Config resources run config commands, the certificate routes run certificate operations.
        string[] configResources = ["config", "hosts", "upstreams", "rate-limit-policies", "access-lists", "rule-sets", "auth-providers", "profiles", "settings", "revisions"];
        var mutating = ApiEndpoints(shared.Api)
            .SelectMany(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [])
                .Where(method => method is "POST" or "PUT" or "DELETE")
                .Select(method => (Route: $"{method} {e.RoutePattern.RawText}", Resource: e.RoutePattern.RawText!.Split('/')[3], e.Metadata.GetMetadata<RequiredPermissionMetadata>()?.Permission)))
            .ToList();

        var config = mutating.Where(m => configResources.Contains(m.Resource)).ToList();
        var certificates = mutating.Where(m => m.Resource == "certificates").ToList();
        Assert.NotEmpty(config);
        Assert.NotEmpty(certificates);
        Assert.All(config, m => Assert.True(m.Permission == OperationPermissions.ForConfigCommand, $"{m.Route} requires {m.Permission}"));
        Assert.All(certificates, m => Assert.True(m.Permission == OperationPermissions.ForCertificateOperation, $"{m.Route} requires {m.Permission}"));
        Assert.Equal(
            OperationPermissions.ForSimulation(Paduraksa.Contracts.Simulation.SimulationTarget.Live),
            [mutating.Single(m => m.Resource == "simulate").Permission!.Value]);
    }

    [Fact]
    public async Task TheOpenApiDocumentIsAnonymousOpenApi31WithTypedIdSchemasAndEveryOperationDescribed()
    {
        var api = shared.Api;

        using var response = await api.GetAsync("/api/v1/openapi.json", caller: null);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, text);
        using var again = await api.GetAsync("/api/v1/openapi.json", caller: null);
        Assert.Equal(text, await again.Content.ReadAsStringAsync(Ct)); // byte for byte the same every time
        var document = JsonNode.Parse(text)!.AsObject();

        Assert.StartsWith("3.1", document["openapi"]!.GetValue<string>(), StringComparison.Ordinal);
        var paths = document["paths"]!.AsObject();
        Assert.Contains("/api/v1/hosts/{id}", paths.Select(p => p.Key));
        Assert.Contains("/api/v1/auth/login", paths.Select(p => p.Key));
        var operations = 0;
        foreach (var (path, item) in paths)
        {
            foreach (var (method, operation) in item!.AsObject())
            {
                operations++;
                Assert.False(string.IsNullOrWhiteSpace(operation!["summary"]?.GetValue<string>()), $"{method} {path} has no summary");
                var permission = operation["x-required-permission"] is not null;
                var secured = operation["security"] is JsonArray { Count: > 0 };
                var anonymous = operation["security"] is JsonArray { Count: 0 };
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
        var schemas = document["components"]!["schemas"]!.AsObject();
        var hostId = schemas.Single(s => s.Key.Contains("HostId", StringComparison.Ordinal) && s.Value!["pattern"] is not null).Value!.AsObject();
        Assert.Equal("^host_[0-9A-Za-z]{22}$", hostId["pattern"]!.GetValue<string>());
        var example = hostId["examples"]![0]!.GetValue<string>();
        Assert.Matches("^host_[0-9A-Za-z]{22}$", example);
        Assert.NotEqual("host_0000000000000000000000", example);
        Assert.Contains("ApiProblem", schemas.Select(s => s.Key));
        var idParameter = paths["/api/v1/hosts/{id}"]!["get"]!["parameters"]![0]!;
        var idSchema = idParameter["schema"]!;
        Assert.True(
            idSchema["$ref"]?.GetValue<string>().EndsWith("/HostId", StringComparison.Ordinal) == true || idSchema["pattern"]?.GetValue<string>() == "^host_[0-9A-Za-z]{22}$",
            "the path parameter is described as a HostId: " + idSchema.ToJsonString());
        Assert.Contains("config.publishFailed", paths["/api/v1/hosts/{id}"]!["put"]!["responses"]!["500"]!["description"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("If-Match", document["info"]!["description"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheOpenApiDocumentDescribesTheHeaderAndLogSettings()
    {
        var api = shared.Api;

        using var response = await api.GetAsync("/api/v1/openapi.json", caller: null);
        var document = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!.AsObject();
        var schemas = document["components"]!["schemas"]!.AsObject();

        // HostSettings carries it beside the TLS layer, and the schema says what each field means.
        var hostSettings = schemas["HostSettings"]!.AsObject();
        Assert.Contains("hsts", hostSettings["properties"]!.AsObject().Select(p => p.Key));
        var property = hostSettings["properties"]!["hsts"]!;
        var reference = property["$ref"]?.GetValue<string>() ?? property["oneOf"]?[1]?["$ref"]?.GetValue<string>();
        Assert.NotNull(reference);
        Assert.Contains("HstsSettings", reference!, StringComparison.Ordinal);

        var hsts = schemas["HstsSettings"]!.AsObject();
        var properties = hsts["properties"]!.AsObject();
        Assert.Equal(["enabled", "maxAge", "includeSubDomains", "preload"], properties.Select(p => p.Key));
        Assert.Contains("max-age", properties["maxAge"]!["description"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("preload", properties["preload"]!["description"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);

        // The Server header layer is beside HSTS, with its own mode and value.
        Assert.Contains("serverHeader", hostSettings["properties"]!.AsObject().Select(p => p.Key));
        var serverProperty = hostSettings["properties"]!["serverHeader"]!;
        var serverReference = serverProperty["$ref"]?.GetValue<string>() ?? serverProperty["oneOf"]?[1]?["$ref"]?.GetValue<string>();
        Assert.NotNull(serverReference);
        Assert.Contains("ServerHeaderSettings", serverReference, StringComparison.Ordinal);
        var serverProperties = schemas["ServerHeaderSettings"]!["properties"]!.AsObject();
        Assert.Equal(["mode", "value"], serverProperties.Select(p => p.Key));
        var mode = serverProperties["mode"]!;
        var modeReference = mode["$ref"]?.GetValue<string>() ?? mode["oneOf"]?[1]?["$ref"]?.GetValue<string>();
        Assert.NotNull(modeReference);
        Assert.Contains("ServerHeaderMode", modeReference, StringComparison.Ordinal);
        Assert.Contains("remove", schemas["ServerHeaderMode"]!["enum"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Contains("128", serverProperties["value"]!["description"]!.GetValue<string>(), StringComparison.Ordinal);

        // The access-log switch is described, and the request ID's destination is its own enum (a $ref carries no description).
        var hostProperties = hostSettings["properties"]!.AsObject();
        Assert.Contains("access log", hostProperties["accessLog"]!["description"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        var propagation = hostProperties["requestIdPropagation"]!;
        var propagationReference = propagation["$ref"]?.GetValue<string>() ?? propagation["oneOf"]?[1]?["$ref"]?.GetValue<string>();
        Assert.NotNull(propagationReference);
        Assert.Contains("off", schemas["RequestIdPropagation"]!["enum"]!.AsArray().Select(v => v!.GetValue<string>()));
    }

    [Fact]
    public async Task TheOpenApiDocumentDescribesTheAuthProviderEndpointsAndTheirPresets()
    {
        var api = shared.Api;

        using var response = await api.GetAsync("/api/v1/openapi.json", caller: null);
        var document = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!.AsObject();
        var paths = document["paths"]!.AsObject();

        Assert.Contains("/api/v1/auth-providers", paths.Select(p => p.Key));
        var operation = paths["/api/v1/auth-providers/{id}"]!["delete"]!;
        Assert.Contains("Refused", operation["description"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("config:write", operation["x-required-permission"]!.GetValue<string>());
        var schemas = document["components"]!["schemas"]!.AsObject();
        var forwardSchema = schemas["ForwardAuthConfig"]!.ToJsonString();
        foreach (var value in (string[])["custom", "authentik", "authelia", "oAuth2Proxy", "request", "host"])
        {
            Assert.Contains($"\"{value}\"", forwardSchema, StringComparison.Ordinal);
        }

        var forward = schemas["ForwardAuthConfig"]!["properties"]!.AsObject();
        Assert.Equal(["url", "preset", "timeoutSeconds", "cacheSeconds", "cacheScope", "requestHeaders", "responseHeaders", "signIn"], forward.Select(p => p.Key));
        Assert.Contains("remembered", forward["cacheSeconds"]!["description"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSharedEnumsKeepTheirSchemaNames()
    {
        // The enums the config document and the proxy share live in Core, but their names are the API contract's: each is a
        // components.schemas key, so a rename here would silently rename a published schema.
        var api = shared.Api;

        using var response = await api.GetAsync("/api/v1/openapi.json", caller: null);
        var document = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!.AsObject();
        var schemas = document["components"]!["schemas"]!.AsObject();

        // Four of them are described as a schema of their own; the other four are inlined where they are used, so their
        // names never reach components.schemas. Both spellings are pinned: either would change the published API.
        string[] names = [.. schemas.Select(s => s.Key)];
        foreach (var name in (string[])["TlsMinimumVersion", "TcpAlpn", "ServerHeaderMode", "RequestIdPropagation"])
        {
            Assert.Contains(name, names);
        }

        Assert.DoesNotContain("UnknownHostMode", names);

        // The inlined one keeps the values a client reads, and the contract's name.
        var unknownHost = schemas["GlobalSettings"]!["properties"]!["unknownHost"]!.AsObject();
        Assert.Equal(
            ["notFound", "misdirectedRequest", "close"],
            unknownHost["enum"]!.AsArray().Select(v => v!.GetValue<string>()));
    }

    [Fact]
    public async Task TheReferenceUiIsAnonymousServesItsScriptLocallyAndNeedsNothingFromOutside()
    {
        var api = shared.Api;

        using var page = await api.GetAsync("/api/docs", caller: null);
        var html = await page.Content.ReadAsStringAsync(Ct);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.StartsWith("text/html", page.Content.Headers.ContentType?.MediaType, StringComparison.Ordinal);

        // The docs' own CSP wins over the default one of the security-headers middleware.
        var csp = string.Join(' ', page.Headers.GetValues("Content-Security-Policy"));
        Assert.Single(page.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("default-src 'none'", csp, StringComparison.Ordinal);
        var cspNonce = Regex.Match(csp, "script-src 'self' 'nonce-([^']+)'");
        Assert.True(cspNonce.Success, csp);
        Assert.DoesNotContain("http", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("'unsafe-eval'", csp, StringComparison.Ordinal);

        // Every script tag, the inline module included, carries the policy's nonce. The HTML encoder escapes characters
        // like '+' (the nonce is base64), so the assertion decodes the page first.
        var decoded = System.Net.WebUtility.HtmlDecode(html);
        var scripts = Regex.Matches(decoded, "<script[^>]*>");
        Assert.NotEmpty(scripts);
        Assert.All(scripts, s => Assert.Contains($"nonce=\"{cspNonce.Groups[1].Value}\"", s.Value, StringComparison.Ordinal));

        // Nothing is fetched from another origin.
        Assert.DoesNotMatch(@"(src|href)\s*=\s*[""']https?://", html);
        Assert.DoesNotContain("cdn.jsdelivr.net", html, StringComparison.Ordinal);
        Assert.DoesNotContain("fonts.googleapis.com", html, StringComparison.Ordinal);
        Assert.DoesNotContain("proxy.scalar.com", html, StringComparison.Ordinal);

        // The script it names is served by this app, and needs no 'unsafe-eval': the bundle neither evals nor builds functions from strings.
        var script = Regex.Match(html, @"<script[^>]*\ssrc=""([^""]+)""");
        Assert.True(script.Success, "the page has no external script reference to check");
        Uri scriptUri = new(page.RequestMessage!.RequestUri!, script.Groups[1].Value);
        Assert.Equal(api.BaseAddress.Authority, scriptUri.Authority);
        Assert.StartsWith("/api/docs/", scriptUri.PathAndQuery, StringComparison.Ordinal);
        using var js = await api.GetAsync(scriptUri.PathAndQuery, caller: null);
        Assert.Equal(HttpStatusCode.OK, js.StatusCode);
        var bundle = await js.Content.ReadAsStringAsync(Ct);
        Assert.True(bundle.Length > 100_000);
        Assert.DoesNotMatch(@"\beval\s*\(", bundle);
        Assert.DoesNotMatch(@"new\s+Function\s*\(", bundle);
    }

    [Fact]
    public async Task AnonymousRequestsToProtectedEndpointsAre401Problems()
    {
        var api = shared.Api;

        using var response = await api.GetAsync("/api/v1/config", caller: null);
        var problem = await AdminApiHarness.ProblemAsync(response, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(401, problem.Status);
        Assert.Equal("auth.required", problem.Code);
    }

    [Fact]
    public async Task HealthEndpointsStayAsTheyAre()
    {
        var api = shared.Api;

        using var live = await api.GetAsync("/healthz", caller: null);
        using var ready = await api.GetAsync("/readyz", caller: null);

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal("text/plain", live.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public async Task ASettingsReadCarriesItsVersionAsETag()
    {
        var api = shared.Api;

        using var response = await api.GetAsync("/api/v1/settings");
        var settings = await AdminApiHarness.ReadAsync(response, Paduraksa.Admin.Json.AdminJsonContext.Default.GlobalSettings, Ct);

        Assert.Equal($"\"{settings.Version}\"", AdminApiHarness.ETag(response));
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task AFreshSettingsDocumentIsVersionZeroAndItsETagSaysSo()
    {
        // Its own host: version 0 is the state of a config nobody has written yet, which a shared host cannot show.
        await using var api = await AdminApiHarness.StartAsync(Ct);

        using var response = await api.GetAsync("/api/v1/settings");
        var fresh = await AdminApiHarness.ReadAsync(response, Paduraksa.Admin.Json.AdminJsonContext.Default.GlobalSettings, Ct);

        Assert.Equal("\"0\"", AdminApiHarness.ETag(response));
        Assert.Equal(0, fresh.Version);
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
        var api = shared.Api;
        const string Body = """{"version":0,"unknownHost":"misdirectedRequest"}""";

        // The tag the API itself issued for the settings it holds (the shared host's version is not 0 by now), and the settings
        // to put back: the good request below changes them, and every other test of the class shares this host.
        using var current = await api.GetAsync("/api/v1/settings");
        var original = await AdminApiHarness.ReadAsync(current, Paduraksa.Admin.Json.AdminJsonContext.Default.GlobalSettings, Ct);

        using var response = await api.SendAsync(HttpMethod.Put, "/api/v1/settings", ifMatch: ifMatch, content: AdminApiHarness.JsonBody(Body));
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.Equal(expected, (await AdminApiHarness.ProblemAsync(response, Ct)).Status);

        using var good = await api.SendAsync(HttpMethod.Put, "/api/v1/settings", ifMatch: AdminApiHarness.ETag(current), content: AdminApiHarness.JsonBody(Body));
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);

        using var changed = await api.GetAsync("/api/v1/settings");
        using var restored = await api.SendJsonAsync(
            HttpMethod.Put, "/api/v1/settings", original, Paduraksa.Admin.Json.AdminJsonContext.Default.GlobalSettings, ifMatch: AdminApiHarness.ETag(changed));
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
    }

    private sealed class ThrowingPermissions : IPermissionEvaluator
    {
        public ValueTask<IReadOnlySet<Permission>> GetPermissionsAsync(Actor actor, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("TOP-SECRET internal detail at /var/lib/paduraksa/vault.db");

        public ValueTask<bool> IsAllowedAsync(CommandOrigin origin, Permission permission, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("TOP-SECRET internal detail at /var/lib/paduraksa/vault.db");
    }
}
