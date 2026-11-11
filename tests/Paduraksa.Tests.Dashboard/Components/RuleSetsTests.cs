using Bunit;

using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

using MudBlazor;

using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard;
using Paduraksa.Dashboard.Drawers;
using Paduraksa.Dashboard.Pages;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>
/// The rule sets: a page that says what each set does and who applies it, the built-in presets beside them (read-only, copied to
/// edit), an editor whose rules read "When [path] [methods] [user agent] then [action]" and can be added, moved and removed, and
/// create, edit and delete through the same review the other objects use. A set in use offers no delete.
/// </summary>
public sealed class RuleSetsTests
{
    private static readonly RuleSetConfig Scanners = new(
        RuleSetId.New(),
        "Scanners",
        Notes: "Known noise",
        Rules:
        [
            new RuleConfig(RuleAction.Allow, new RuleMatchConfig(new RulePathConfig(RulePathKind.Prefix, "/health"))),
            new RuleConfig(RuleAction.Deny, new RuleMatchConfig(new RulePathConfig(RulePathKind.Prefix, "/admin"), ["POST", "PUT"], new RuleUserAgentConfig(["sqlmap", "nikto"], Empty: true))),
        ]);

    private static readonly RuleSetConfig Empty = new(RuleSetId.New(), "Empty");

    [Fact]
    public async Task ThePage_ListsEachSetWithItsRulesAndTheHostsAndRoutesThatUseIt()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        HostConfig host = new(
            HostId.New(), ["nas.home.test"], Routes: [new RouteConfig(RouteId.New(), UpstreamId.New(), Path: "/admin", Settings: new RouteSettings { RuleSet = Use(Empty.Id) })],
            Settings: new HostSettings { RuleSet = Use(Scanners.Id) });
        config.Document = new ConfigDocument(RuleSets: [Scanners, Empty], Hosts: [host]);

        var page = context.Render<RuleSets>();

