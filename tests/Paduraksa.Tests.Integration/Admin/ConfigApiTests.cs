using System.Net;

using Paduraksa.Admin.Json;
using Paduraksa.Contracts.Api;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Revisions;
using Paduraksa.Core.Model;
using Paduraksa.Tests.Integration.Config;
using Paduraksa.Tests.Integration.Hosting;
using Paduraksa.Tests.Shared;

namespace Paduraksa.Tests.Integration.Admin;

/// <summary>The config resources of the API through the real host: CRUD, concurrency, dry runs, validation, revisions and who may do what.</summary>
public sealed class ConfigApiTests(SharedAdminApi shared) : IClassFixture<SharedAdminApi>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<HttpResponseMessage> PostHostAsync(AdminApiHarness api, HostConfig host, string query = "", string? caller = "owner") =>
        api.SendJsonAsync(HttpMethod.Post, "/api/v1/hosts" + query, host, AdminJsonContext.Default.HostConfig, caller);

    private static async Task<ConfigCommandResult> ResultAsync(HttpResponseMessage response) =>
        await AdminApiHarness.ReadAsync(response, AdminJsonContext.Default.ConfigCommandResult, Ct);

    [Fact]
    public async Task AnAdminHostIsCreatedThroughTheHostsEndpoint()
    {
        AdminApiHarness api = shared.Api;
        HostConfig host = new(TestIds.Host("admin-via-api"), ["admin.test"], Kind: HostKind.Admin);

        using HttpResponseMessage created = await PostHostAsync(api, host);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        ConfigCommandResult result = await ResultAsync(created);
        Assert.Equal(ConfigCommandStatus.Committed, result.Status);

        using HttpResponseMessage read = await api.GetAsync($"/api/v1/hosts/{host.Id}");
        HostConfig stored = await AdminApiHarness.ReadAsync(read, AdminJsonContext.Default.HostConfig, Ct);
        Assert.Equal(HostKind.Admin, stored.Kind);
        Assert.Empty(stored.Routes);
    }

    [Fact]
    public async Task AHostIsCreatedReadReplacedAndDeletedWithItsVersion()
    {
        AdminApiHarness api = shared.Api;
        HostConfig host = ConfigTestSupport.RespondHost("a", "a.test", "ok");

        using HttpResponseMessage created = await PostHostAsync(api, host);
        ConfigCommandResult result = await ResultAsync(created);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal($"/api/v1/hosts/{host.Id}", created.Headers.Location!.ToString());
        Assert.Equal(ConfigCommandStatus.Committed, result.Status);
        string versionTag = AdminApiHarness.ETag(created)!;
        Assert.Equal(AdminApiHarness.ETag(created), $"\"{result.Version}\"");

        using HttpResponseMessage read = await api.GetAsync($"/api/v1/hosts/{host.Id}");
        HostConfig stored = await AdminApiHarness.ReadAsync(read, AdminJsonContext.Default.HostConfig, Ct);
        Assert.Equal(versionTag, AdminApiHarness.ETag(read));
        Assert.Equal(host.Names, stored.Names);

        HostConfig changed = stored with { Respond = new RespondConfig(200, "changed") };
        using HttpResponseMessage noMatch = await api.SendJsonAsync(HttpMethod.Put, $"/api/v1/hosts/{host.Id}", changed, AdminJsonContext.Default.HostConfig);
        Assert.Equal(HttpStatusCode.PreconditionRequired, noMatch.StatusCode);
        Assert.Equal(428, (await AdminApiHarness.ProblemAsync(noMatch, Ct)).Status);

        using HttpResponseMessage stale = await api.SendJsonAsync(HttpMethod.Put, $"/api/v1/hosts/{host.Id}", changed, AdminJsonContext.Default.HostConfig, ifMatch: "\"99\"");
        ApiProblem conflict = await AdminApiHarness.ProblemAsync(stale, Ct);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(result.Version, conflict.Conflict!.CurrentVersion);

        using HttpResponseMessage replaced = await api.SendJsonAsync(HttpMethod.Put, $"/api/v1/hosts/{host.Id}", changed, AdminJsonContext.Default.HostConfig, ifMatch: versionTag);
        ConfigCommandResult second = await ResultAsync(replaced);
        Assert.Equal(ConfigCommandStatus.Committed, second.Status);
        Assert.True(second.Version > result.Version);

        using HttpResponseMessage unmatchedDelete = await api.SendAsync(HttpMethod.Delete, $"/api/v1/hosts/{host.Id}");
        Assert.Equal(HttpStatusCode.PreconditionRequired, unmatchedDelete.StatusCode);
        using HttpResponseMessage deleted = await api.SendAsync(HttpMethod.Delete, $"/api/v1/hosts/{host.Id}", ifMatch: AdminApiHarness.ETag(replaced));
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using HttpResponseMessage gone = await api.GetAsync($"/api/v1/hosts/{host.Id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task ADryRunReturnsThePlanAndCommitsNothing()
    {
        // Its own host: it counts the hosts and revisions the dry run leaves behind, so it must start with none.
        await using AdminApiHarness api = await AdminApiHarness.StartAsync(Ct);
        HostConfig host = ConfigTestSupport.RespondHost("dry", "dry.test", "ok");

        using HttpResponseMessage response = await PostHostAsync(api, host, "?dryRun=true");
        ConfigCommandResult plan = await ResultAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ConfigCommandStatus.DryRun, plan.Status);
        Assert.Contains(plan.Changes, c => c is HostChange { Kind: ConfigChangeKind.Added });
        using HttpResponseMessage hosts = await api.GetAsync("/api/v1/hosts");
        Assert.Empty(await AdminApiHarness.ReadAsync(hosts, AdminJsonContext.Default.IReadOnlyListHostConfig, Ct));
        using HttpResponseMessage revisions = await api.GetAsync("/api/v1/revisions");
        Assert.Empty((await AdminApiHarness.ReadAsync(revisions, AdminJsonContext.Default.PageRevisionSummary, Ct)).Items);

        using HttpResponseMessage bad = await PostHostAsync(api, host, "?dryRun=maybe");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task ValidationFailuresAre422WithTheServicesErrorsAndAnExistingIdIs409()
    {
        AdminApiHarness api = shared.Api;

        using HttpResponseMessage invalid = await PostHostAsync(api, ConfigTestSupport.ProxyHost("p", "p.test", "missing-upstream"));
        ApiProblem problem = await AdminApiHarness.ProblemAsync(invalid, Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        Assert.NotEmpty(problem.Errors!);
        Assert.All(problem.Errors!, e => Assert.False(string.IsNullOrEmpty(e.Code)));

        HostConfig host = ConfigTestSupport.RespondHost("dup", "dup.test", "ok");
        using HttpResponseMessage first = await PostHostAsync(api, host);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        using HttpResponseMessage again = await PostHostAsync(api, host);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.NotNull((await AdminApiHarness.ProblemAsync(again, Ct)).Conflict);

        using HttpResponseMessage mismatch = await api.SendJsonAsync(HttpMethod.Put, $"/api/v1/hosts/{TestIds.Host("other")}", host, AdminJsonContext.Default.HostConfig, ifMatch: "\"1\"");
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
    }

    [Fact]
    public async Task RoutesLiveInsideTheirHostAndUseTheHostsVersion()
    {
        AdminApiHarness api = shared.Api;
        UpstreamConfig upstream = ConfigTestSupport.Upstream("u-routes", "http://127.0.0.1:9");
        using HttpResponseMessage upstreamCreated = await api.SendJsonAsync(HttpMethod.Post, "/api/v1/upstreams", upstream, AdminJsonContext.Default.UpstreamConfig);
        Assert.Equal(HttpStatusCode.Created, upstreamCreated.StatusCode);
        HostConfig host = ConfigTestSupport.ProxyHost("h-routes", "h-routes.test", "u-routes");
        using HttpResponseMessage hostCreated = await PostHostAsync(api, host);
        string version = AdminApiHarness.ETag(hostCreated)!;

        RouteConfig route = new(TestIds.Route("h-routes-api"), upstream.Id, Path: "/api");
        using HttpResponseMessage missingMatch = await api.SendJsonAsync(HttpMethod.Put, $"/api/v1/hosts/{host.Id}/routes/{route.Id}", route, AdminJsonContext.Default.RouteConfig);
        Assert.Equal(HttpStatusCode.PreconditionRequired, missingMatch.StatusCode);
        using HttpResponseMessage added = await api.SendJsonAsync(HttpMethod.Put, $"/api/v1/hosts/{host.Id}/routes/{route.Id}", route, AdminJsonContext.Default.RouteConfig, ifMatch: version);
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        Assert.Equal($"/api/v1/hosts/{host.Id}/routes/{route.Id}", added.Headers.Location!.ToString());
        Assert.Equal(ConfigCommandStatus.Committed, (await ResultAsync(added)).Status);

        using HttpResponseMessage list = await api.GetAsync($"/api/v1/hosts/{host.Id}/routes");
        Assert.Equal(2, (await AdminApiHarness.ReadAsync(list, AdminJsonContext.Default.IReadOnlyListRouteConfig, Ct)).Count);
        using HttpResponseMessage one = await api.GetAsync($"/api/v1/hosts/{host.Id}/routes/{route.Id}");
        Assert.Equal("/api", (await AdminApiHarness.ReadAsync(one, AdminJsonContext.Default.RouteConfig, Ct)).Path);

        using HttpResponseMessage removed = await api.SendAsync(HttpMethod.Delete, $"/api/v1/hosts/{host.Id}/routes/{route.Id}", ifMatch: AdminApiHarness.ETag(added));
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        using HttpResponseMessage gone = await api.GetAsync($"/api/v1/hosts/{host.Id}/routes/{route.Id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task UpstreamsAndProfilesAreManagedAndAnUpstreamInUseCannotBeDeleted()
    {
        // Its own host: it asserts the lists hold exactly what it created, so it must start with none.
        await using AdminApiHarness api = await AdminApiHarness.StartAsync(Ct);
        UpstreamConfig upstream = ConfigTestSupport.Upstream("u", "http://127.0.0.1:9");
        using HttpResponseMessage createdUpstream = await api.SendJsonAsync(HttpMethod.Post, "/api/v1/upstreams", upstream, AdminJsonContext.Default.UpstreamConfig);
        string upstreamVersion = AdminApiHarness.ETag(createdUpstream)!;
        ProfileConfig profile = new(ProfileId.New(), "strict");
        using HttpResponseMessage createdProfile = await api.SendJsonAsync(HttpMethod.Post, "/api/v1/profiles", profile, AdminJsonContext.Default.ProfileConfig);
        Assert.Equal(HttpStatusCode.Created, createdProfile.StatusCode);
        using HttpResponseMessage proxied = await PostHostAsync(api, ConfigTestSupport.ProxyHost("h", "h.test", "u"));
        Assert.Equal(HttpStatusCode.Created, proxied.StatusCode);

        using HttpResponseMessage inUse = await api.SendAsync(HttpMethod.Delete, $"/api/v1/upstreams/{upstream.Id}", ifMatch: upstreamVersion);
        ApiProblem problem = await AdminApiHarness.ProblemAsync(inUse, Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, inUse.StatusCode);
        Assert.NotEmpty(problem.RelatedErrors!);

        using HttpResponseMessage profiles = await api.GetAsync("/api/v1/profiles");
        Assert.Single(await AdminApiHarness.ReadAsync(profiles, AdminJsonContext.Default.IReadOnlyListProfileConfig, Ct));
        using HttpResponseMessage upstreams = await api.GetAsync("/api/v1/upstreams");
        Assert.Single(await AdminApiHarness.ReadAsync(upstreams, AdminJsonContext.Default.IReadOnlyListUpstreamConfig, Ct));
        using HttpResponseMessage profileGone = await api.SendAsync(HttpMethod.Delete, $"/api/v1/profiles/{profile.Id}", ifMatch: AdminApiHarness.ETag(createdProfile));
        Assert.Equal(HttpStatusCode.NoContent, profileGone.StatusCode);
    }

    [Fact]
    public async Task SettingsNeedTheirVersion()
    {
        AdminApiHarness api = shared.Api;
        using HttpResponseMessage read = await api.GetAsync("/api/v1/settings");
        GlobalSettings settings = await AdminApiHarness.ReadAsync(read, AdminJsonContext.Default.GlobalSettings, Ct);
        GlobalSettings changed = settings with { UnknownHost = UnknownHostBehaviour.MisdirectedRequest };

        using HttpResponseMessage without = await api.SendJsonAsync(HttpMethod.Put, "/api/v1/settings", changed, AdminJsonContext.Default.GlobalSettings);
        Assert.Equal(HttpStatusCode.PreconditionRequired, without.StatusCode);
        using HttpResponseMessage with = await api.SendJsonAsync(HttpMethod.Put, "/api/v1/settings", changed, AdminJsonContext.Default.GlobalSettings, ifMatch: AdminApiHarness.ETag(read));
        Assert.Equal(HttpStatusCode.OK, with.StatusCode);
        using HttpResponseMessage stale = await api.SendJsonAsync(HttpMethod.Put, "/api/v1/settings", changed, AdminJsonContext.Default.GlobalSettings, ifMatch: AdminApiHarness.ETag(read));
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);

        // The request above changed the class's global settings; put them back, so a test that runs later on this shared host
        // sees the config it started with.
        using HttpResponseMessage now = await api.GetAsync("/api/v1/settings");
        using HttpResponseMessage restored = await api.SendJsonAsync(HttpMethod.Put, "/api/v1/settings", settings, AdminJsonContext.Default.GlobalSettings, ifMatch: AdminApiHarness.ETag(now));
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
    }

    [Fact]
    public async Task RevisionsPageDiffAndRollBack()
    {
        // Its own host: it pages through every revision and rolls back to one, so it must start with none.
        await using AdminApiHarness api = await AdminApiHarness.StartAsync(Ct);
        List<ConfigRevisionId> ids = [];
        foreach (string label in new[] { "r1", "r2", "r3" })
        {
            using HttpResponseMessage created = await PostHostAsync(api, ConfigTestSupport.RespondHost(label, $"{label}.test", "ok"));
            ids.Add((await ResultAsync(created)).Revision!.Id);
        }

        using HttpResponseMessage first = await api.GetAsync("/api/v1/revisions?limit=2");
        Page<RevisionSummary> page = await AdminApiHarness.ReadAsync(first, AdminJsonContext.Default.PageRevisionSummary, Ct);
        Assert.Equal([ids[2], ids[1]], page.Items.Select(r => r.Id));
        Assert.NotNull(page.NextCursor);
        using HttpResponseMessage second = await api.GetAsync($"/api/v1/revisions?limit=2&cursor={page.NextCursor}");
        Page<RevisionSummary> rest = await AdminApiHarness.ReadAsync(second, AdminJsonContext.Default.PageRevisionSummary, Ct);
        Assert.Equal([ids[0]], rest.Items.Select(r => r.Id));
        Assert.Null(rest.NextCursor);
        foreach (string bad in new[] { "limit=0", "limit=9999", "cursor=garbage" })
        {
            using HttpResponseMessage rejected = await api.GetAsync("/api/v1/revisions?" + bad);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }

        using HttpResponseMessage detail = await api.GetAsync($"/api/v1/revisions/{ids[1]}");
        RevisionDetail revision = await AdminApiHarness.ReadAsync(detail, AdminJsonContext.Default.RevisionDetail, Ct);
        Assert.Equal(2, revision.Document.Hosts.Count);
        Assert.Equal(ids[0], revision.Revision.ParentId);
        using HttpResponseMessage diff = await api.GetAsync($"/api/v1/revisions/diff?from={ids[0]}&to={ids[2]}");
        Assert.Equal(2, (await AdminApiHarness.ReadAsync(diff, AdminJsonContext.Default.RevisionDiff, Ct)).Changes.Count(c => c is HostChange));
        using HttpResponseMessage noDiff = await api.GetAsync($"/api/v1/revisions/diff?from={ids[0]}");
        Assert.Equal(HttpStatusCode.BadRequest, noDiff.StatusCode);

        using HttpResponseMessage current = await api.GetAsync("/api/v1/config");
        string latest = AdminApiHarness.ETag(current)!;
        Assert.Equal($"\"{ids[2]}\"", latest);
        using HttpResponseMessage noMatch = await api.SendAsync(HttpMethod.Post, $"/api/v1/revisions/{ids[0]}/rollback");
        Assert.Equal(HttpStatusCode.PreconditionRequired, noMatch.StatusCode);
        using HttpResponseMessage stale = await api.SendAsync(HttpMethod.Post, $"/api/v1/revisions/{ids[0]}/rollback", ifMatch: $"\"{ids[0]}\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        using HttpResponseMessage dry = await api.SendAsync(HttpMethod.Post, $"/api/v1/revisions/{ids[0]}/rollback?dryRun=true", ifMatch: latest);
        Assert.Equal(ConfigCommandStatus.DryRun, (await ResultAsync(dry)).Status);
        using HttpResponseMessage rolledBack = await api.SendAsync(HttpMethod.Post, $"/api/v1/revisions/{ids[0]}/rollback", ifMatch: latest);
        Assert.Equal(ConfigCommandStatus.Committed, (await ResultAsync(rolledBack)).Status);
        using HttpResponseMessage hosts = await api.GetAsync("/api/v1/hosts");
        Assert.Single(await AdminApiHarness.ReadAsync(hosts, AdminJsonContext.Default.IReadOnlyListHostConfig, Ct));
        using HttpResponseMessage missing = await api.GetAsync($"/api/v1/revisions/{ConfigRevisionId.New()}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Theory]
    [InlineData("viewer", "GET", "/api/v1/config", 200)]
    [InlineData("viewer", "POST", "/api/v1/hosts", 403)]
    [InlineData("viewer", "GET", "/api/v1/audit", 403)]
    [InlineData("viewer", "POST", "/api/v1/certificates/self-signed", 403)]
    [InlineData("viewer", "GET", "/api/v1/dns-providers", 200)]
    [InlineData("operator", "POST", "/api/v1/hosts", 400)] // allowed in: the empty body is the problem, not the role
    [InlineData("operator", "POST", "/api/v1/dns-providers", 403)]
    [InlineData("operator", "GET", "/api/v1/audit", 403)]
    [InlineData("auditor", "GET", "/api/v1/audit", 200)]
    [InlineData("auditor", "GET", "/api/v1/audit/verify", 200)]
    [InlineData("auditor", "POST", "/api/v1/simulate", 403)]
    [InlineData("auditor", "POST", "/api/v1/hosts", 403)]
    [InlineData("admin", "GET", "/api/v1/audit", 200)]
    [InlineData("admin", "POST", "/api/v1/dns-providers", 400)]
    [InlineData("owner", "GET", "/api/v1/acme-accounts", 200)]
    [InlineData(null, "GET", "/api/v1/hosts", 401)]
    [InlineData(null, "POST", "/api/v1/simulate", 401)]
    public async Task EachRoleGetsWhatItsPermissionsAllow(string? role, string method, string path, int expected)
    {
        AdminApiHarness api = shared.Api;

        using HttpResponseMessage response = await api.SendAsync(new HttpMethod(method), path, role, content: method == "POST" ? AdminApiHarness.JsonBody(string.Empty) : null);

        Assert.Equal(expected, (int)response.StatusCode);
        if (expected >= 400)
        {
            Assert.Equal(expected, (await AdminApiHarness.ProblemAsync(response, Ct)).Status);
        }
    }
}
