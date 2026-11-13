using Bunit;

using MudBlazor;

using Paduraksa.Application.ErrorPages;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Simulation;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Components;
using Paduraksa.Dashboard.Config;
using Paduraksa.Dashboard.Drawers;
using Paduraksa.Dashboard.Pages;
using Paduraksa.Dashboard.SettingsArea;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>The error pages' preview, the maintenance toggle and badge, and the simulator's word on the page: the pieces beyond one field per setting.</summary>
public sealed class ErrorPagesAndMaintenanceTests
{
    private static readonly UpstreamConfig App = new(UpstreamId.New(), "app", Version: 1, Destinations: [new DestinationConfig(DestinationId.New(), "http://app:8080")]);

    private static readonly HostConfig Host = new(HostId.New(), ["app.example.com"], Version: 2, Routes: [new RouteConfig(RouteId.New(), App.Id)]);

    [Fact]
    public async Task APreviewIsTheProxysOwnRenderingInAFrameThatCannotRunScript()
    {
        await using DashboardTestContext context = new();
        ErrorPageContent page = new() { Heading = "{status} {statusText}", Text = "Seen at {host}" };
        var fields = RenderField(context, new HostSettings { ErrorPages = new ErrorPagesSettings { NotFound = page } });

        Assert.Empty(fields.FindAll("[data-field=error-page-404-frame]"));
        await fields.Find("[data-field=error-page-404-preview]").ClickAsync(new());

        var frame = fields.Find("[data-field=error-page-404-frame]");
        Assert.Equal(string.Empty, frame.GetAttribute("sandbox"));
        Assert.Equal("no-referrer", frame.GetAttribute("referrerpolicy"));

        // The same bytes the API's preview, and the proxy's writer, give for the page.
        var (preview, errors) = ErrorPagePreviewer.Render(new ErrorPagePreviewRequest(page, 404));
        Assert.Empty(errors);
        Assert.Equal(preview!.Html, frame.GetAttribute("srcdoc"));
        Assert.Contains("<h1>404 Not Found</h1>", frame.GetAttribute("srcdoc"), StringComparison.Ordinal);

        await fields.Find("[data-field=error-page-404-preview]").ClickAsync(new());
        Assert.Empty(fields.FindAll("[data-field=error-page-404-frame]"));
    }

