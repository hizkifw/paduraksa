using Bunit;

using Microsoft.Extensions.DependencyInjection;

using MudBlazor;

using Paduraksa.Application.Drafts;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Drafts;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard;
using Paduraksa.Dashboard.Components;
using Paduraksa.Dashboard.Config;
using Paduraksa.Dashboard.Drawers;
using Paduraksa.Dashboard.Pages;
using Paduraksa.Dashboard.SettingsArea;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>
/// Drafts in the dashboard (docs/design/dashboard.md "Drafts (M10)"): the bar across the pages, Add to draft in the review sheet and what it sends, the draft drawer (which is the
/// review of the whole set, with a diff per entry, remove and changed-elsewhere states), "Draft" marks on the lists and in the pickers, and the apply through the draft.
/// </summary>
public sealed class DraftTests
{
    private static readonly RateLimitPolicyConfig Bulk = new(
        RateLimitPolicyId.New(), "Bulk", Requests: 60, Per: RateLimitPeriod.Minute, Burst: 20, Version: 4);

    private static readonly UpstreamConfig App = new(UpstreamId.New(), "app", Destinations: [new DestinationConfig(DestinationId.New(), "http://10.0.0.5:8080")], Version: 2);

    private static ConfigDraft DraftOf(params DraftEntry[] entries) =>
        new(DraftId.New(), 3, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(30), entries);

    private static DraftEntry Entry(int number, ConfigCommand command, DraftEntryState state = DraftEntryState.Ready)
    {
        var (target, action) = DraftTargets.Of(command)!.Value;
        return new DraftEntry(number, action, target, command, DateTimeOffset.UnixEpoch, state);
    }

    private static async Task<DraftState> LoadedAsync(DashboardTestContext context)
    {
        var state = context.Services.GetRequiredService<DraftState>();
        await state.EnsureLoadedAsync();
        return state;
    }

    // ---- the bar ----

    [Fact]
    public async Task TheBar_ShowsOnlyWhileADraftIsOpen_WithItsChangeCount()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        config.Document = new ConfigDocument(Upstreams: [App]);
        var bar = context.Render<DraftBar>();
        await bar.InvokeAsync(() => LoadedAsync(context));
        Assert.Empty(bar.FindAll("[data-draft-bar]"));

        config.Draft = DraftOf(Entry(1, new UpsertUpstreamCommand(App with { Name = "renamed" })), Entry(2, new DeleteRateLimitPolicyCommand(Bulk.Id, 4)));
        await bar.InvokeAsync(config.DraftChangedAsync);

