using System.Net;
using System.Text.Json.Nodes;

using Paduraksa.Admin.Json;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Tests.Integration.Config;
using Paduraksa.Tests.Integration.Hosting;

namespace Paduraksa.Tests.Integration.Admin;

/// <summary>Every collection pages the same way: <c>?limit=&amp;cursor=</c> in, <c>{ items, nextCursor }</c> out (docs/design/admin-surfaces.md "Conventions for a new endpoint").</summary>
public sealed class PaginationApiTests(SharedAdminApi shared) : IClassFixture<SharedAdminApi>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Collections =>
    [
        "hosts", "upstreams", "profiles", "rate-limit-policies", "access-lists", "rule-sets", "rule-sets/presets", "auth-providers",
        "certificates", "acme-orders", "acme-accounts", "dns-providers", "notification-channels", "drafts",
    ];

    [Theory]
    [MemberData(nameof(Collections))]
    public async Task ACollectionAnswersAPageAndRefusesAnUnusableLimitOrCursor(string collection)
    {
        var api = shared.Api;

        using var first = await api.GetAsync($"/api/v1/{collection}?limit=1");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var page = JsonNode.Parse(await first.Content.ReadAsStringAsync(Ct))!.AsObject();
        Assert.True(page["items"] is JsonArray { Count: <= 1 }, $"{collection}: items is an array of at most the limit");
        Assert.True(page.ContainsKey("nextCursor"), $"{collection}: nextCursor is always present, null on the last page");
        Assert.Equal(["items", "nextCursor"], page.Select(p => p.Key).Order(StringComparer.Ordinal));

        foreach (var bad in new[] { "limit=0", "limit=201", "limit=x", "cursor=not-a-cursor" })
        {
            using var refused = await api.GetAsync($"/api/v1/{collection}?{bad}");
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }
    }

    [Fact]
    public async Task ConfigCollectionsCanBeWalkedAPageAtATimeAndEveryObjectIsSeenOnceInIdOrder()
    {
        var api = shared.Api;
        var suffix = HostId.New().ToString()[^8..].ToLowerInvariant();
        for (var i = 0; i < 3; i++)
        {
            await CreateAsync(api, "upstreams", ConfigTestSupport.Upstream($"pg-{suffix}-{i}", "http://127.0.0.1:9"), AdminJsonContext.Default.UpstreamConfig);
            await CreateAsync(api, "profiles", new ProfileConfig(ProfileId.New(), $"pg-{suffix}-{i}"), AdminJsonContext.Default.ProfileConfig);
            await CreateAsync(api, "rate-limit-policies", new RateLimitPolicyConfig(RateLimitPolicyId.New(), $"pg-{suffix}-{i}", Requests: 30, Per: RateLimitPeriod.Second, Burst: 60), AdminJsonContext.Default.RateLimitPolicyConfig);
            await CreateAsync(api, "access-lists", new AccessListConfig(AccessListId.New(), $"pg-{suffix}-{i}", Ip: new AccessListIp(["10.0.0.0/8"])), AdminJsonContext.Default.AccessListConfig);
            await CreateAsync(api, "rule-sets", new RuleSetConfig(RuleSetId.New(), $"pg-{suffix}-{i}", Rules: [new RuleConfig(RuleAction.Deny, new RuleMatchConfig(new RulePathConfig(RulePathKind.Prefix, "/admin")))]), AdminJsonContext.Default.RuleSetConfig);
            await CreateAsync(api, "auth-providers", new AuthProviderConfig(AuthProviderId.New(), $"pg-{suffix}-{i}", ForwardAuth: new ForwardAuthConfig("http://authentik:9000", ForwardAuthPreset.Authentik)), AdminJsonContext.Default.AuthProviderConfig);
            await CreateAsync(api, "hosts", ConfigTestSupport.RespondHost($"pg-{suffix}-{i}", $"pg-{suffix}-{i}.test", "ok"), AdminJsonContext.Default.HostConfig);
        }

        foreach (var collection in new[] { "upstreams", "profiles", "rate-limit-policies", "access-lists", "rule-sets", "auth-providers", "hosts" })
        {
            List<string> ids = [];
            string? cursor = null;
            var pages = 0;
            do
            {
                using var response = await api.GetAsync($"/api/v1/{collection}?limit=2" + (cursor is null ? string.Empty : $"&cursor={cursor}"));
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var page = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!.AsObject();
                var items = page["items"]!.AsArray();
                Assert.InRange(items.Count, 1, 2);
                ids.AddRange(items.Select(i => i!["id"]!.GetValue<string>()));
                cursor = page["nextCursor"]?.GetValue<string>();
                pages++;
            }
            while (cursor is not null && pages < 100);

            Assert.True(ids.Count >= 3, $"{collection}: the three created objects are in the walk");
            Assert.Equal(ids.Order(StringComparer.Ordinal), ids);
            Assert.Equal(ids.Count, ids.Distinct().Count());
            Assert.True(pages >= 2, $"{collection}: three objects take more than one page of two");
        }
    }

    [Fact]
    public async Task APageOfARouteListIsThePagesOfThatHostAndTheCursorOfAnotherCollectionIsRefused()
    {
        var api = shared.Api;
        using var hosts = await api.GetAsync("/api/v1/hosts?limit=1");
        var hostCursorSource = JsonNode.Parse(await hosts.Content.ReadAsStringAsync(Ct))!["nextCursor"]?.GetValue<string>();
        if (hostCursorSource is not null)
        {
            using var wrongKind = await api.GetAsync($"/api/v1/upstreams?cursor={hostCursorSource}");
            Assert.Equal(HttpStatusCode.BadRequest, wrongKind.StatusCode);
        }
    }

    private static async Task CreateAsync<T>(AdminApiHarness api, string collection, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        using var created = await api.SendJsonAsync(HttpMethod.Post, $"/api/v1/{collection}", value, typeInfo);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }
}
