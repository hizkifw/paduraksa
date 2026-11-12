using Bunit;

using Microsoft.AspNetCore.Components;

using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Pages;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>
/// A list page offers its primary action once: the empty state carries it while the list is empty, and the page header does as soon as the
/// list has something in it. The action keeps one <c>data-action</c> hook either way.
/// </summary>
public sealed class EmptyListActionTests
{
    private static readonly ConfigDocument One = new(
        Hosts: [new HostConfig(HostId.New(), ["a.example.test"])],
        Upstreams: [new UpstreamConfig(UpstreamId.New(), "app")],
        Profiles: [new ProfileConfig(ProfileId.New(), "media")],
        RateLimitPolicies: [new RateLimitPolicyConfig(RateLimitPolicyId.New(), "api", Requests: 10, Per: RateLimitPeriod.Second)],
        AccessLists: [new AccessListConfig(AccessListId.New(), "office", Ip: new AccessListIp(["10.0.0.0/8"]))],
        RuleSets: [new RuleSetConfig(RuleSetId.New(), "scanners")],
        AuthProviders: [new AuthProviderConfig(AuthProviderId.New(), "authelia")]);

    [Fact]
    public Task Hosts() => ExpectOnceAsync<Hosts>("add-host");

    [Fact]
    public Task Upstreams() => ExpectOnceAsync<Upstreams>("add-upstream");

    [Fact]
    public Task Profiles() => ExpectOnceAsync<Profiles>("add-profile");

    [Fact]
    public Task RateLimits() => ExpectOnceAsync<RateLimits>("add-rate-limit");

    [Fact]
    public Task AccessLists() => ExpectOnceAsync<AccessLists>("add-access-list");

    [Fact]
    public Task RuleSets() => ExpectOnceAsync<RuleSets>("add-rule-set");

    [Fact]
    public Task AuthProviders() => ExpectOnceAsync<AuthProviders>("add-auth-provider");

    private static async Task ExpectOnceAsync<TPage>(string action)
        where TPage : IComponent
    {
        var hook = $"[data-action={action}]";

        await using (DashboardTestContext empty = new())
        {
            ConfigPages.SignIn(empty, Role.Admin);
            var page = empty.Render<TPage>();
            await page.WaitForStateAsync(() => page.FindAll(".pdk-empty").Count == 1);
            Assert.Single(page.FindAll(hook));
            Assert.Single(page.FindAll(".pdk-empty " + hook));
            Assert.Empty(page.FindAll(".pdk-page-header-actions " + hook));
        }

        await using (DashboardTestContext filled = new())
        {
            ConfigPages.SignIn(filled, Role.Admin).Document = One;
            var page = filled.Render<TPage>();
            await page.WaitForStateAsync(() => page.FindAll(".pdk-page-header-actions " + hook).Count == 1);
            Assert.Single(page.FindAll(hook));
            Assert.Empty(page.FindAll(".pdk-empty"));
        }
    }
}