        await bar.WaitForStateAsync(() => bar.FindAll("[data-draft-bar]").Count == 1);
        Assert.Equal("2 changes", bar.Find("[data-draft-count]").TextContent.Trim());
        Assert.Single(bar.FindAll("[data-action=review-draft]"));
        Assert.Single(bar.FindAll("[data-action=discard-draft]"));
    }

    [Fact]
    public async Task TheBar_SaysHowManyEntriesAreChangedElsewhere()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        config.Draft = DraftOf(Entry(1, new UpsertUpstreamCommand(App), DraftEntryState.ChangedElsewhere));

        var bar = context.Render<DraftBar>();

        await bar.WaitForStateAsync(() => bar.FindAll("[data-draft-bar]").Count == 1);
        Assert.Equal("1 change, 1 changed elsewhere", bar.Find("[data-draft-count]").TextContent.Trim());
    }

    [Fact]
    public async Task ARoleThatMayNotChangeConfigHasNoBarAndAsksNothing()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Viewer);
        config.Draft = DraftOf(Entry(1, new UpsertUpstreamCommand(App)));

        var bar = context.Render<DraftBar>();
        await bar.InvokeAsync(() => LoadedAsync(context));

        Assert.Empty(bar.FindAll("[data-draft-bar]"));
    }

    [Fact]
    public async Task ADroppedDraftsNoticeIsShownOnceAsAToast()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        config.Notices.Add(new DraftNotice(DraftId.New(), DateTimeOffset.UnixEpoch, 3));
        var snackbar = context.Services.GetRequiredService<ISnackbar>();

        var bar = context.Render<DraftBar>();
        await bar.WaitForStateAsync(() => snackbar.ShownSnackbars.Any());

        Assert.Equal("Your draft with 3 changes was dropped after 30 days untouched.", Assert.Single(snackbar.ShownSnackbars).Message?.ToString());
        Assert.Empty(config.Notices);
    }

    [Fact]
    public async Task Discard_AsksFirstAndThenDropsTheDraft()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        config.Draft = DraftOf(Entry(1, new UpsertUpstreamCommand(App)));
        context.Render<MudPopoverProvider>();
        var dialogs = context.Render<MudDialogProvider>();
        var bar = context.Render<DraftBar>();
        await bar.WaitForStateAsync(() => bar.FindAll("[data-draft-bar]").Count == 1);

        var asking = bar.Find("[data-action=discard-draft]").ClickAsync(new());
        await dialogs.WaitForStateAsync(() => dialogs.FindAll(".pdk-confirm").Count == 1);
        Assert.Equal(0, config.DraftDiscards);
        await dialogs.Find(".pdk-confirm").ClickAsync(new());
        await asking;

        await bar.WaitForStateAsync(() => bar.FindAll("[data-draft-bar]").Count == 0);
        Assert.Equal(1, config.DraftDiscards);
    }

    // ---- the draft's list ----

    [Fact]
    public async Task TheList_HasEachEntryAndRemovingOneTakesItOutOfTheDraft()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        config.Document = new ConfigDocument(RateLimitPolicies: [Bulk]);
        config.Draft = DraftOf(Entry(1, new UpsertUpstreamCommand(App with { Version = 0 })), Entry(2, new DeleteRateLimitPolicyCommand(Bulk.Id, Bulk.Version)));
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        var bar = context.Render<DraftBar>();
        await bar.WaitForStateAsync(() => bar.FindAll("[data-draft-bar]").Count == 1);

        await bar.Find("[data-action=review-draft]").ClickAsync(new());
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=draft] [data-entry]").Count == 2);

        Assert.Equal("Add upstream app", drawers.Find("[data-entry='1'] .pdk-list-title").TextContent.Trim());
        Assert.Equal("Delete rate limit Bulk", drawers.Find("[data-entry='2'] .pdk-list-title").TextContent.Trim());

        await drawers.Find("[data-entry='1'] [data-action=remove-entry]").ClickAsync(new());

        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=draft] [data-entry]").Count == 1);
        Assert.Equal([2], config.Draft!.Entries.Select(e => e.Number));
        Assert.Equal("1 change", bar.Find("[data-draft-count]").TextContent.Trim());
    }

    [Fact]
    public async Task AnEntryChangedElsewhere_SaysSoAndCanBeReloaded()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        config.Document = new ConfigDocument(Upstreams: [App]);
        config.Draft = DraftOf(Entry(1, new UpsertUpstreamCommand(App with { Name = "mine" }), DraftEntryState.ChangedElsewhere) with { Reason = "Its object was changed or removed elsewhere since you added it." }, Entry(2, new UpsertRateLimitPolicyCommand(Bulk)));
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        var bar = context.Render<DraftBar>();
        await bar.WaitForStateAsync(() => bar.FindAll("[data-draft-bar]").Count == 1);
        await bar.Find("[data-action=review-draft]").ClickAsync(new());
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=draft] [data-entry]").Count == 2);

        var blocked = drawers.Find("[data-entry='1']");
        Assert.Equal("changed-elsewhere", blocked.GetAttribute("data-state"));
        Assert.Contains("Changed elsewhere", blocked.TextContent, StringComparison.Ordinal);
        Assert.Contains("Reload or remove it before applying", blocked.TextContent, StringComparison.Ordinal);
        Assert.Empty(drawers.Find("[data-entry='2']").QuerySelectorAll("[data-action=reload-entry]"));

        await blocked.QuerySelector("[data-action=reload-entry]")!.ClickAsync(new());

        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=draft]").Count == 0);
        Assert.Equal([2], config.Draft!.Entries.Select(e => e.Number));
        Assert.EndsWith(DashboardRoutes.Upstream(App.Id), context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyDraft_SaysWhatToDoAndHasNothingToApply()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        config.Draft = DraftOf();
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        var bar = context.Render<DraftBar>();
        await bar.WaitForStateAsync(() => bar.FindAll("[data-draft-bar]").Count == 1);

        await bar.Find("[data-action=review-draft]").ClickAsync(new());

        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=draft] .pdk-empty").Count == 1);
        Assert.Contains("No changes in the draft", drawers.Markup, StringComparison.Ordinal);
        Assert.True(drawers.Find("[data-action=apply-draft]").HasAttribute("disabled"));
    }

    // ---- each entry's diff ----

    [Fact]
    public async Task ASmallDraft_OpensEveryEntryWithItsDiffRenderedAsTheReviewShowsIt()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        config.Document = new ConfigDocument(Upstreams: [App], RateLimitPolicies: [Bulk]);
        config.Draft = DraftOf(Entry(1, new UpsertUpstreamCommand(App with { Name = "renamed" })), Entry(2, new DeleteRateLimitPolicyCommand(Bulk.Id, Bulk.Version)));
        config.WorkingDocument = new ConfigDocument(Upstreams: [App with { Name = "renamed" }]);

        var (drawers, _) = await OpenReviewAsync(context);

        var changed = drawers.Find("[data-entry='1']");
        Assert.Equal("true", changed.GetAttribute("data-open"));
        Assert.Contains("Changed", changed.QuerySelector(".pdk-diff .pdk-badge")!.TextContent, StringComparison.Ordinal);
        Assert.Equal("app", changed.QuerySelector(".pdk-diff-old")!.TextContent.Trim());
        Assert.Equal("renamed", changed.QuerySelector(".pdk-diff-new")!.TextContent.Trim());
        var removed = drawers.Find("[data-entry='2']");
        Assert.Contains("Removed", removed.QuerySelector(".pdk-diff .pdk-badge")!.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALongDraft_StartsCollapsedToItsSummaryLines_AndAnEntryOpensWithItsToggle()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        config.Document = new ConfigDocument(Upstreams: [App]);
        var entries = Enumerable.Range(1, DraftReviewDrawer.OpenByDefaultUpTo + 1)
            .Select(i => Entry(i, new UpsertRateLimitPolicyCommand(new RateLimitPolicyConfig(RateLimitPolicyId.New(), $"limit {i}", Requests: 10, Per: RateLimitPeriod.Minute, Burst: 5))))
            .ToArray();
        config.Draft = DraftOf(entries);
        config.WorkingDocument = config.Document;

        var (drawers, _) = await OpenReviewAsync(context);

        Assert.Equal(entries.Length, drawers.FindAll("[data-entry]").Count);
        Assert.Empty(drawers.FindAll("[data-entry] .pdk-diff"));
        Assert.All(drawers.FindAll("[data-entry]"), e => Assert.Equal("false", e.GetAttribute("data-open")));
        Assert.Equal("Add rate limit limit 1", drawers.Find("[data-entry='1'] .pdk-list-title").TextContent.Trim());

        await drawers.Find("[data-entry='1'] [data-action=toggle-entry]").ClickAsync(new());

        Assert.Equal("true", drawers.Find("[data-entry='1']").GetAttribute("data-open"));
        Assert.Single(drawers.FindAll("[data-entry] .pdk-diff"));
        Assert.Contains("Added", drawers.Find("[data-entry='1'] .pdk-diff .pdk-badge").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDraftIsReviewedOnceWhenItOpens_NotOnEveryRenderOrUnchangedTick()
    {
        await using DashboardTestContext context = new();
        var config = DraftedUpstream(context);
        var (drawers, _) = await OpenReviewAsync(context);
        Assert.Equal(1, config.DraftReviews);

        // A render (the entry's toggle) and a commit that leaves the draft and the head as they were do not review again.
        await drawers.Find("[data-entry='1'] [data-action=toggle-entry]").ClickAsync(new());
        await drawers.InvokeAsync(config.DraftChangedAsync);
        await drawers.InvokeAsync(config.DraftChangedAsync);
        Assert.Equal(1, config.DraftReviews);

        // A change to the draft does.
        config.Draft = config.Draft! with { Version = config.Draft.Version + 1 };
        await drawers.InvokeAsync(config.DraftChangedAsync);
        await drawers.WaitForStateAsync(() => config.DraftReviews == 2);
    }

    // ---- the review and the apply ----

    /// <summary>The bar's Review: the draft drawer is the review, and it has run it by the time it is ready.</summary>
    private static async Task<(IRenderedComponent<DrawerHost> Drawers, IRenderedComponent<DraftBar> Bar)> OpenReviewAsync(DashboardTestContext context)
    {
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        var bar = context.Render<DraftBar>();
        await bar.WaitForStateAsync(() => bar.FindAll("[data-draft-bar]").Count == 1);
        await bar.Find("[data-action=review-draft]").ClickAsync(new());
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=draft]").Count == 1 && drawers.FindAll("[data-review-phase=loading]").Count == 0);
        return (drawers, bar);
    }

    private static FakeDashboardConfig DraftedUpstream(DashboardTestContext context)
    {
        var config = ConfigPages.SignIn(context, Role.Operator);
        config.Document = new ConfigDocument(Upstreams: [App]);
        config.Draft = DraftOf(Entry(1, new UpsertUpstreamCommand(App with { Name = "mine" })));
        config.WorkingDocument = new ConfigDocument(Upstreams: [App with { Name = "mine" }]);
        return config;
    }

    [Fact]
    public async Task ReviewingTheDraft_RunsItThroughTheDraftAndApplySendsTheTokenOfTheReviewItShowed()
    {
        await using DashboardTestContext context = new();
        var config = DraftedUpstream(context);
        var draftId = config.Draft!.Id;
        var token = FakeDashboardConfig.TokenOf(config.Draft);
        var (drawers, bar) = await OpenReviewAsync(context);

        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-action=apply-draft]").Count == 1);
        Assert.Empty(config.Executed);
        Assert.Equal("Apply", drawers.Find("[data-action=apply-draft]").TextContent.Trim());
        await drawers.Find("[data-action=apply-draft]").ClickAsync(new());

        await drawers.WaitForStateAsync(() => config.DraftApplies.Count == 1);
        Assert.Equal((draftId, token), (config.DraftApplies[0].Draft, config.DraftApplies[0].Token));
        Assert.Empty(config.Executed);
        await bar.WaitForStateAsync(() => bar.FindAll("[data-draft-bar]").Count == 0);
    }

    [Fact]
    public async Task WhenTheConfigChangedSinceTheReview_TheSheetShowsTheNewReviewInPlace_AndApplyNeedsANewConfirmation()
    {
        await using DashboardTestContext context = new();
        var config = DraftedUpstream(context);
        var (drawers, bar) = await OpenReviewAsync(context);
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-action=apply-draft]").Count == 1);
        var firstToken = FakeDashboardConfig.TokenOf(config.Draft!);

        // Something the draft's outcome depends on moves between the review and the click.
        config.Draft = config.Draft! with { Version = config.Draft.Version + 1 };
        var freshToken = FakeDashboardConfig.TokenOf(config.Draft);
        await drawers.Find("[data-action=apply-draft]").ClickAsync(new());

        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-review=changed-since-review]").Count == 1);
        Assert.Single(config.DraftApplies);
        Assert.Equal(firstToken, config.DraftApplies[0].Token);
        Assert.NotNull(config.Draft);
        Assert.False(drawers.Find("[data-action=apply-draft]").HasAttribute("disabled"));
        Assert.Contains("check it, then apply", drawers.Find("[data-review=changed-since-review]").TextContent, StringComparison.Ordinal);

        // Nothing was applied on its own; the second, deliberate click names the new review.
        await drawers.Find("[data-action=apply-draft]").ClickAsync(new());
        await drawers.WaitForStateAsync(() => config.DraftApplies.Count == 2);
        Assert.Equal(freshToken, config.DraftApplies[1].Token);
        await bar.WaitForStateAsync(() => bar.FindAll("[data-draft-bar]").Count == 0);
    }

    [Fact]
    public async Task AnEntryBlockedBetweenTheReviewAndTheApply_RefusesTheWholeDraft_AndTheSheetListsItWithReload()
    {
        await using DashboardTestContext context = new();
        var config = DraftedUpstream(context);
        var (drawers, _) = await OpenReviewAsync(context);
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-action=apply-draft]").Count == 1);

        config.Draft = config.Draft! with { Entries = [.. config.Draft.Entries.Select(e => e with { State = DraftEntryState.ChangedElsewhere, Reason = "Its object was changed or removed elsewhere since you added it." })] };
        await drawers.Find("[data-action=apply-draft]").ClickAsync(new());

        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-review=blocked]").Count == 1);
        Assert.Contains("nothing can be applied", drawers.Find("[data-review=blocked]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Change upstream mine", drawers.Find("[data-entry='1']").TextContent, StringComparison.Ordinal);
        Assert.Single(drawers.FindAll("[data-entry='1'] [data-action=reload-entry]"));
        Assert.True(drawers.Find("[data-action=apply-draft]").HasAttribute("disabled"));
        Assert.NotNull(config.Draft);
    }

    [Fact]
    public async Task ABlockedEntry_DisablesApplyAndEachOneLinksToItsReload()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        config.Document = new ConfigDocument(Upstreams: [App]);
        config.Draft = DraftOf(
            Entry(1, new UpsertUpstreamCommand(App with { Name = "mine" }), DraftEntryState.ChangedElsewhere) with { Reason = "Its object was changed or removed elsewhere since you added it." },
            Entry(2, new UpsertRateLimitPolicyCommand(Bulk with { Version = 0 })));
        config.WorkingDocument = new ConfigDocument(Upstreams: [App], RateLimitPolicies: [Bulk]);
        var (drawers, _) = await OpenReviewAsync(context);

        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-review=blocked]").Count == 1);
        Assert.Equal("1 change is changed elsewhere, so nothing can be applied.", drawers.Find("[data-review=blocked] strong").TextContent.Trim());
        Assert.True(drawers.Find("[data-action=apply-draft]").HasAttribute("disabled"));
        Assert.Empty(config.DraftApplies);

        await drawers.Find("[data-entry='1'] [data-action=reload-entry]").ClickAsync(new());

        Assert.Equal([2], config.Draft!.Entries.Select(e => e.Number));
        Assert.EndsWith(DashboardRoutes.Upstream(App.Id), context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri, StringComparison.Ordinal);
    }

    // ---- the forms and the review sheet ----

    private static async Task<IRenderedComponent<DrawerHost>> OpenRateLimitFormAsync(DashboardTestContext context)
    {
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        context.Render<RateLimits>(parameters => parameters.Add(p => p.Key, Bulk.Id.ToString()));
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=rate-limit] input[data-field=name]").Count == 1);
        return drawers;
    }

    private static async Task SubmitFormAsync(IRenderedComponent<DrawerHost> drawers)
    {
        await Type(drawers, "[data-drawer=rate-limit] input[data-field=name]", "Bulk traffic");

        // The form's submit waits for the review sheet to be answered, so it is not awaited here.
        _ = drawers.Find("form[data-drawer=rate-limit]").SubmitAsync();
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=review]").Count == 1 && drawers.FindAll("[data-review-phase=loading]").Count == 0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AFormHasOneReviewChangesButton_WhetherOrNotADraftIsOpen(bool draftOpen)
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        config.Document = new ConfigDocument(RateLimitPolicies: [Bulk]);
        config.Draft = draftOpen ? DraftOf(Entry(1, new UpsertUpstreamCommand(App))) : null;

        var drawers = await OpenRateLimitFormAsync(context);

        Assert.Equal("Review changes", Assert.Single(drawers.FindAll("[data-drawer=rate-limit] button[type=submit]")).TextContent.Trim());
        Assert.Empty(drawers.FindAll("[data-action=add-to-draft], [data-action=add-to-new-draft]"));
    }

    [Fact]
    public async Task WithADraftOpen_TheReviewSheetMakesAddToDraftThePrimary_AndItAddsTheChangeWithTheVersionTheFormOpenedWith()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        config.Document = new ConfigDocument(RateLimitPolicies: [Bulk]);
        config.Draft = DraftOf(Entry(1, new UpsertUpstreamCommand(App)));
        var drawers = await OpenRateLimitFormAsync(context);

        await SubmitFormAsync(drawers);

        var add = drawers.Find("[data-drawer=review] [data-action=add-to-draft]");
        var apply = drawers.Find("[data-drawer=review] [data-review=apply]");
        // The same look with or without a draft open (the owner's call): Apply now filled, Add to draft outlined beside it.
        Assert.Contains("mud-button-outlined", add.ClassName, StringComparison.Ordinal);
        Assert.Contains("mud-button-filled", apply.ClassName, StringComparison.Ordinal);
        Assert.Equal("Add to draft", add.TextContent.Trim());
        Assert.Equal("Apply now", apply.TextContent.Trim());
        await add.ClickAsync(new());

        await drawers.WaitForStateAsync(() => config.DraftAdds.Count == 1);
        var added = Assert.IsType<UpsertRateLimitPolicyCommand>(Assert.Single(config.DraftAdds[0]));
        Assert.Equal((Bulk.Id, 4L, "Bulk traffic"), (added.Policy.Id, added.Policy.Version, added.Policy.Name));
        Assert.DoesNotContain(config.Executed, command => !command.DryRun);
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=rate-limit]").Count == 0 && drawers.FindAll("[data-drawer=review]").Count == 0);
        Assert.Equal(2, config.Draft!.Entries.Count);
    }

    [Fact]
    public async Task WithNoDraft_ApplyNowIsThePrimaryInTheSameOrder_AndAddToDraftStartsOne()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        config.Document = new ConfigDocument(RateLimitPolicies: [Bulk]);
        var drawers = await OpenRateLimitFormAsync(context);

        await SubmitFormAsync(drawers);

        var buttons = drawers.FindAll("[data-drawer=review] .pdk-drawer-actions button").Select(b => b.GetAttribute("data-action") ?? b.GetAttribute("data-review")).ToList();
        Assert.Equal(["cancel", "add-to-draft", "apply"], buttons);
        Assert.Contains("mud-button-filled", drawers.Find("[data-drawer=review] [data-review=apply]").ClassName, StringComparison.Ordinal);
        Assert.Contains("mud-button-outlined", drawers.Find("[data-drawer=review] [data-action=add-to-draft]").ClassName, StringComparison.Ordinal);
        Assert.Empty(drawers.FindAll("[data-review=drops-entry]"));
        await drawers.Find("[data-drawer=review] [data-action=add-to-draft]").ClickAsync(new());

        await drawers.WaitForStateAsync(() => config.DraftAdds.Count == 1);
        Assert.NotNull(config.Draft);
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=rate-limit]").Count == 0);
    }

    [Fact]
    public async Task ApplyingNowSaysItDropsTheObjectsEntryFromTheDraft_AndDropsIt()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        config.Document = new ConfigDocument(RateLimitPolicies: [Bulk]);
        config.Draft = DraftOf(Entry(1, new UpsertRateLimitPolicyCommand(Bulk with { Name = "older" })), Entry(2, new UpsertUpstreamCommand(App)));
        var drawers = await OpenRateLimitFormAsync(context);

        await SubmitFormAsync(drawers);

        Assert.Equal("Applying now drops this object's entry from the draft.", drawers.Find("[data-review=drops-entry]").TextContent.Trim());
        await drawers.Find("[data-drawer=review] [data-review=apply]").ClickAsync(new());

        await drawers.WaitForStateAsync(() => config.Executed.Any(command => !command.DryRun));
        await drawers.WaitForStateAsync(() => config.Draft!.Entries.Count == 1);
        Assert.Equal([2], config.Draft!.Entries.Select(e => e.Number));
        Assert.Empty(config.DraftAdds);
    }

    [Fact]
    public async Task ARefusedAdd_ShowsTheErrorInTheSheetAndAtTheFormsField_AndLeavesBothOpen()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        config.Document = new ConfigDocument(RateLimitPolicies: [Bulk]);
        config.AddResult = _ => new DraftResult(
            DraftStatus.Refused,
            Command: new ConfigCommandResult(ConfigCommandStatus.Invalid, Errors: [new ConfigError("/name", "rateLimit.name.taken", "another rate limit has that name")]),
            FailedCommand: 0);
        var drawers = await OpenRateLimitFormAsync(context);
        await SubmitFormAsync(drawers);

        await drawers.Find("[data-drawer=review] [data-action=add-to-draft]").ClickAsync(new());

        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=review] [data-review=invalid]").Count == 1);
        Assert.Contains("another rate limit has that name", drawers.Find("[data-drawer=review] [data-review=invalid]").TextContent, StringComparison.OrdinalIgnoreCase);
        Assert.Single(drawers.FindAll("[data-drawer=rate-limit]"));
        Assert.Null(config.Draft);
    }

    [Fact]
    public async Task ADeleteFromTheFormGoesThroughTheSameSheet_AndAddToDraftMakesItAnEntry()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        config.Document = new ConfigDocument(RateLimitPolicies: [Bulk]);
        config.Draft = DraftOf(Entry(1, new UpsertUpstreamCommand(App)));
        var drawers = await OpenRateLimitFormAsync(context);
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=rate-limit] [data-action=delete]").Count == 1);

        _ = drawers.Find("[data-drawer=rate-limit] [data-action=delete]").ClickAsync(new());
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=review] [data-action=add-to-draft]").Count == 1);
        Assert.Contains("-error", drawers.Find("[data-drawer=review] [data-review=apply]").ClassName, StringComparison.Ordinal);
        await drawers.Find("[data-drawer=review] [data-action=add-to-draft]").ClickAsync(new());

        await drawers.WaitForStateAsync(() => config.DraftAdds.Count == 1);
        var delete = Assert.IsType<DeleteRateLimitPolicyCommand>(Assert.Single(config.DraftAdds[0]));
        Assert.Equal((Bulk.Id, 4L), (delete.RateLimitPolicyId, delete.Version));
    }

    [Fact]
    public async Task TheGlobalDefaultsHaveOneReviewChangesButton_AndTheSheetOffersTheDraft()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        config.Document = new ConfigDocument();
        config.Draft = DraftOf(Entry(1, new UpsertUpstreamCommand(App)));
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        var page = context.Render<GlobalDefaults>();
        await page.WaitForStateAsync(() => page.FindAll("form[data-form=defaults]").Count == 1);

        Assert.Equal("Review changes", page.Find("form[data-form=defaults] button[type=submit]").TextContent.Trim());
        Assert.Empty(page.FindAll("[data-action=add-to-draft]"));
        await page.Find("form[data-form=defaults]").SubmitAsync();
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=review] [data-action=add-to-draft]").Count == 1);
        await drawers.Find("[data-drawer=review] [data-action=add-to-draft]").ClickAsync(new());

        await page.WaitForStateAsync(() => config.DraftAdds.Count == 1);
        Assert.IsType<UpdateSettingsCommand>(Assert.Single(config.DraftAdds[0]));
        Assert.DoesNotContain(config.Executed, command => !command.DryRun);
    }

    // ---- marks and the read model ----

    [Fact]
    public async Task APageReadsTheHeadWithTheDraftApplied_AndMarksWhatTheDraftAdds()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        var drafted = App with { Id = UpstreamId.New(), Name = "from the draft", Version = 0 };
        config.Document = new ConfigDocument(Upstreams: [App]);
        config.WorkingDocument = new ConfigDocument(Upstreams: [App, drafted]);
        config.Draft = DraftOf(Entry(1, new UpsertUpstreamCommand(drafted)));
        context.Render<MudPopoverProvider>();
        context.Render<DrawerHost>();

        var page = context.Render<Upstreams>();

        await page.WaitForStateAsync(() => page.FindAll(".pdk-upstreams-table [data-chip=draft]").Count == 1);
        var row = page.FindAll(".pdk-dt-row").Single(r => r.TextContent.Contains("from the draft", StringComparison.Ordinal));
        Assert.Contains("Draft", row.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Draft", page.FindAll(".pdk-dt-row").Single(r => r.TextContent.Contains("app", StringComparison.Ordinal) && !ReferenceEquals(r, row)).TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APageFollowsTheDraftWhenItChangesInAnotherTab()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        var drafted = App with { Id = UpstreamId.New(), Name = "from the draft", Version = 0 };
        config.Document = new ConfigDocument(Upstreams: [App]);
        context.Render<MudPopoverProvider>();
        context.Render<DrawerHost>();
        var page = context.Render<Upstreams>();
        await page.WaitForStateAsync(() => page.FindAll(".pdk-upstreams-table").Count == 1);
        Assert.DoesNotContain("from the draft", page.Markup, StringComparison.Ordinal);

        config.WorkingDocument = new ConfigDocument(Upstreams: [App, drafted]);
        config.Draft = DraftOf(Entry(1, new UpsertUpstreamCommand(drafted)));
        await page.InvokeAsync(config.DraftChangedAsync);

        await page.WaitForStateAsync(() => page.Markup.Contains("from the draft", StringComparison.Ordinal));
        Assert.Single(page.FindAll("[data-chip=draft]"));
    }

    [Fact]
    public async Task APickerMarksTheObjectsTheDraftAdds()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        config.Draft = DraftOf(Entry(1, new UpsertRateLimitPolicyCommand(Bulk with { Version = 0 })));
        var popovers = context.Render<MudPopoverProvider>();
        await LoadedAsync(context);

        var field = context.Render<RateLimitField>(parameters => parameters
            .Add(p => p.Policies, [Bulk with { Version = 0 }])
            .Add(p => p.Inherited, null)
            .Add(p => p.Label, "Rate limit"));
        await field.InvokeAsync(() => field.FindComponent<MudSelect<string>>().Instance.OpenMenu());

        await popovers.WaitForStateAsync(() => popovers.FindAll("[data-chip=draft]").Count == 1);
        Assert.Contains("Bulk", popovers.Find("[data-chip=draft]").ParentElement!.TextContent, StringComparison.Ordinal);
    }

    private static async Task Type(IRenderedComponent<DrawerHost> drawers, string selector, string value)
    {
        await drawers.Find(selector).InputAsync(new() { Value = value });
        await drawers.Find(selector).BlurAsync(new());
    }
}
