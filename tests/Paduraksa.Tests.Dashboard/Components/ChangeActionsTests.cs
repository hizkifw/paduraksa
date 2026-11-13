using Bunit;

using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

using MudBlazor;

using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Drafts;
using Paduraksa.Contracts.Revisions;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Drawers;
using Paduraksa.Dashboard.Pages;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>
/// Every control that changes the config goes through the review sheet, where Apply now and Add to draft are the choices (docs/design/dashboard.md "Making a change"):
/// the host list's switch and wrench, a destination's switch, a row's delete and a whole-document change's notice. Nothing applies or drafts silently.
/// </summary>
public sealed class ChangeActionsTests
{
    private static readonly UpstreamConfig App = new(UpstreamId.New(), "app", Version: 1,
        Destinations: [new DestinationConfig(DestinationId.New(), "http://app:8080"), new DestinationConfig(DestinationId.New(), "http://app2:8080")]);

    private static readonly HostConfig Host = new(HostId.New(), ["app.example.com"], Version: 2, Routes: [new RouteConfig(RouteId.New(), App.Id)]);

    private static readonly RateLimitPolicyConfig Bulk = new(RateLimitPolicyId.New(), "Bulk", Requests: 60, Per: RateLimitPeriod.Minute, Burst: 20, Version: 4);

    private static ConfigDraft DraftOf(params DraftEntry[] entries) =>
        new(DraftId.New(), 3, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(30), entries);

    private static DraftEntry Entry(int number, ConfigCommand command)
    {
        var (target, action) = DraftTargets.Of(command)!.Value;
        return new DraftEntry(number, action, target, command, DateTimeOffset.UnixEpoch, DraftEntryState.Ready);
    }

    private static FakeDashboardConfig Setup(DashboardTestContext context, bool draftOpen, ConfigDocument? document = null)
    {
        var config = ConfigPages.SignIn(context, Role.Operator);
        config.Document = document ?? new ConfigDocument(Upstreams: [App], Hosts: [Host], RateLimitPolicies: [Bulk]);
        config.Draft = draftOpen ? DraftOf(Entry(1, new UpsertRateLimitPolicyCommand(Bulk with { Name = "other" }))) : null;
        return config;
    }

    private static async Task<(IRenderedComponent<Hosts> Page, IRenderedComponent<DrawerHost> Drawers)> HostsAsync(DashboardTestContext context)
    {
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        var page = context.Render<Hosts>();
        await page.WaitForStateAsync(() => page.FindAll(".pdk-hosts-table").Count == 1);
        return (page, drawers);
    }