        await page.WaitForStateAsync(() => page.FindAll("[data-rule-set]").Count == 2);
        var scanners = page.Find("[data-rule-set=Scanners]");
        Assert.Contains("2 rules: 1 deny, 1 allow", scanners.TextContent.Replace("allow, 1 deny", "deny, 1 allow", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("1 host", scanners.TextContent, StringComparison.Ordinal);
        Assert.Contains("nas.home.test", scanners.TextContent, StringComparison.Ordinal);
        var empty = page.Find("[data-rule-set=Empty]");
        Assert.Contains("no rules", empty.TextContent, StringComparison.Ordinal);
        Assert.Contains("1 route", empty.TextContent, StringComparison.Ordinal);
        Assert.Contains("/admin", empty.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASetInUse_OffersNoDelete_AndAnUnusedOneDoes()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        config.Document = new ConfigDocument(RuleSets: [Scanners, Empty], Hosts: [new HostConfig(HostId.New(), ["nas.home.test"], Settings: new HostSettings { RuleSet = Use(Scanners.Id) })]);

        var page = context.Render<RuleSets>();

        await page.WaitForStateAsync(() => page.FindAll("[data-rule-set]").Count == 2);
        Assert.Empty(page.FindAll("[data-rule-set=Scanners] [data-action=delete]"));
        Assert.Single(page.FindAll("[data-rule-set=Empty] [data-action=delete]"));
    }

    [Fact]
    public async Task APageWithoutSets_SaysWhatOneIsForAndStillShowsThePresets()
    {
        await using DashboardTestContext context = new();
        ConfigPages.SignIn(context, Role.Operator).Document = new ConfigDocument();

        var page = context.Render<RuleSets>();

        await page.WaitForStateAsync(() => page.FindAll("[data-section=rule-sets] .pdk-empty").Count == 1);
        Assert.Contains("No rule sets yet", page.Find(".pdk-empty").TextContent, StringComparison.Ordinal);
        Assert.Equal(["Block AI crawlers", "Block known scanners", "Block empty user agent"], page.FindAll("[data-preset]").Select(row => row.GetAttribute("data-preset")));
    }

    [Fact]
    public async Task ThePresets_AreReadOnlyWithTheirReasonAndACopyToEditLink()
    {
        await using DashboardTestContext context = new();
        ConfigPages.SignIn(context, Role.Operator).Document = new ConfigDocument(
            Hosts: [new HostConfig(HostId.New(), ["nas.home.test"], Settings: new HostSettings { RuleSet = Use(RuleSetPresets.KnownScanners.Id) })]);

        var page = context.Render<RuleSets>();

        await page.WaitForStateAsync(() => page.FindAll("[data-preset]").Count == 3);
        var scanners = page.Find("[data-preset='Block known scanners']");
        Assert.Contains("sqlmap", scanners.TextContent, StringComparison.Ordinal);
        Assert.Contains("1 host", scanners.TextContent, StringComparison.Ordinal);
        Assert.Equal(DashboardRoutes.CopyRuleSet(RuleSetPresets.KnownScanners.Id), scanners.QuerySelector("[data-action=copy-preset]")!.GetAttribute("href"));
        Assert.All(page.FindAll("[data-preset]"), row => Assert.Empty(row.QuerySelectorAll("[data-action=delete]")));
        Assert.Contains("1 rule: deny", page.Find("[data-preset='Block empty user agent']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACopyOfAPreset_StartsANewSetWithItsRulesAndAFreshID()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        config.Document = new ConfigDocument();
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(DashboardRoutes.CopyRuleSet(RuleSetPresets.AiCrawlers.Id));
        context.Render<RuleSets>(parameters => parameters.Add(p => p.Key, DashboardRoutes.NewSegment));

        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=rule-set] input[data-field=name]").Count == 1);
        Assert.Equal("Block AI crawlers (copy)", drawers.Find("[data-drawer=rule-set] input[data-field=name]").GetAttribute("value"));
        Assert.Single(drawers.FindAll("[data-rule]"));
        Assert.Contains("GPTBot", drawers.Find("textarea[data-field=agents-0]").TextContent, StringComparison.Ordinal);

        _ = drawers.Find("form[data-drawer=rule-set]").SubmitAsync();
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-review=apply]").Count == 1);
        var sent = Assert.IsType<UpsertRuleSetCommand>(config.Executed[0]).RuleSet;
        Assert.NotEqual(RuleSetPresets.AiCrawlers.Id, sent.Id);
        Assert.Equal(0L, sent.Version);
        Assert.Equal(RuleAction.Deny, Assert.Single(sent.Rules).Action);
        Assert.Equal(RobotsText.AiCrawlers.Order(StringComparer.Ordinal), sent.Rules[0].Match.UserAgent!.Contains);
    }

    [Fact]
    public async Task CreatingASet_SendsItsRulesInOrderThroughReview()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        config.Document = new ConfigDocument();
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        context.Render<RuleSets>(parameters => parameters.Add(p => p.Key, DashboardRoutes.NewSegment));

        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=rule-set] input[data-field=name]").Count == 1);
        await TypeAsync(drawers, "[data-drawer=rule-set] input[data-field=name]", "Block admin");
        Assert.Contains("No rules: every request goes on.", drawers.Find("[data-section=no-rules]").TextContent, StringComparison.Ordinal);

        await drawers.Find("[data-action=add-rule]").ClickAsync(new());
        await ChooseKindAsync(drawers, 0, RulePathKind.Prefix);
        await TypeAsync(drawers, "[data-drawer=rule-set] input[data-field=pattern-0]", "/admin");
        await ChooseMethodsAsync(drawers, 0, "POST", "DELETE");
        await TypeAsync(drawers, "[data-drawer=rule-set] textarea[data-field=agents-0]", "sqlmap\nMozilla compatible");
        await ChooseActionAsync(drawers, 0, RuleAction.Drop);

        await drawers.Find("[data-action=add-rule]").ClickAsync(new());
        await ChooseMethodsAsync(drawers, 1, "TRACE");

        var reviewing = drawers.Find("form[data-drawer=rule-set]").SubmitAsync();
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-review=apply]").Count == 1);
        var dry = Assert.IsType<UpsertRuleSetCommand>(config.Executed[0]);
        Assert.True(dry.DryRun);
        var set = dry.RuleSet;
        Assert.Equal(("Block admin", 0L), (set.Name, set.Version));
        Assert.Equal(2, set.Rules.Count);
        var first = set.Rules[0];
        Assert.Equal(RuleAction.Drop, first.Action);
        Assert.Equal(new RulePathConfig(RulePathKind.Prefix, "/admin"), first.Match.Path);
        Assert.Equal(["DELETE", "POST"], first.Match.Methods);
        Assert.Equal(["sqlmap", "Mozilla compatible"], first.Match.UserAgent!.Contains);
        Assert.False(first.Match.UserAgent.Empty);
        Assert.Equal(RuleAction.Deny, set.Rules[1].Action);
        Assert.Equal(["TRACE"], set.Rules[1].Match.Methods);
        Assert.Null(set.Rules[1].Match.Path);
        Assert.Null(set.Rules[1].Match.UserAgent);

