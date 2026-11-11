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
        var api = shared.Api;
        HostConfig host = new(TestIds.Host("admin-via-api"), ["admin.test"], Kind: HostKind.Admin);

        using var created = await PostHostAsync(api, host);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var result = await ResultAsync(created);
        Assert.Equal(ConfigCommandStatus.Committed, result.Status);

        using var read = await api.GetAsync($"/api/v1/hosts/{host.Id}");
        var stored = await AdminApiHarness.ReadAsync(read, AdminJsonContext.Default.HostConfig, Ct);
        Assert.Equal(HostKind.Admin, stored.Kind);
        Assert.Empty(stored.Routes);
    }

    [Fact]
    public async Task AHostIsCreatedReadReplacedAndDeletedWithItsVersion()
    {
        var api = shared.Api;
        var host = ConfigTestSupport.RespondHost("a", "a.test", "ok");

        using var created = await PostHostAsync(api, host);
        var result = await ResultAsync(created);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal($"/api/v1/hosts/{host.Id}", created.Headers.Location!.ToString());
        Assert.Equal(ConfigCommandStatus.Committed, result.Status);
        var versionTag = AdminApiHarness.ETag(created)!;
        Assert.Equal(AdminApiHarness.ETag(created), $"\"{result.Version}\"");

        using var read = await api.GetAsync($"/api/v1/hosts/{host.Id}");
        var stored = await AdminApiHarness.ReadAsync(read, AdminJsonContext.Default.HostConfig, Ct);
        Assert.Equal(versionTag, AdminApiHarness.ETag(read));
        Assert.Equal(host.Names, stored.Names);

        var changed = stored with { Respond = new RespondConfig(200, "changed") };
        using var noMatch = await api.SendJsonAsync(HttpMethod.Put, $"/api/v1/hosts/{host.Id}", changed, AdminJsonContext.Default.HostConfig);
        Assert.Equal(HttpStatusCode.PreconditionRequired, noMatch.StatusCode);
        Assert.Equal(428, (await AdminApiHarness.ProblemAsync(noMatch, Ct)).Status);

        using var stale = await api.SendJsonAsync(HttpMethod.Put, $"/api/v1/hosts/{host.Id}", changed, AdminJsonContext.Default.HostConfig, ifMatch: "\"99\"");
        var conflict = await AdminApiHarness.ProblemAsync(stale, Ct);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(result.Version, conflict.Conflict!.CurrentVersion);

        using var replaced = await api.SendJsonAsync(HttpMethod.Put, $"/api/v1/hosts/{host.Id}", changed, AdminJsonContext.Default.HostConfig, ifMatch: versionTag);
        var second = await ResultAsync(replaced);
        Assert.Equal(ConfigCommandStatus.Committed, second.Status);
        Assert.True(second.Version > result.Version);

        using var unmatchedDelete = await api.SendAsync(HttpMethod.Delete, $"/api/v1/hosts/{host.Id}");
        Assert.Equal(HttpStatusCode.PreconditionRequired, unmatchedDelete.StatusCode);
        using var deleted = await api.SendAsync(HttpMethod.Delete, $"/api/v1/hosts/{host.Id}", ifMatch: AdminApiHarness.ETag(replaced));
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var gone = await api.GetAsync($"/api/v1/hosts/{host.Id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task ADryRunReturnsThePlanAndCommitsNothing()
    {
        // Its own host: it counts the hosts and revisions the dry run leaves behind, so it must start with none.
        await using var api = await AdminApiHarness.StartAsync(Ct);
        var host = ConfigTestSupport.RespondHost("dry", "dry.test", "ok");

        using var response = await PostHostAsync(api, host, "?dryRun=true");
        var plan = await ResultAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ConfigCommandStatus.DryRun, plan.Status);
        Assert.Contains(plan.Changes, c => c is HostChange { Kind: ConfigChangeKind.Added });
        using var hosts = await api.GetAsync("/api/v1/hosts");
        Assert.Empty(await AdminApiHarness.ReadAsync(hosts, AdminJsonContext.Default.IReadOnlyListHostConfig, Ct));
        using var revisions = await api.GetAsync("/api/v1/revisions");
        Assert.Empty((await AdminApiHarness.ReadAsync(revisions, AdminJsonContext.Default.PageRevisionSummary, Ct)).Items);

        using var bad = await PostHostAsync(api, host, "?dryRun=maybe");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task ValidationFailuresAre422WithTheServicesErrorsAndAnExistingIdIs409()
    {
        var api = shared.Api;

        using var invalid = await PostHostAsync(api, ConfigTestSupport.ProxyHost("p", "p.test", "missing-upstream"));
        var problem = await AdminApiHarness.ProblemAsync(invalid, Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        Assert.NotEmpty(problem.Errors!);
        Assert.All(problem.Errors!, e => Assert.False(string.IsNullOrEmpty(e.Code)));

        var host = ConfigTestSupport.RespondHost("dup", "dup.test", "ok");
        using var first = await PostHostAsync(api, host);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        using var again = await PostHostAsync(api, host);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.NotNull((await AdminApiHarness.ProblemAsync(again, Ct)).Conflict);

        using var mismatch = await api.SendJsonAsync(HttpMethod.Put, $"/api/v1/hosts/{TestIds.Host("other")}", host, AdminJsonContext.Default.HostConfig, ifMatch: "\"1\"");
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
    }

    [Fact]
    public async Task RoutesLiveInsideTheirHostAndUseTheHostsVersion()
    {
        var api = shared.Api;
        var upstream = ConfigTestSupport.Upstream("u-routes", "http://127.0.0.1:9");
        using var upstreamCreated = await api.SendJsonAsync(HttpMethod.Post, "/api/v1/upstreams", upstream, AdminJsonContext.Default.UpstreamConfig);
        Assert.Equal(HttpStatusCode.Created, upstreamCreated.StatusCode);
        var host = ConfigTestSupport.ProxyHost("h-routes", "h-routes.test", "u-routes");
        using var hostCreated = await PostHostAsync(api, host);
        var version = AdminApiHarness.ETag(hostCreated)!;

        RouteConfig route = new(TestIds.Route("h-routes-api"), upstream.Id, Path: "/api");
        using var missingMatch = await api.SendJsonAsync(HttpMethod.Put, $"/api/v1/hosts/{host.Id}/routes/{route.Id}", route, AdminJsonContext.Default.RouteConfig);
        Assert.Equal(HttpStatusCode.PreconditionRequired, missingMatch.StatusCode);
        using var added = await api.SendJsonAsync(HttpMethod.Put, $"/api/v1/hosts/{host.Id}/routes/{route.Id}", route, AdminJsonContext.Default.RouteConfig, ifMatch: version);
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        Assert.Equal($"/api/v1/hosts/{host.Id}/routes/{route.Id}", added.Headers.Location!.ToString());
        Assert.Equal(ConfigCommandStatus.Committed, (await ResultAsync(added)).Status);

        using var list = await api.GetAsync($"/api/v1/hosts/{host.Id}/routes");
        Assert.Equal(2, (await AdminApiHarness.ReadAsync(list, AdminJsonContext.Default.IReadOnlyListRouteConfig, Ct)).Count);
        using var one = await api.GetAsync($"/api/v1/hosts/{host.Id}/routes/{route.Id}");
        Assert.Equal("/api", (await AdminApiHarness.ReadAsync(one, AdminJsonContext.Default.RouteConfig, Ct)).Path);

        using var removed = await api.SendAsync(HttpMethod.Delete, $"/api/v1/hosts/{host.Id}/routes/{route.Id}", ifMatch: AdminApiHarness.ETag(added));
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        using var gone = await api.GetAsync($"/api/v1/hosts/{host.Id}/routes/{route.Id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task UpstreamsAndProfilesAreManagedAndAnUpstreamInUseCannotBeDeleted()
    {
        var api = shared.Api;
        var upstream = ConfigTestSupport.Upstream("u-profiles", "http://127.0.0.1:9");
        using var createdUpstream = await api.SendJsonAsync(HttpMethod.Post, "/api/v1/upstreams", upstream, AdminJsonContext.Default.UpstreamConfig);
        var upstreamVersion = AdminApiHarness.ETag(createdUpstream)!;
        ProfileConfig profile = new(ProfileId.New(), "strict-profiles");
        using var createdProfile = await api.SendJsonAsync(HttpMethod.Post, "/api/v1/profiles", profile, AdminJsonContext.Default.ProfileConfig);
        Assert.Equal(HttpStatusCode.Created, createdProfile.StatusCode);
        using var proxied = await PostHostAsync(api, ConfigTestSupport.ProxyHost("h-profiles", "h-profiles.test", "u-profiles"));
        Assert.Equal(HttpStatusCode.Created, proxied.StatusCode);

        using var inUse = await api.SendAsync(HttpMethod.Delete, $"/api/v1/upstreams/{upstream.Id}", ifMatch: upstreamVersion);
        var problem = await AdminApiHarness.ProblemAsync(inUse, Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, inUse.StatusCode);
        Assert.NotEmpty(problem.RelatedErrors!);

        using var profiles = await api.GetAsync("/api/v1/profiles");
        Assert.Single(await AdminApiHarness.ReadAsync(profiles, AdminJsonContext.Default.IReadOnlyListProfileConfig, Ct));
        using var upstreams = await api.GetAsync("/api/v1/upstreams");
        Assert.Contains(await AdminApiHarness.ReadAsync(upstreams, AdminJsonContext.Default.IReadOnlyListUpstreamConfig, Ct), u => u.Id == upstream.Id);
        using var profileGone = await api.SendAsync(HttpMethod.Delete, $"/api/v1/profiles/{profile.Id}", ifMatch: AdminApiHarness.ETag(createdProfile));
        Assert.Equal(HttpStatusCode.NoContent, profileGone.StatusCode);
    }

    [Fact]
    public async Task RateLimitPoliciesAreManagedAndOneInUseCannotBeDeleted()
    {
        var api = shared.Api;
        var upstream = ConfigTestSupport.Upstream("u-rates", "http://127.0.0.1:9");
        using var createdUpstream = await api.SendJsonAsync(HttpMethod.Post, "/api/v1/upstreams", upstream, AdminJsonContext.Default.UpstreamConfig);
        Assert.Equal(HttpStatusCode.Created, createdUpstream.StatusCode);

        RateLimitPolicyConfig policy = new(RateLimitPolicyId.New(), "api-budget", Requests: 30, Per: RateLimitPeriod.Second, Burst: 60);
        using var created = await api.SendJsonAsync(HttpMethod.Post, "/api/v1/rateLimitPolicies", policy, AdminJsonContext.Default.RateLimitPolicyConfig);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal($"/api/v1/rateLimitPolicies/{policy.Id}", created.Headers.Location!.ToString());
        var version = AdminApiHarness.ETag(created)!;

        using var read = await api.GetAsync($"/api/v1/rateLimitPolicies/{policy.Id}");
        var stored = await AdminApiHarness.ReadAsync(read, AdminJsonContext.Default.RateLimitPolicyConfig, Ct);
        Assert.Equal(30, stored.Requests);
        Assert.Equal(version, AdminApiHarness.ETag(read));
        using var list = await api.GetAsync("/api/v1/rateLimitPolicies");
        Assert.Contains(await AdminApiHarness.ReadAsync(list, AdminJsonContext.Default.IReadOnlyListRateLimitPolicyConfig, Ct), p => p.Id == policy.Id);

        using var replaced = await api.SendJsonAsync(
            HttpMethod.Put, $"/api/v1/rateLimitPolicies/{policy.Id}", stored with { Burst = 10 }, AdminJsonContext.Default.RateLimitPolicyConfig, ifMatch: version);
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        var latest = AdminApiHarness.ETag(replaced)!;

        HostConfig host = ConfigTestSupport.ProxyHost("h-rates", "h-rates.test", "u-rates") with
        {
            Settings = new HostSettings { RateLimit = new RateLimitSettings { PolicyId = policy.Id } },
        };
        using var hostCreated = await PostHostAsync(api, host);
        Assert.Equal(HttpStatusCode.Created, hostCreated.StatusCode);

        using var inUse = await api.SendAsync(HttpMethod.Delete, $"/api/v1/rateLimitPolicies/{policy.Id}", ifMatch: latest);
        var problem = await AdminApiHarness.ProblemAsync(inUse, Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, inUse.StatusCode);
        Assert.NotEmpty(problem.RelatedErrors!);

        using var hostGone = await api.SendAsync(HttpMethod.Delete, $"/api/v1/hosts/{host.Id}", ifMatch: AdminApiHarness.ETag(hostCreated));
        Assert.Equal(HttpStatusCode.NoContent, hostGone.StatusCode);
        using var policyGone = await api.SendAsync(HttpMethod.Delete, $"/api/v1/rateLimitPolicies/{policy.Id}", ifMatch: latest);
        Assert.Equal(HttpStatusCode.NoContent, policyGone.StatusCode);
        using var gone = await api.GetAsync($"/api/v1/rateLimitPolicies/{policy.Id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);

        using var upstreamGone = await api.SendAsync(HttpMethod.Delete, $"/api/v1/upstreams/{upstream.Id}", ifMatch: AdminApiHarness.ETag(createdUpstream));
        Assert.Equal(HttpStatusCode.NoContent, upstreamGone.StatusCode);
    }

    [Fact]
    public async Task AccessListsAreManagedPasswordsGoToTheVaultAndOneInUseCannotBeDeleted()
    {
        var api = shared.Api;
        var upstream = ConfigTestSupport.Upstream("u-lists", "http://127.0.0.1:9");
        using var createdUpstream = await api.SendJsonAsync(HttpMethod.Post, "/api/v1/upstreams", upstream, AdminJsonContext.Default.UpstreamConfig);
        Assert.Equal(HttpStatusCode.Created, createdUpstream.StatusCode);

        AccessListConfig list = new(
            AccessListId.New(),
            "office",
            Ip: new AccessListIp(["10.0.0.0/8"]),
            BasicAuth: new AccessListBasicAuth(Users: [new AccessListUserConfig("alice", Password: "correct horse battery staple")]));
        using var created = await api.SendJsonAsync(HttpMethod.Post, "/api/v1/accessLists", list, AdminJsonContext.Default.AccessListConfig);
        var createdBody = await created.Content.ReadAsStringAsync(Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal($"/api/v1/accessLists/{list.Id}", created.Headers.Location!.ToString());
        Assert.DoesNotContain("correct horse", createdBody, StringComparison.Ordinal);
        var version = AdminApiHarness.ETag(created)!;

        // What comes back names the vault's credential: no password, no hash, and not even a "password" member.
        using var read = await api.GetAsync($"/api/v1/accessLists/{list.Id}");
        var readBody = await read.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("correct horse", readBody, StringComparison.Ordinal);
        Assert.DoesNotContain("\"password\"", readBody, StringComparison.Ordinal);
        Assert.DoesNotContain("pbkdf2", readBody, StringComparison.Ordinal);
        Assert.DoesNotContain("passwordHash", readBody, StringComparison.Ordinal);
        Assert.Contains("\"credentialId\":\"pwc_", readBody, StringComparison.Ordinal);
        Assert.Equal(version, AdminApiHarness.ETag(read));
        var stored = System.Text.Json.JsonSerializer.Deserialize(readBody, AdminJsonContext.Default.AccessListConfig)!;
        var credential = Assert.Single(stored.BasicAuth!.Users).CredentialId;
        using var all = await api.GetAsync("/api/v1/accessLists");
        Assert.Contains(await AdminApiHarness.ReadAsync(all, AdminJsonContext.Default.IReadOnlyListAccessListConfig, Ct), l => l.Id == list.Id);

        // An edit that names the user without a password keeps the credential.
        var edit = stored with { BasicAuth = new AccessListBasicAuth(Users: [new AccessListUserConfig("alice")]), Ip = new AccessListIp(["10.0.0.0/8", "192.168.0.0/16"]) };
        using var replaced = await api.SendJsonAsync(HttpMethod.Put, $"/api/v1/accessLists/{list.Id}", edit, AdminJsonContext.Default.AccessListConfig, ifMatch: version);
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        var latest = AdminApiHarness.ETag(replaced)!;
        using var reread = await api.GetAsync($"/api/v1/accessLists/{list.Id}");
        Assert.Equal(credential, Assert.Single((await AdminApiHarness.ReadAsync(reread, AdminJsonContext.Default.AccessListConfig, Ct)).BasicAuth!.Users).CredentialId);

        HostConfig host = ConfigTestSupport.ProxyHost("h-lists", "h-lists.test", "u-lists") with
        {
            Settings = new HostSettings { AccessList = new AccessListSettings { AccessListId = list.Id } },
        };
        using var hostCreated = await PostHostAsync(api, host);
        Assert.Equal(HttpStatusCode.Created, hostCreated.StatusCode);

        using var inUse = await api.SendAsync(HttpMethod.Delete, $"/api/v1/accessLists/{list.Id}", ifMatch: latest);
        var problem = await AdminApiHarness.ProblemAsync(inUse, Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, inUse.StatusCode);
        Assert.Contains(problem.RelatedErrors!, e => e.Code == "host.accessList.unknown");

        using var hostGone = await api.SendAsync(HttpMethod.Delete, $"/api/v1/hosts/{host.Id}", ifMatch: AdminApiHarness.ETag(hostCreated));
        Assert.Equal(HttpStatusCode.NoContent, hostGone.StatusCode);
        using var listGone = await api.SendAsync(HttpMethod.Delete, $"/api/v1/accessLists/{list.Id}", ifMatch: latest);
        Assert.Equal(HttpStatusCode.NoContent, listGone.StatusCode);
        using var gone = await api.GetAsync($"/api/v1/accessLists/{list.Id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);

        using var upstreamGone = await api.SendAsync(HttpMethod.Delete, $"/api/v1/upstreams/{upstream.Id}", ifMatch: AdminApiHarness.ETag(createdUpstream));
        Assert.Equal(HttpStatusCode.NoContent, upstreamGone.StatusCode);
    }

    [Fact]
    public async Task RuleSetsAreManagedPresetsAreReadOnlyAndOneInUseCannotBeDeleted()
    {
        var api = shared.Api;
        var upstream = ConfigTestSupport.Upstream("u-rules", "http://127.0.0.1:9");
        using var createdUpstream = await api.SendJsonAsync(HttpMethod.Post, "/api/v1/upstreams", upstream, AdminJsonContext.Default.UpstreamConfig);
        Assert.Equal(HttpStatusCode.Created, createdUpstream.StatusCode);

        RuleSetConfig set = new(
            RuleSetId.New(),
            "block-admin",
            Rules:
            [
                new RuleConfig(RuleAction.Allow, new RuleMatchConfig(new RulePathConfig(RulePathKind.Prefix, "/admin/public"))),
                new RuleConfig(RuleAction.Deny, new RuleMatchConfig(new RulePathConfig(RulePathKind.Prefix, "/admin"), ["POST", "get"], new RuleUserAgentConfig(["sqlmap"], Empty: true))),
            ]);
        using var created = await api.SendJsonAsync(HttpMethod.Post, "/api/v1/ruleSets", set, AdminJsonContext.Default.RuleSetConfig);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal($"/api/v1/ruleSets/{set.Id}", created.Headers.Location!.ToString());
        var version = AdminApiHarness.ETag(created)!;

        // Read back canonically: the enums by name, the methods upper case and sorted.
        using var read = await api.GetAsync($"/api/v1/ruleSets/{set.Id}");
        var readBody = await read.Content.ReadAsStringAsync(Ct);
        Assert.Contains("\"action\":\"deny\"", readBody, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"prefix\"", readBody, StringComparison.Ordinal);
        Assert.Contains("\"methods\":[\"GET\",\"POST\"]", readBody, StringComparison.Ordinal);
        Assert.Equal(version, AdminApiHarness.ETag(read));
        var stored = System.Text.Json.JsonSerializer.Deserialize(readBody, AdminJsonContext.Default.RuleSetConfig)!;
        using var all = await api.GetAsync("/api/v1/ruleSets");
        Assert.Contains(await AdminApiHarness.ReadAsync(all, AdminJsonContext.Default.IReadOnlyListRuleSetConfig, Ct), r => r.Id == set.Id);

        // The built-in presets are read-only: listed on their own, readable by ID, never written.
        using var presets = await api.GetAsync("/api/v1/ruleSets/presets");
        var presetList = await AdminApiHarness.ReadAsync(presets, AdminJsonContext.Default.IReadOnlyListRuleSetConfig, Ct);
        Assert.Equal(["Block AI crawlers", "Block known scanners", "Block empty user agent"], presetList.Select(p => p.Name));
        Assert.DoesNotContain(await AdminApiHarness.ReadAsync(await api.GetAsync("/api/v1/ruleSets"), AdminJsonContext.Default.IReadOnlyListRuleSetConfig, Ct), r => RuleSetPresets.IsPreset(r.Id));
        using var presetRead = await api.GetAsync($"/api/v1/ruleSets/{RuleSetPresets.KnownScanners.Id}");
        Assert.Equal(HttpStatusCode.OK, presetRead.StatusCode);
        using var presetWrite = await api.SendJsonAsync(HttpMethod.Post, "/api/v1/ruleSets", RuleSetPresets.AiCrawlers, AdminJsonContext.Default.RuleSetConfig);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, presetWrite.StatusCode);
        Assert.Equal("ruleSet.id.reserved", Assert.Single((await AdminApiHarness.ProblemAsync(presetWrite, Ct)).Errors!).Code);

        // A regex that cannot be built is a 422 pointing at its field.
        RuleSetConfig broken = new(RuleSetId.New(), "broken", Rules: [new RuleConfig(RuleAction.Deny, new RuleMatchConfig(new RulePathConfig(RulePathKind.Regex, "(unclosed")))]);
        using var rejected = await api.SendJsonAsync(HttpMethod.Post, "/api/v1/ruleSets", broken, AdminJsonContext.Default.RuleSetConfig);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        var error = Assert.Single((await AdminApiHarness.ProblemAsync(rejected, Ct)).Errors!);
        Assert.Equal("/rules/0/match/path/pattern", error.Path);
        Assert.Equal("ruleSet.rule.path.regex.invalid", error.Code);

        using var replaced = await api.SendJsonAsync(HttpMethod.Put, $"/api/v1/ruleSets/{set.Id}", stored with { Notes = "edited" }, AdminJsonContext.Default.RuleSetConfig, ifMatch: version);
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        var latest = AdminApiHarness.ETag(replaced)!;

        // A host may name the set or a preset; naming one that exists nowhere is refused.
        HostConfig host = ConfigTestSupport.ProxyHost("h-rules", "h-rules.test", "u-rules") with
        {
            Settings = new HostSettings { RuleSet = new RuleSetSettings { RuleSetId = set.Id } },
        };
        using var hostCreated = await PostHostAsync(api, host);
        Assert.Equal(HttpStatusCode.Created, hostCreated.StatusCode);

        using var inUse = await api.SendAsync(HttpMethod.Delete, $"/api/v1/ruleSets/{set.Id}", ifMatch: latest);
        var problem = await AdminApiHarness.ProblemAsync(inUse, Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, inUse.StatusCode);
        Assert.Contains(problem.RelatedErrors!, e => e.Code == "host.ruleSet.unknown");

        using var hostGone = await api.SendAsync(HttpMethod.Delete, $"/api/v1/hosts/{host.Id}", ifMatch: AdminApiHarness.ETag(hostCreated));
        Assert.Equal(HttpStatusCode.NoContent, hostGone.StatusCode);
        using var setGone = await api.SendAsync(HttpMethod.Delete, $"/api/v1/ruleSets/{set.Id}", ifMatch: latest);
        Assert.Equal(HttpStatusCode.NoContent, setGone.StatusCode);
        using var gone = await api.GetAsync($"/api/v1/ruleSets/{set.Id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);

        using var upstreamGone = await api.SendAsync(HttpMethod.Delete, $"/api/v1/upstreams/{upstream.Id}", ifMatch: AdminApiHarness.ETag(createdUpstream));
        Assert.Equal(HttpStatusCode.NoContent, upstreamGone.StatusCode);
    }

    [Fact]
    public async Task AWeakOrEmptyPasswordIsAValidationFailureThatStoresNothing()
    {
        var api = shared.Api;
        AccessListConfig list = new(AccessListId.New(), "bad-passwords", BasicAuth: new AccessListBasicAuth(Users: [new AccessListUserConfig("alice", Password: string.Empty)]));

        using var rejected = await api.SendJsonAsync(HttpMethod.Post, "/api/v1/accessLists", list, AdminJsonContext.Default.AccessListConfig);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        Assert.Contains("accessList.user.password.empty", await rejected.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        using var gone = await api.GetAsync($"/api/v1/accessLists/{list.Id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task AuthProvidersAreManagedAPresetIsReadBackByNameAndOneInUseCannotBeDeleted()
    {
        var api = shared.Api;
        var upstream = ConfigTestSupport.Upstream("u-auth", "http://127.0.0.1:9");
        using var createdUpstream = await api.SendJsonAsync(HttpMethod.Post, "/api/v1/upstreams", upstream, AdminJsonContext.Default.UpstreamConfig);
        Assert.Equal(HttpStatusCode.Created, createdUpstream.StatusCode);

        AuthProviderConfig provider = new(
            AuthProviderId.New(),
            "authentik",
            ForwardAuth: new ForwardAuthConfig(
                "http://authentik:9000",
                ForwardAuthPreset.Authentik,
                CacheSeconds: 30,
                RequestHeaders: ["x-tenant", "X-Tenant"],
                SignIn: new ForwardAuthSignInConfig("/outpost.goauthentik.io/start", "rd")));
        using var created = await api.SendJsonAsync(HttpMethod.Post, "/api/v1/authProviders", provider, AdminJsonContext.Default.AuthProviderConfig);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal($"/api/v1/authProviders/{provider.Id}", created.Headers.Location!.ToString());
        var version = AdminApiHarness.ETag(created)!;

        // Read back canonically: the preset by name, the header list a set. No secret is part of it.
        using var read = await api.GetAsync($"/api/v1/authProviders/{provider.Id}");
        var readBody = await read.Content.ReadAsStringAsync(Ct);
        Assert.Contains("\"preset\":\"authentik\"", readBody, StringComparison.Ordinal);
        Assert.Contains("\"requestHeaders\":[\"x-tenant\"]", readBody, StringComparison.Ordinal);
        Assert.Equal(version, AdminApiHarness.ETag(read));
        var stored = System.Text.Json.JsonSerializer.Deserialize(readBody, AdminJsonContext.Default.AuthProviderConfig)!;
        using var all = await api.GetAsync("/api/v1/authProviders");
        Assert.Contains(await AdminApiHarness.ReadAsync(all, AdminJsonContext.Default.IReadOnlyListAuthProviderConfig, Ct), p => p.Id == provider.Id);

        // An endpoint with credentials and a header the proxy owns are 422s pointing at their fields.
        AuthProviderConfig broken = new(AuthProviderId.New(), "broken", ForwardAuth: new ForwardAuthConfig("http://user:pw@auth/", ResponseHeaders: ["X-Forwarded-For"]));
        using var rejected = await api.SendJsonAsync(HttpMethod.Post, "/api/v1/authProviders", broken, AdminJsonContext.Default.AuthProviderConfig);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        Assert.Equal(
            [("/forwardAuth/url", "authProvider.url.invalid"), ("/forwardAuth/responseHeaders/0", "authProvider.header.invalid")],
            (await AdminApiHarness.ProblemAsync(rejected, Ct)).Errors!.Select(e => (e.Path, e.Code)));

        using var replaced = await api.SendJsonAsync(HttpMethod.Put, $"/api/v1/authProviders/{provider.Id}", stored with { Notes = "edited" }, AdminJsonContext.Default.AuthProviderConfig, ifMatch: version);
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        var latest = AdminApiHarness.ETag(replaced)!;

        // A host may name the provider; naming one that exists nowhere is refused.
        HostConfig host = ConfigTestSupport.ProxyHost("h-auth", "h-auth.test", "u-auth") with
        {
            Settings = new HostSettings { AuthProvider = new AuthProviderSettings { AuthProviderId = provider.Id } },
        };
        using var hostCreated = await PostHostAsync(api, host);
        Assert.Equal(HttpStatusCode.Created, hostCreated.StatusCode);
        using var unknown = await PostHostAsync(api, ConfigTestSupport.ProxyHost("h-auth-2", "h-auth-2.test", "u-auth") with
        {
            Settings = new HostSettings { AuthProvider = new AuthProviderSettings { AuthProviderId = AuthProviderId.New() } },
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);

        using var inUse = await api.SendAsync(HttpMethod.Delete, $"/api/v1/authProviders/{provider.Id}", ifMatch: latest);
        var problem = await AdminApiHarness.ProblemAsync(inUse, Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, inUse.StatusCode);
        Assert.Contains(problem.RelatedErrors!, e => e.Code == "host.authProvider.unknown");

        using var hostGone = await api.SendAsync(HttpMethod.Delete, $"/api/v1/hosts/{host.Id}", ifMatch: AdminApiHarness.ETag(hostCreated));
        Assert.Equal(HttpStatusCode.NoContent, hostGone.StatusCode);
        using var providerGone = await api.SendAsync(HttpMethod.Delete, $"/api/v1/authProviders/{provider.Id}", ifMatch: latest);
        Assert.Equal(HttpStatusCode.NoContent, providerGone.StatusCode);
        using var gone = await api.GetAsync($"/api/v1/authProviders/{provider.Id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);

        using var upstreamGone = await api.SendAsync(HttpMethod.Delete, $"/api/v1/upstreams/{upstream.Id}", ifMatch: AdminApiHarness.ETag(createdUpstream));
        Assert.Equal(HttpStatusCode.NoContent, upstreamGone.StatusCode);
    }

    [Fact]
    public async Task AnInlineIpAllowListIsRefusedWithASentenceThatNamesAccessLists()
    {
        var api = shared.Api;
        var host = ConfigTestSupport.ProxyHost("h-inline", "h-inline.test", "u-inline");
        var json = System.Text.Json.JsonSerializer.Serialize(host, AdminJsonContext.Default.HostConfig)
            .Replace("\"settings\":{}", "\"settings\":{\"ipAllowList\":{\"addresses\":[\"10.0.0.0/8\"]}}", StringComparison.Ordinal);
        Assert.Contains("ipAllowList", json, StringComparison.Ordinal);

        using var response = await api.SendAsync(HttpMethod.Post, "/api/v1/hosts", content: AdminApiHarness.JsonBody(json));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await AdminApiHarness.ProblemAsync(response, Ct);
        Assert.Contains("access lists replaced", problem.Detail, StringComparison.Ordinal);
        Assert.Contains("/api/v1/accessLists", problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AUserSentWithAPasswordHashIsRefusedWithASentenceThatSaysWhereThePasswordWentAndNothingIsStored()
    {
        var api = shared.Api;
        var id = AccessListId.New();
        var json = $$$"""{"id":"{{{id}}}","name":"inline-hash","version":0,"basicAuth":{"users":[{"username":"alice","passwordHash":"$pbkdf2-sha256$i=210000$c2FsdHNhbHRzYWx0c2FsdA$YWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWE"}]}}""";

        using var response = await api.SendAsync(HttpMethod.Post, "/api/v1/accessLists", content: AdminApiHarness.JsonBody(json));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await AdminApiHarness.ProblemAsync(response, Ct);
        Assert.Contains("'passwordHash'", problem.Detail, StringComparison.Ordinal);
        Assert.Contains("vault", problem.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("pbkdf2", problem.Detail, StringComparison.Ordinal);
        using var gone = await api.GetAsync($"/api/v1/accessLists/{id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task SettingsNeedTheirVersion()
    {
        var api = shared.Api;
        using var read = await api.GetAsync("/api/v1/settings");
        var settings = await AdminApiHarness.ReadAsync(read, AdminJsonContext.Default.GlobalSettings, Ct);
        var changed = settings with { UnknownHost = UnknownHostBehaviour.MisdirectedRequest };

        using var without = await api.SendJsonAsync(HttpMethod.Put, "/api/v1/settings", changed, AdminJsonContext.Default.GlobalSettings);
        Assert.Equal(HttpStatusCode.PreconditionRequired, without.StatusCode);
        using var with = await api.SendJsonAsync(HttpMethod.Put, "/api/v1/settings", changed, AdminJsonContext.Default.GlobalSettings, ifMatch: AdminApiHarness.ETag(read));
        Assert.Equal(HttpStatusCode.OK, with.StatusCode);
        using var stale = await api.SendJsonAsync(HttpMethod.Put, "/api/v1/settings", changed, AdminJsonContext.Default.GlobalSettings, ifMatch: AdminApiHarness.ETag(read));
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);

        // The request above changed the class's global settings; put them back, so a test that runs later on this shared host
        // sees the config it started with.
        using var now = await api.GetAsync("/api/v1/settings");
        using var restored = await api.SendJsonAsync(HttpMethod.Put, "/api/v1/settings", settings, AdminJsonContext.Default.GlobalSettings, ifMatch: AdminApiHarness.ETag(now));
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
    }

    [Fact]
    public async Task RevisionsPageDiffAndRollBack()
    {
        // Its own host: it pages through every revision and rolls back to one, so it must start with none.
        await using var api = await AdminApiHarness.StartAsync(Ct);
        List<ConfigRevisionId> ids = [];
        foreach (var label in new[] { "r1", "r2", "r3" })
        {
            using var created = await PostHostAsync(api, ConfigTestSupport.RespondHost(label, $"{label}.test", "ok"));
            ids.Add((await ResultAsync(created)).Revision!.Id);
        }

        using var first = await api.GetAsync("/api/v1/revisions?limit=2");
        Page<RevisionSummary> page = await AdminApiHarness.ReadAsync(first, AdminJsonContext.Default.PageRevisionSummary, Ct);
        Assert.Equal([ids[2], ids[1]], page.Items.Select(r => r.Id));
        Assert.NotNull(page.NextCursor);
        using var second = await api.GetAsync($"/api/v1/revisions?limit=2&cursor={page.NextCursor}");
        Page<RevisionSummary> rest = await AdminApiHarness.ReadAsync(second, AdminJsonContext.Default.PageRevisionSummary, Ct);
        Assert.Equal([ids[0]], rest.Items.Select(r => r.Id));
        Assert.Null(rest.NextCursor);
        foreach (var bad in new[] { "limit=0", "limit=9999", "cursor=garbage" })
        {
            using var rejected = await api.GetAsync("/api/v1/revisions?" + bad);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }

        using var detail = await api.GetAsync($"/api/v1/revisions/{ids[1]}");
        var revision = await AdminApiHarness.ReadAsync(detail, AdminJsonContext.Default.RevisionDetail, Ct);
        Assert.Equal(2, revision.Document.Hosts.Count);
        Assert.Equal(ids[0], revision.Revision.ParentId);
        using var diff = await api.GetAsync($"/api/v1/revisions/diff?from={ids[0]}&to={ids[2]}");
        Assert.Equal(2, (await AdminApiHarness.ReadAsync(diff, AdminJsonContext.Default.RevisionDiff, Ct)).Changes.Count(c => c is HostChange));
        using var noDiff = await api.GetAsync($"/api/v1/revisions/diff?from={ids[0]}");
        Assert.Equal(HttpStatusCode.BadRequest, noDiff.StatusCode);

        using var current = await api.GetAsync("/api/v1/config");
        var latest = AdminApiHarness.ETag(current)!;
        Assert.Equal($"\"{ids[2]}\"", latest);
        using var noMatch = await api.SendAsync(HttpMethod.Post, $"/api/v1/revisions/{ids[0]}/rollback");
        Assert.Equal(HttpStatusCode.PreconditionRequired, noMatch.StatusCode);
        using var stale = await api.SendAsync(HttpMethod.Post, $"/api/v1/revisions/{ids[0]}/rollback", ifMatch: $"\"{ids[0]}\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        using var dry = await api.SendAsync(HttpMethod.Post, $"/api/v1/revisions/{ids[0]}/rollback?dryRun=true", ifMatch: latest);
        Assert.Equal(ConfigCommandStatus.DryRun, (await ResultAsync(dry)).Status);
        using var rolledBack = await api.SendAsync(HttpMethod.Post, $"/api/v1/revisions/{ids[0]}/rollback", ifMatch: latest);
        Assert.Equal(ConfigCommandStatus.Committed, (await ResultAsync(rolledBack)).Status);
        using var hosts = await api.GetAsync("/api/v1/hosts");
        Assert.Single(await AdminApiHarness.ReadAsync(hosts, AdminJsonContext.Default.IReadOnlyListHostConfig, Ct));
        using var missing = await api.GetAsync($"/api/v1/revisions/{ConfigRevisionId.New()}");
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
        var api = shared.Api;

        using var response = await api.SendAsync(new HttpMethod(method), path, role, content: method == "POST" ? AdminApiHarness.JsonBody(string.Empty) : null);

        Assert.Equal(expected, (int)response.StatusCode);
        if (expected >= 400)
        {
            Assert.Equal(expected, (await AdminApiHarness.ProblemAsync(response, Ct)).Status);
        }
    }
}