    private static async Task WaitForSheetAsync(IRenderedComponent<DrawerHost> drawers) =>
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=review]").Count == 1 && drawers.FindAll("[data-review-phase=loading]").Count == 0);

    private static string Primary(IRenderedComponent<DrawerHost> drawers) =>
        drawers.FindAll("[data-drawer=review] .pdk-drawer-actions .mud-button-filled").Single().GetAttribute("data-action") is { } action ? action : "apply";

    [Theory]
    [InlineData(false, "apply")]
    [InlineData(true, "add-to-draft")]
    public async Task TheHostListSwitch_OpensTheSheet_AndNeverAppliesOrDraftsSilently(bool draftOpen, string primary)
    {
        await using DashboardTestContext context = new();
        var config = Setup(context, draftOpen);
        var (page, drawers) = await HostsAsync(context);

        _ = page.Find("[data-host='app.example.com'] .pdk-dt-controls input[type=checkbox]").ChangeAsync(new ChangeEventArgs { Value = false });
        await WaitForSheetAsync(drawers);

        Assert.Contains("Switch off app.example.com", drawers.Find("[data-drawer=review]").TextContent, StringComparison.Ordinal);
        Assert.Equal(primary, Primary(drawers));
        Assert.Equal(["cancel", "add-to-draft", "apply"], drawers.FindAll("[data-drawer=review] .pdk-drawer-actions button").Select(b => b.GetAttribute("data-action") ?? b.GetAttribute("data-review")));
        Assert.All(config.Executed, command => Assert.True(command.DryRun));
        Assert.Empty(config.DraftAdds);
    }

    [Fact]
    public async Task TheHostListSwitch_AddToDraft_AddsTheHostWithItsVersionAndMarksTheDraftedState()
    {
        await using DashboardTestContext context = new();
        var config = Setup(context, draftOpen: true);
        var (page, drawers) = await HostsAsync(context);
        _ = page.Find("[data-host='app.example.com'] .pdk-dt-controls input[type=checkbox]").ChangeAsync(new ChangeEventArgs { Value = false });
        await WaitForSheetAsync(drawers);

        await drawers.Find("[data-drawer=review] [data-action=add-to-draft]").ClickAsync(new());

        await drawers.WaitForStateAsync(() => config.DraftAdds.Count == 1);
        var added = Assert.IsType<UpsertHostCommand>(Assert.Single(config.DraftAdds[0]));
        Assert.Equal((Host.Id, 2L, false), (added.Host.Id, added.Host.Version, added.Host.Enabled));
        Assert.DoesNotContain(config.Executed, command => !command.DryRun);
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=review]").Count == 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheWrenchOnAsksForTheOptionsPrefilled_ThenOpensTheSameSheet(bool draftOpen)
    {
        await using DashboardTestContext context = new();
        var config = Setup(context, draftOpen);
        var (page, drawers) = await HostsAsync(context);

        _ = page.Find("[data-action=maintenance]").ClickAsync(new());
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=maintenance] [data-field=maintenance-status]").Count == 1);
        Assert.Empty(config.Executed);
        Assert.Equal("Review changes", drawers.Find("[data-drawer=maintenance] button[type=submit]").TextContent.Trim());
        Assert.Empty(drawers.FindAll("[data-drawer=maintenance] [data-action=add-to-draft]"));

        _ = drawers.Find("form[data-drawer=maintenance]").SubmitAsync();
        await WaitForSheetAsync(drawers);

        var reviewed = Assert.IsType<UpsertHostCommand>(Assert.Single(config.Executed));
        Assert.Equal(true, reviewed.Host.Settings.Maintenance?.Enabled);
        Assert.Equal(draftOpen ? "add-to-draft" : "apply", Primary(drawers));
        if (draftOpen)
        {
            await drawers.Find("[data-drawer=review] [data-action=add-to-draft]").ClickAsync(new());
            await drawers.WaitForStateAsync(() => config.DraftAdds.Count == 1);
            Assert.True(Assert.IsType<UpsertHostCommand>(Assert.Single(config.DraftAdds[0])).Host.Settings.Maintenance?.Enabled);
            await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=maintenance]").Count == 0);
        }
    }

    [Fact]
    public async Task AChangeTheLiveConfigRefuses_StillOffersAddToDraftWhileADraftIsOpen_AndApplyNowIsOff()
    {
        await using DashboardTestContext context = new();
        var config = Setup(context, draftOpen: true);
        // The host names an upstream only the draft adds: the live config can't take it alone.
        config.Results = command => new ConfigCommandResult(ConfigCommandStatus.Invalid, Errors: [new ConfigError("/routes/0/upstreamId", "route.upstream.unknown", "no such upstream")]);
        var (page, drawers) = await HostsAsync(context);

        _ = page.Find("[data-host='app.example.com'] .pdk-dt-controls input[type=checkbox]").ChangeAsync(new ChangeEventArgs { Value = false });
        await WaitForSheetAsync(drawers);

        Assert.Equal(["cancel", "add-to-draft", "apply"], drawers.FindAll("[data-drawer=review] .pdk-drawer-actions button").Select(b => b.GetAttribute("data-action") ?? b.GetAttribute("data-review")));
        Assert.True(drawers.Find("[data-drawer=review] [data-review=apply]").HasAttribute("disabled"));
        Assert.False(drawers.Find("[data-drawer=review] [data-action=add-to-draft]").HasAttribute("disabled"));
        Assert.Contains("before this can be applied on its own", drawers.Find("[data-drawer=review] [data-review=invalid]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADestinationsSwitchIsPartOfTheUpstreamFormAndLeavesThroughTheSameSheet()
    {
        await using DashboardTestContext context = new();
        var config = Setup(context, draftOpen: true);
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        context.Render<Upstreams>(parameters => parameters.Add(p => p.Key, App.Id.ToString()));
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=upstream] .pdk-destination-row").Count == 2);

        await drawers.Find("[data-drawer=upstream] .pdk-destination-row:nth-child(2) input[type=checkbox]").ChangeAsync(new ChangeEventArgs { Value = false });
        _ = drawers.Find("form[data-drawer=upstream]").SubmitAsync();
        await WaitForSheetAsync(drawers);
        await drawers.Find("[data-drawer=review] [data-action=add-to-draft]").ClickAsync(new());

        await drawers.WaitForStateAsync(() => config.DraftAdds.Count == 1);
        var added = Assert.IsType<UpsertUpstreamCommand>(Assert.Single(config.DraftAdds[0]));
        Assert.Equal([true, false], added.Upstream.Destinations.Select(d => d.Enabled));
        Assert.Empty(drawers.FindAll("[data-drawer=upstream] [data-action=add-to-draft]"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARowsDelete_OpensTheSheetWithADangerApplyNow_AndAddToDraftMakesItAnEntry(bool draftOpen)
    {
        await using DashboardTestContext context = new();
        var config = Setup(context, draftOpen);
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        var page = context.Render<RateLimits>();
        await page.WaitForStateAsync(() => page.FindAll("[data-action=delete]").Count == 1);

        _ = page.Find("[data-action=delete]").ClickAsync(new());
        await WaitForSheetAsync(drawers);

        Assert.Contains("-error", drawers.Find("[data-drawer=review] [data-review=apply]").ClassName, StringComparison.Ordinal);
        Assert.Equal("Apply now", drawers.Find("[data-drawer=review] [data-review=apply]").TextContent.Trim());
        await drawers.Find("[data-drawer=review] [data-action=add-to-draft]").ClickAsync(new());

        await drawers.WaitForStateAsync(() => config.DraftAdds.Count == 1);
        var delete = Assert.IsType<DeleteRateLimitPolicyCommand>(Assert.Single(config.DraftAdds[0]));
        Assert.Equal((Bulk.Id, 4L), (delete.RateLimitPolicyId, delete.Version));
        Assert.All(config.Executed, command => Assert.True(command.DryRun));
    }

    [Fact]
    public async Task ARollbackWhileADraftIsOpen_WarnsOnceAndOffersToReviewTheDraftFirst_AndHasNoDraftChoice()
    {
        await using DashboardTestContext context = new();
        var config = Setup(context, draftOpen: true);
        var me = UserId.New();
        var first = Revision(1, null, new ConfigDocument(Upstreams: [App]), me);
        var second = Revision(2, first.Revision.Id, new ConfigDocument(Upstreams: [App], Hosts: [Host]), me);
        config.Revisions[first.Revision.Id] = first;
        config.Revisions[second.Revision.Id] = second;
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/history?revision={first.Revision.Id}");
        var page = context.Render<History>();
        await page.WaitForStateAsync(() => page.FindAll("[data-action=rollback]").Count == 1);

        await page.Find("[data-action=rollback]").ClickAsync(new());
        await WaitForSheetAsync(drawers);

        var notice = drawers.Find("[data-drawer=review] [data-review=draft-open]");
        Assert.Contains("You have a draft with 1 change", notice.TextContent, StringComparison.Ordinal);
        Assert.Empty(drawers.FindAll("[data-drawer=review] [data-action=add-to-draft]"));

        await notice.QuerySelector("[data-action=review-draft-first]")!.ClickAsync(new());

        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=draft]").Count == 1 && drawers.FindAll("[data-drawer=review]").Count == 0);
    }

    [Fact]
    public async Task ARollbackWithNoDraftOpenHasNoNotice()
    {
        await using DashboardTestContext context = new();
        var config = Setup(context, draftOpen: false);
        var me = UserId.New();
        var first = Revision(1, null, new ConfigDocument(Upstreams: [App]), me);
        var second = Revision(2, first.Revision.Id, new ConfigDocument(Upstreams: [App], Hosts: [Host]), me);
        config.Revisions[first.Revision.Id] = first;
        config.Revisions[second.Revision.Id] = second;
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/history?revision={first.Revision.Id}");
        var page = context.Render<History>();
        await page.WaitForStateAsync(() => page.FindAll("[data-action=rollback]").Count == 1);

        await page.Find("[data-action=rollback]").ClickAsync(new());
        await WaitForSheetAsync(drawers);

        Assert.Empty(drawers.FindAll("[data-review=draft-open]"));
    }

    private static RevisionDetail Revision(long seq, ConfigRevisionId? parent, ConfigDocument document, UserId user) =>
        new(
            new RevisionSummary(ConfigRevisionId.New(), seq, new ActorRef(ActorKind.User, user), Channel.Dashboard, DateTimeOffset.UtcNow, new string('0', 64), parent),
            [],
            document);
}