    [Fact]
    public async Task EachPageRowIsTitledWithTheStatusTheProxySends()
    {
        await using DashboardTestContext context = new();
        var fields = RenderField(context, new HostSettings());

        Assert.Contains("431 Request Header Fields Too Large", fields.Markup, StringComparison.Ordinal);
        Assert.Contains("413 Content Too Large", fields.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADocumentsPreviewKeepsItsOwnStyleAndNoScriptRuns()
    {
        await using DashboardTestContext context = new();
        const string Html = "<style>p{color:#900}</style><p>{status} for {host}</p>";
        var fields = RenderField(context, new HostSettings { ErrorPages = new ErrorPagesSettings { ServiceUnavailable = new ErrorPageContent { Mode = ErrorPageMode.Html, Html = Html } } });

        await fields.Find("[data-field=error-page-503-preview]").ClickAsync(new());

        var frame = fields.Find("[data-field=error-page-503-frame]");
        Assert.Equal("<style>p{color:#900}</style><p>503 for app.example.com</p>", frame.GetAttribute("srcdoc"));
        Assert.Equal(string.Empty, frame.GetAttribute("sandbox"));
        Assert.Contains("HTML document", fields.Find("[data-field=error-page-503]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APageThatCouldNotBeSavedSaysSoInsteadOfPreviewing()
    {
        await using DashboardTestContext context = new();
        var fields = RenderField(context, new HostSettings { ErrorPages = new ErrorPagesSettings { NotFound = new ErrorPageContent { Heading = "{nope}" } } });

        await fields.Find("[data-field=error-page-404-preview]").ClickAsync(new());

        Assert.Empty(fields.FindAll("[data-field=error-page-404-frame]"));
        var problem = fields.Find("[data-field=error-page-404-preview-error]").TextContent;
        Assert.Contains("can't be previewed until it is valid", problem, StringComparison.Ordinal);
        Assert.Contains("{nope}", problem, StringComparison.Ordinal);
        Assert.Contains("{status}, {statusText}, {requestId}, {host}, {cfRay}", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInheritedPageIsPreviewedAndTheBuiltInOneIsNot()
    {
        await using DashboardTestContext context = new();
        var inherited = new HostSettings { ErrorPages = new ErrorPagesSettings { NotFound = new ErrorPageContent { Heading = "From below" } } };
        var fields = context.Render<HostSettingsFields>(parameters => parameters
            .Add(f => f.Settings, new HostSettings())
            .Add(f => f.Inherited, inherited)
            .Add(f => f.PathPrefix, "/settings"));

        // The layer sets nothing for 404, so the preview shows what it inherits; a status nobody set has no page of its own to show.
        await fields.Find("[data-field=error-page-404-preview]").ClickAsync(new());
        Assert.Contains("<h1>From below</h1>", fields.Find("[data-field=error-page-404-frame]").GetAttribute("srcdoc"), StringComparison.Ordinal);
        Assert.Empty(fields.FindAll("[data-field=error-page-403-preview]"));
    }

    [Fact]
    public async Task SwitchingTheKindKeepsOnlyWhatTheNewKindHasAndTypingSetsEachField()
    {
        await using DashboardTestContext context = new();
        HostSettings current = new();
        var fields = context.Render<HostSettingsFields>(parameters => parameters
            .Add(f => f.Settings, current)
            .Add(f => f.SettingsChanged, (Action<HostSettings>)(value => current = value))
            .Add(f => f.Inherited, new HostSettings())
            .Add(f => f.PathPrefix, "/settings"));

        await fields.InvokeAsync(() => fields.FindComponents<MudSelect<ErrorPageMode?>>().Single(s => s.Instance.Label == "404 page").Instance.ValueChanged.InvokeAsync(ErrorPageMode.Message));
        await InputOf(fields, "error-page-404-heading").ChangeAsync(new() { Value = "Not here" });
        await InputOf(fields, "error-page-404-text").ChangeAsync(new() { Value = "Try the front page." });
        Assert.Equal(new ErrorPageContent { Mode = ErrorPageMode.Message, Heading = "Not here", Text = "Try the front page." }, current.ErrorPages!.NotFound);

        // A document can't carry a heading beside it (the validator refuses the pair), so the switch drops them.
        await fields.InvokeAsync(() => fields.FindComponents<MudSelect<ErrorPageMode?>>().Single(s => s.Instance.Label == "404 page").Instance.ValueChanged.InvokeAsync(ErrorPageMode.Html));
        Assert.Equal(new ErrorPageContent { Mode = ErrorPageMode.Html }, current.ErrorPages!.NotFound);
        await InputOf(fields, "error-page-404-html").ChangeAsync(new() { Value = "<p>{status}</p>" });
        Assert.Equal(new ErrorPageContent { Mode = ErrorPageMode.Html, Html = "<p>{status}</p>" }, current.ErrorPages!.NotFound);
        Assert.Contains("{status}, {statusText}, {requestId}, {host}, {cfRay}", fields.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheMaintenancePagePreviewsWithTheStatusTheHostAnswers()
    {
        await using DashboardTestContext context = new();
        var settings = new HostSettings
        {
            ErrorPages = new ErrorPagesSettings { Maintenance = new ErrorPageContent { Heading = "{status} {statusText}" } },
            Maintenance = new MaintenanceSettings { Status = 200 },
        };
        var fields = RenderField(context, settings);

        await fields.Find("[data-field=error-page-maintenance-preview]").ClickAsync(new());

        Assert.Contains("<h1>200 OK</h1>", fields.Find("[data-field=error-page-maintenance-frame]").GetAttribute("srcdoc"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheMaintenanceBypassOffersTheListsAndNamesNobodyWhenNoneIsSet()
    {
        var summary = SettingsSummary.Summary(new HostSettings { Maintenance = new MaintenanceSettings { Enabled = true, Status = 503, RetryAfterSeconds = 120 } });
        Assert.Equal(["In maintenance (503, retry after 120 s)"], summary);
        Assert.Equal(["Not in maintenance"], SettingsSummary.Summary(new HostSettings { Maintenance = new MaintenanceSettings { Enabled = false } }));
        Assert.Equal(["Maintenance when on (200)"], SettingsSummary.Summary(new HostSettings { Maintenance = new MaintenanceSettings { Status = 200 } }));
        Assert.Equal(
            ["2 error pages, maintenance page, upstream errors: replace every error"],
            SettingsSummary.Summary(new HostSettings
            {
                ErrorPages = new ErrorPagesSettings
                {
                    NotFound = new ErrorPageContent { Heading = "a" },
                    BadGateway = new ErrorPageContent { Heading = "b" },
                    Maintenance = new ErrorPageContent { Heading = "c" },
                    ReplaceUpstream = UpstreamErrorReplacement.Any,
                },
            }));
    }

    [Fact]
    public void TheHostsListKnowsMaintenanceFromTheLayerThatSetsItAndNeverForAnAdminHost()
    {
        ProfileId lan = ProfileId.New();
        ConfigDocument Document(HostSettings? defaults, HostSettings? profile, HostSettings? host, HostKind kind = HostKind.Proxy) => new(
            Settings: new GlobalSettings(Defaults: defaults),
            Profiles: [new ProfileConfig(lan, "LAN", Settings: profile)],
            Upstreams: [App],
            Hosts: [Host with { Kind = kind, ProfileId = lan, Settings = host ?? new HostSettings() }]);
        HostSettings on = new() { Maintenance = new MaintenanceSettings { Enabled = true } };
        HostSettings off = new() { Maintenance = new MaintenanceSettings { Enabled = false } };

        bool InMaintenance(ConfigDocument document) => HostOverview.Rows(document, null, [], DateTimeOffset.UnixEpoch).Single().InMaintenance;

        Assert.False(InMaintenance(Document(null, null, null)));
        Assert.True(InMaintenance(Document(on, null, null)));
        Assert.True(InMaintenance(Document(null, on, null)));
        Assert.True(InMaintenance(Document(null, null, on)));
        Assert.False(InMaintenance(Document(on, null, off)));
        Assert.False(InMaintenance(Document(on, off, null)));
        Assert.True(InMaintenance(Document(off, on, null)));
        Assert.False(InMaintenance(Document(on, null, null, HostKind.Admin)));
    }

    [Fact]
    public async Task AHostInMaintenanceShowsItInTheListAndTheWrenchEndsItThroughReview()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        config.Document = new ConfigDocument(Upstreams: [App], Hosts: [Host with { Settings = new HostSettings { Maintenance = new MaintenanceSettings { Enabled = true, Status = 503 } } }]);
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();

        var page = context.Render<Hosts>();
        await page.WaitForStateAsync(() => page.FindAll(".pdk-hosts-table").Count == 1);

        var status = page.Find("[data-host='app.example.com'] .pdk-dt-status").TextContent;
        Assert.Contains("Maintenance", status, StringComparison.Ordinal);
        Assert.DoesNotContain("Online", status, StringComparison.Ordinal);

        // The click's handler waits for the review to be answered, so it is not awaited here.
        var clicked = page.Find("[data-action=maintenance]").ClickAsync(new());
        await drawers.WaitForStateAsync(() => drawers.Markup.Contains("End maintenance of app.example.com", StringComparison.Ordinal));
        var reviewed = Assert.IsType<UpsertHostCommand>(Assert.Single(config.Executed));
        Assert.Equal(new MaintenanceSettings { Enabled = false, Status = 503 }, reviewed.Host.Settings.Maintenance);
        Assert.True(reviewed.DryRun);
    }

    [Fact]
    public async Task AHostNotInMaintenanceShowsOnlineAndTheWrenchPutsItThere()
    {
        await using DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Operator);
        config.Document = new ConfigDocument(Upstreams: [App], Hosts: [Host]);
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();

        var page = context.Render<Hosts>();
        await page.WaitForStateAsync(() => page.FindAll(".pdk-hosts-table").Count == 1);
        Assert.DoesNotContain("Maintenance", page.Find("[data-host='app.example.com'] .pdk-dt-status").TextContent, StringComparison.Ordinal);

        // Switching it on asks for the mode's options first, prefilled, and then the review sheet follows.
        _ = page.Find("[data-action=maintenance]").ClickAsync(new());
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=maintenance] [data-field=maintenance-status]").Count == 1);
        _ = drawers.Find("[data-drawer=maintenance] [data-action=review]").ClickAsync(new());
        await drawers.WaitForStateAsync(() => drawers.Markup.Contains("Put app.example.com in maintenance", StringComparison.Ordinal) && drawers.FindAll("[data-drawer=review]").Count == 1);
        var reviewed = Assert.IsType<UpsertHostCommand>(Assert.Single(config.Executed));
        Assert.Equal(new MaintenanceSettings { Enabled = true }, reviewed.Host.Settings.Maintenance);
    }

    [Fact]
    public async Task AReaderAndAnAdminHostAreNotOfferedTheWrench()
    {
        await using DashboardTestContext viewer = new();
        ConfigPages.SignIn(viewer, Role.Viewer).Document = new ConfigDocument(Upstreams: [App], Hosts: [Host]);
        var viewerPage = viewer.Render<Hosts>();
        await viewerPage.WaitForStateAsync(() => viewerPage.FindAll(".pdk-hosts-table").Count == 1);
        Assert.Empty(viewerPage.FindAll("[data-action=maintenance]"));

        await using DashboardTestContext admin = new();
        ConfigPages.SignIn(admin, Role.Operator).Document = new ConfigDocument(Hosts: [new HostConfig(HostId.New(), ["admin.example.com"], Kind: HostKind.Admin)]);
        var adminPage = admin.Render<Hosts>();
        await adminPage.WaitForStateAsync(() => adminPage.FindAll(".pdk-hosts-table").Count == 1);
        Assert.Empty(adminPage.FindAll("[data-action=maintenance]"));
    }

    [Fact]
    public async Task TheSimulatorSaysWhichPageABrowserGetsAndFromWhichLayer()
    {
        await using DashboardTestContext context = new();
        (SimulationErrorPage? Page, string? Expected)[] cases =
        [
            (new SimulationErrorPage(false, ErrorPageSource.BuiltIn, ErrorPageMode.Message), "built-in page"),
            (new SimulationErrorPage(true, ErrorPageSource.Host, ErrorPageMode.Message), "custom message from the host"),
            (new SimulationErrorPage(true, ErrorPageSource.Profile, ErrorPageMode.Html), "custom HTML document from the profile"),
            (new SimulationErrorPage(true, ErrorPageSource.GlobalDefaults, ErrorPageMode.Message), "custom message from the global defaults"),
            (null, null),
        ];
        foreach (var (page, expected) in cases)
        {
            SimulationResult result = new(
                SimulationStatus.Simulated,
                new SimulationSource(SimulationTargetKind.Live),
                new SimulationOutcome(SimulationOutcomeKind.FixedAnswer, "answered", 404, ErrorPage: page),
                [new SimulationStep(SimulationStepKind.Maintenance, SimulationStepVerdict.Stopped, "maintenance", [])]);

            var view = context.Render<SimulationView>(parameters => parameters.Add(v => v.Result, result));

            if (expected is null)
            {
                Assert.Empty(view.FindAll("[data-field=sim-page]"));
            }
            else
            {
                Assert.Contains(expected, view.Find("[data-field=sim-page]").TextContent, StringComparison.Ordinal);
            }

            Assert.Equal("Maintenance mode", view.Find(".pdk-trace-title").TextContent);
        }
    }

    /// <summary>The control's input: MudBlazor puts the unmatched <c>data-field</c> on the input itself or on its wrapper.</summary>
    private static AngleSharp.Dom.IElement InputOf(IRenderedComponent<HostSettingsFields> fields, string field)
    {
        var element = fields.Find($"[data-field={field}]");
        return element.LocalName is "input" or "textarea" ? element : element.QuerySelector("input, textarea")!;
    }

    private static IRenderedComponent<HostSettingsFields> RenderField(DashboardTestContext context, HostSettings settings) =>
        context.Render<HostSettingsFields>(parameters => parameters
            .Add(f => f.Settings, settings)
            .Add(f => f.Inherited, new HostSettings())
            .Add(f => f.PathPrefix, "/settings"));
}