        await drawers.Find("[data-review=apply]").ClickAsync(new());
        await reviewing.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken);
        Assert.False(Assert.IsType<UpsertRuleSetCommand>(config.Executed[^1]).DryRun);
    }

    [Fact]
    public async Task EditingASet_ShowsItsRulesAndMovingOneChangesTheOrderSent()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        var stored = Scanners with { Version = 4 };
        config.Document = new ConfigDocument(RuleSets: [stored]);
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        context.Render<RuleSets>(parameters => parameters.Add(p => p.Key, stored.Id.ToString()));

        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-rule]").Count == 2);
        Assert.Equal("/health", drawers.Find("input[data-field=pattern-0]").GetAttribute("value"));
        Assert.Equal("/admin", drawers.Find("input[data-field=pattern-1]").GetAttribute("value"));
        Assert.True(drawers.Find("[data-action=move-up-0]").HasAttribute("disabled"));
        Assert.True(drawers.Find("[data-action=move-down-1]").HasAttribute("disabled"));

        await drawers.Find("[data-action=move-up-1]").ClickAsync(new());

        Assert.Equal("/admin", drawers.Find("input[data-field=pattern-0]").GetAttribute("value"));
        Assert.Equal("/health", drawers.Find("input[data-field=pattern-1]").GetAttribute("value"));
        _ = drawers.Find("form[data-drawer=rule-set]").SubmitAsync();
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-review=apply]").Count == 1);
        var edit = Assert.IsType<UpsertRuleSetCommand>(config.Executed[0]).RuleSet;
        Assert.Equal((stored.Id, 4L), (edit.Id, edit.Version));
        Assert.Equal(["/admin", "/health"], edit.Rules.Select(rule => rule.Match.Path!.Pattern));
        Assert.Equal([RuleAction.Deny, RuleAction.Allow], edit.Rules.Select(rule => rule.Action));
    }

    [Fact]
    public async Task RemovingARule_DropsItsRow()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        config.Document = new ConfigDocument(RuleSets: [Scanners with { Version = 2 }]);
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        context.Render<RuleSets>(parameters => parameters.Add(p => p.Key, Scanners.Id.ToString()));
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-rule]").Count == 2);

        await drawers.Find("[data-action=remove-rule-0]").ClickAsync(new());

        Assert.Single(drawers.FindAll("[data-rule]"));
        _ = drawers.Find("form[data-drawer=rule-set]").SubmitAsync();
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-review=apply]").Count == 1);
        Assert.Equal("/admin", Assert.Single(Assert.IsType<UpsertRuleSetCommand>(config.Executed[0]).RuleSet.Rules).Match.Path!.Pattern);
    }

    [Fact]
    public async Task TheForm_SaysWhatIsWrongAtTheFields_ARegexItsReasonAndAnEmptyRuleThatItMatchesEverything()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        config.Document = new ConfigDocument(RuleSets: [Empty]);
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        context.Render<RuleSets>(parameters => parameters.Add(p => p.Key, DashboardRoutes.NewSegment));
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=rule-set] input[data-field=name]").Count == 1);
        await TypeAsync(drawers, "[data-drawer=rule-set] input[data-field=name]", "empty");
        await drawers.Find("[data-action=add-rule]").ClickAsync(new());
        await drawers.Find("[data-action=add-rule]").ClickAsync(new());
        await ChooseKindAsync(drawers, 0, RulePathKind.Regex);
        await TypeAsync(drawers, "[data-drawer=rule-set] input[data-field=pattern-0]", "(unclosed");

        // Typing into the regex shows its reason at the field at once.
        Assert.Contains("can't be used", drawers.Find("[data-drawer=rule-set]").TextContent, StringComparison.Ordinal);

        _ = drawers.Find("form[data-drawer=rule-set]").SubmitAsync();
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-form=errors]").Count == 1);

        var text = drawers.Find("[data-drawer=rule-set]").TextContent;
        Assert.Contains("is already used", text, StringComparison.Ordinal);
        Assert.Contains("State at least one condition", text, StringComparison.Ordinal);
        Assert.Single(drawers.FindAll("[data-error=rule-1]"));
        Assert.Empty(drawers.FindAll("[data-error=rule-0]"));
        Assert.Empty(config.Executed);
    }

    [Fact]
    public async Task DeletingAnUnusedSet_AsksFirstAndSendsTheStoredVersion()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        var stored = Empty with { Version = 4 };
        config.Document = new ConfigDocument(RuleSets: [stored]);
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        var page = context.Render<RuleSets>();

        await page.WaitForStateAsync(() => page.FindAll("[data-rule-set] [data-action=delete]").Count == 1);
        var deleting = page.Find("[data-rule-set] [data-action=delete]").ClickAsync(new());
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-review=apply]").Count == 1);

        var dry = Assert.IsType<DeleteRuleSetCommand>(config.Executed[0]);
        Assert.True(dry.DryRun);
        Assert.Equal((stored.Id, 4L), (dry.RuleSetId, dry.Version));

        await drawers.Find("[data-review=apply]").ClickAsync(new());
        await deleting.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken);
        await drawers.WaitForStateAsync(() => config.Executed.Count == 2);
        Assert.False(Assert.IsType<DeleteRuleSetCommand>(config.Executed[1]).DryRun);
    }

    [Fact]
    public async Task AReadOnlyRole_SeesTheSetButCannotChangeIt()
    {
        await using DashboardTestContext context = new();
        ConfigPages.SignIn(context, Role.Viewer).Document = new ConfigDocument(RuleSets: [Scanners]);
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        var page = context.Render<RuleSets>(parameters => parameters.Add(p => p.Key, Scanners.Id.ToString()));
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-banner=read-only]").Count == 1);

        Assert.Empty(drawers.FindAll("[data-action=add-rule]"));
        Assert.Empty(drawers.FindAll("[data-action=remove-rule-0]"));
        Assert.Empty(drawers.FindAll("[data-action=review]"));
        Assert.Empty(page.FindAll("[data-action=add-rule-set]"));
        Assert.Empty(page.FindAll("[data-action=copy-preset]"));
    }

    private static RuleSetSettings Use(RuleSetId set) => new() { RuleSetId = set };

    /// <summary>Types into a MudBlazor field and leaves it (which sends what was typed at once, instead of after the field's debounce).</summary>
    private static async Task TypeAsync(IRenderedComponent<DrawerHost> drawers, string selector, string value)
    {
        await drawers.Find(selector).InputAsync(new() { Value = value });
        await drawers.Find(selector).BlurAsync(new());
    }

    private static Task ChooseKindAsync(IRenderedComponent<DrawerHost> drawers, int rule, RulePathKind kind) =>
        drawers.InvokeAsync(() => drawers.FindComponents<MudSelect<RulePathKind?>>()[rule].Instance.ValueChanged.InvokeAsync(kind));

    private static Task ChooseMethodsAsync(IRenderedComponent<DrawerHost> drawers, int rule, params string[] methods) =>
        drawers.InvokeAsync(() => drawers.FindComponents<MudSelect<string>>().Where(select => select.Instance.Label == "Methods").ElementAt(rule).Instance.SelectedValuesChanged.InvokeAsync(methods));

    private static Task ChooseActionAsync(IRenderedComponent<DrawerHost> drawers, int rule, RuleAction action) =>
        drawers.InvokeAsync(() => drawers.FindComponents<MudSelect<RuleAction>>()[rule].Instance.ValueChanged.InvokeAsync(action));
}
