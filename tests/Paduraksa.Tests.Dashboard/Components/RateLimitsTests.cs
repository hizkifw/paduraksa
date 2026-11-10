using Bunit;

using MudBlazor;

using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard;
using Paduraksa.Dashboard.Drawers;
using Paduraksa.Dashboard.Pages;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>
/// The rate-limit policies: a list that says what each allows and who applies it, an editor whose header field belongs to
/// the header key alone, and create, edit and delete through the same review the other objects use. A policy in use offers
/// no delete, because the validator would refuse it.
/// </summary>
public sealed class RateLimitsTests
{
    private static readonly RateLimitPolicyConfig Strict = new(
        RateLimitPolicyId.New(), "Strict API", Requests: 10, Per: RateLimitPeriod.Second, Burst: 50, Key: RateLimitKey.Header, Header: "X-Api-Key");

    private static readonly RateLimitPolicyConfig Bulk = new(
        RateLimitPolicyId.New(), "Bulk", Requests: 60, Per: RateLimitPeriod.Minute, Burst: 20);

    [Fact]
    public async Task ThePage_ListsEachPolicyWithItsSummaryAndTheHostsAndRoutesThatUseIt()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        HostConfig host = new(
            HostId.New(), ["nas.home.test"], Routes: [new RouteConfig(RouteId.New(), UpstreamId.New(), Path: "/api", Settings: RouteLimit(Bulk.Id))],
            Settings: new HostSettings { RateLimit = new RateLimitSettings { PolicyId = Strict.Id } });
        config.Document = new ConfigDocument(RateLimitPolicies: [Strict, Bulk], Hosts: [host]);

        var page = context.Render<RateLimits>();

        await page.WaitForStateAsync(() => page.FindAll("[data-rate-limit]").Count == 2);
        var strict = page.Find("[data-rate-limit='Strict API']");
        Assert.Contains("10 per second, bursts of 50, per X-Api-Key header", strict.TextContent, StringComparison.Ordinal);
        Assert.Contains("1 host", strict.TextContent, StringComparison.Ordinal);
        Assert.Contains("nas.home.test", strict.TextContent, StringComparison.Ordinal);
        var bulk = page.Find("[data-rate-limit=Bulk]");
        Assert.Contains("60 per minute, bursts of 20, per client IP", bulk.TextContent, StringComparison.Ordinal);
        Assert.Contains("1 route", bulk.TextContent, StringComparison.Ordinal);
        Assert.Contains("/api", bulk.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APolicyInUse_OffersNoDelete_AndAnUnusedOneDoes()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        HostConfig host = new(HostId.New(), ["nas.home.test"], Settings: new HostSettings { RateLimit = new RateLimitSettings { PolicyId = Strict.Id } });
        config.Document = new ConfigDocument(RateLimitPolicies: [Strict, Bulk], Hosts: [host]);

        var page = context.Render<RateLimits>();

        await page.WaitForStateAsync(() => page.FindAll("[data-rate-limit]").Count == 2);
        Assert.Empty(page.FindAll("[data-rate-limit='Strict API'] [data-action=delete]"));
        Assert.Single(page.FindAll("[data-rate-limit=Bulk] [data-action=delete]"));
    }

    [Fact]
    public async Task CreatingAPolicy_SendsTheFormThroughReview()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        config.Document = new ConfigDocument();
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        context.Render<RateLimits>(parameters => parameters.Add(p => p.Key, DashboardRoutes.NewSegment));

        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=rate-limit] input[data-field=name]").Count == 1);
        await TypeAsync(drawers, "[data-drawer=rate-limit] input[data-field=name]", "Strict API");
        await drawers.InvokeAsync(() => drawers.FindComponents<MudSelect<RateLimitPeriod>>()
            .Single(select => select.Instance.Label == "Per")
            .Instance.ValueChanged.InvokeAsync(RateLimitPeriod.Second));
        await TypeAsync(drawers, "[data-drawer=rate-limit] input[data-field=requests]", "10", numeric: true);
        await TypeAsync(drawers, "[data-drawer=rate-limit] input[data-field=burst]", "50", numeric: true);
        await drawers.InvokeAsync(() => drawers.FindComponents<MudSelect<RateLimitKey>>()
            .Single(select => select.Instance.Label == "Count by")
            .Instance.ValueChanged.InvokeAsync(RateLimitKey.Header));
        await TypeAsync(drawers, "[data-drawer=rate-limit] input[data-field=header]", "X-Api-Key");

        var reviewing = drawers.Find("form[data-drawer=rate-limit]").SubmitAsync();
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-review=apply]").Count == 1);
        var dry = Assert.IsType<UpsertRateLimitPolicyCommand>(config.Executed[0]);
        Assert.True(dry.DryRun);
        Assert.Equal(("Strict API", 10, RateLimitPeriod.Second, 50, RateLimitKey.Header, "X-Api-Key"),
            (dry.Policy.Name, dry.Policy.Requests, dry.Policy.Per, dry.Policy.Burst, dry.Policy.Key, dry.Policy.Header));
        Assert.Equal(0, dry.Policy.Version);

        await drawers.Find("[data-review=apply]").ClickAsync(new());
        await reviewing.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken);
        var committed = Assert.IsType<UpsertRateLimitPolicyCommand>(config.Executed[^1]);
        Assert.False(committed.DryRun);
        Assert.Equal(dry.Policy.Id, committed.Policy.Id);
    }

    [Fact]
    public async Task EditingAPolicy_SendsTheVersionItOpenedWith()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        var stored = Bulk with { Version = 4 };
        config.Document = new ConfigDocument(RateLimitPolicies: [stored]);
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        context.Render<RateLimits>(parameters => parameters.Add(p => p.Key, stored.Id.ToString()));

        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=rate-limit] input[data-field=name]").Count == 1);
        Assert.Equal("Bulk", drawers.Find("[data-drawer=rate-limit] input[data-field=name]").GetAttribute("value"));
        await TypeAsync(drawers, "[data-drawer=rate-limit] input[data-field=name]", "Bulk traffic");
        _ = drawers.Find("form[data-drawer=rate-limit]").SubmitAsync();
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-review=apply]").Count == 1);

        var edit = Assert.IsType<UpsertRateLimitPolicyCommand>(config.Executed[0]);
        Assert.True(edit.DryRun);
        Assert.Equal((stored.Id, 4L, "Bulk traffic"), (edit.Policy.Id, edit.Policy.Version, edit.Policy.Name));
    }

    [Fact]
    public async Task DeletingAnUnusedPolicy_AsksFirstAndSendsTheStoredVersion()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        var stored = Bulk with { Version = 4 };
        config.Document = new ConfigDocument(RateLimitPolicies: [stored]);
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        var page = context.Render<RateLimits>();

        await page.WaitForStateAsync(() => page.FindAll("[data-action=delete]").Count == 1);
        var deleting = page.Find("[data-action=delete]").ClickAsync(new());
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-review=apply]").Count == 1);

        var dry = Assert.IsType<DeleteRateLimitPolicyCommand>(config.Executed[0]);
        Assert.True(dry.DryRun);
        Assert.Equal((stored.Id, 4L), (dry.RateLimitPolicyId, dry.Version));

        await drawers.Find("[data-review=apply]").ClickAsync(new());
        await deleting.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken);
        await drawers.WaitForStateAsync(() => config.Executed.Count == 2);
        var committed = Assert.IsType<DeleteRateLimitPolicyCommand>(config.Executed[1]);
        Assert.False(committed.DryRun);
        Assert.Equal(stored.Id, committed.RateLimitPolicyId);
    }

    [Fact]
    public async Task TheHeaderField_AppearsOnlyForTheHeaderKey()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        config.Document = new ConfigDocument();
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        context.Render<RateLimits>(parameters => parameters.Add(p => p.Key, DashboardRoutes.NewSegment));
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=rate-limit] [data-field=key]").Count == 1);

        Assert.Empty(drawers.FindAll("[data-drawer=rate-limit] [data-field=header]"));

        await drawers.InvokeAsync(() => drawers.FindComponents<MudSelect<RateLimitKey>>()
            .Single(select => select.Instance.Label == "Count by")
            .Instance.ValueChanged.InvokeAsync(RateLimitKey.Header));
        Assert.Single(drawers.FindAll("[data-drawer=rate-limit] input[data-field=header]"));

        await drawers.InvokeAsync(() => drawers.FindComponents<MudSelect<RateLimitKey>>()
            .Single(select => select.Instance.Label == "Count by")
            .Instance.ValueChanged.InvokeAsync(RateLimitKey.ClientIpAndPath));
        Assert.Empty(drawers.FindAll("[data-drawer=rate-limit] [data-field=header]"));
    }

    private static RouteSettings RouteLimit(RateLimitPolicyId policy) => new()
    {
        RateLimit = new RateLimitSettings { PolicyId = policy },
    };

    /// <summary>Types into a MudBlazor field and leaves it (which sends what was typed at once, instead of after the field's debounce).</summary>
    private static async Task TypeAsync(IRenderedComponent<DrawerHost> drawers, string selector, string value, bool numeric = false)
    {
        if (numeric)
        {
            // A numeric field listens for change, not input.
            await drawers.Find(selector).ChangeAsync(new() { Value = value });
        }
        else
        {
            await drawers.Find(selector).InputAsync(new() { Value = value });
        }

        await drawers.Find(selector).BlurAsync(new());
    }
}
