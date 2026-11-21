using Bunit;

using Paduraksa.Application.Compilation;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Components;
using Paduraksa.Dashboard.Config;
using Paduraksa.Dashboard.SettingsArea;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>The honeypot's host settings in the form: what an empty path shows, where the trap can be opened, and where the rows aren't offered.</summary>
public sealed class HoneypotSettingsTests
{
    private static readonly HoneypotNames Derived = new("/archive-k7f3/", "Quiet Hours");

    [Fact]
    public async Task AnEmptyPathAndNameShowTheDerivedOnesAsTheirPlaceholders()
    {
        await using DashboardTestContext context = new();

        var fields = Fields(context, new HostSettings(), derived: Derived);

        Assert.Equal("/archive-k7f3/", PlaceholderOf(fields, "honeypot-path"));
        Assert.Equal("Quiet Hours", PlaceholderOf(fields, "honeypot-site-name"));
    }

    [Fact]
    public async Task APathTheLayerBelowSetsIsTheOneAnEmptyFieldShows_AndWithoutAHostTheDerivedOneIsNamedByItsSource()
    {
        await using DashboardTestContext context = new();
        HostSettings below = new() { Honeypot = new HoneypotSettings { Path = "/notes/", SiteName = "Notes" } };

        var inheriting = Fields(context, new HostSettings(), inherited: below, derived: Derived);
        var profile = Fields(context, new HostSettings());

        Assert.Equal("/notes/", PlaceholderOf(inheriting, "honeypot-path"));
        Assert.Equal("Notes", PlaceholderOf(inheriting, "honeypot-site-name"));
        Assert.Equal("Derived from the host", PlaceholderOf(profile, "honeypot-path"));
    }

    [Fact]
    public async Task OpenTrapLinksToTheEffectivePathOnTheHost_OnlyWhileTheTrapIsOn()
    {
        await using DashboardTestContext context = new();
        HostSettings on = new() { Honeypot = new HoneypotSettings { Enabled = true } };

        var derived = Fields(context, on, derived: Derived, origin: "https://app.example.com");
        var configured = Fields(context, new HostSettings { Honeypot = new HoneypotSettings { Enabled = true, Path = "/notes/" } }, derived: Derived, origin: "https://app.example.com");
        var off = Fields(context, new HostSettings(), derived: Derived, origin: "https://app.example.com");
        var noHost = Fields(context, on, derived: Derived);

        var link = derived.Find("[data-action=open-trap]");
        Assert.Equal("https://app.example.com/archive-k7f3/", link.GetAttribute("href"));
        Assert.Equal("_blank", link.GetAttribute("target"));
        Assert.Contains("noopener", link.GetAttribute("rel"), StringComparison.Ordinal);
        Assert.Equal("https://app.example.com/notes/", configured.Find("[data-action=open-trap]").GetAttribute("href"));
        Assert.Empty(off.FindAll("[data-action=open-trap]"));
        Assert.Empty(noHost.FindAll("[data-action=open-trap]"));
    }

    [Fact]
    public async Task TheLinkIsNotOfferedWithoutAKnownPath()
    {
        await using DashboardTestContext context = new();

        var fields = Fields(context, new HostSettings { Honeypot = new HoneypotSettings { Enabled = true } }, origin: "https://app.example.com");

        Assert.Empty(fields.FindAll("[data-action=open-trap]"));
    }

    [Fact]
    public async Task AnAdminHostIsNotOfferedTheTrap()
    {
        await using DashboardTestContext context = new();

        var fields = Fields(context, new HostSettings(), showHoneypot: false);

        Assert.Empty(fields.FindAll("[data-field=honeypot-enabled]"));
        Assert.Empty(fields.FindAll("[data-field=honeypot-path]"));
    }

    [Fact]
    public async Task TheHostFormTakesTheTrapOriginFromTheFirstPlainName_OverHttpsUnlessThereIsNoCertificate()
    {
        var upstream = UpstreamId.New();
        HostConfig stored = new(
            HostId.New(), ["*.example.com", "app.example.com"], Settings: new HostSettings { Honeypot = new HoneypotSettings { Enabled = true } },
            Routes: [new RouteConfig(RouteId.New(), upstream)]);
        await using DashboardTestContext context = new();

        var form = Form(context, HostEditModel.From(stored));
        var plain = Form(context, HostEditModel.From(stored with { Certificate = new CertificateBinding { Mode = CertificateBindingMode.None } }));
        var wildcard = Form(context, HostEditModel.From(stored with { Names = ["*.example.com"] }));
        var added = HostEditModel.New(upstream);
        added.Names[0].Value = "new.example.com";
        added.Settings = stored.Settings!;
        var unsaved = Form(context, added);

        Assert.Equal("https://app.example.com/archive-k7f3/", form.Find("[data-action=open-trap]").GetAttribute("href"));
        Assert.Equal("http://app.example.com/archive-k7f3/", plain.Find("[data-action=open-trap]").GetAttribute("href"));
        Assert.Empty(wildcard.FindAll("[data-action=open-trap]"));
        Assert.Empty(unsaved.FindAll("[data-action=open-trap]"));
    }

    [Fact]
    public async Task TheHostFormOffersTheRowsToEveryKindButAdmin()
    {
        var upstream = UpstreamId.New();
        await using DashboardTestContext context = new();

        var proxy = Form(context, HostEditModel.From(new HostConfig(HostId.New(), ["app.example.com"], Routes: [new RouteConfig(RouteId.New(), upstream)])));
        var admin = Form(context, HostEditModel.From(new HostConfig(HostId.New(), ["admin.example.com"], Kind: HostKind.Admin)));

        Assert.Single(proxy.FindAll("[data-field=honeypot-enabled]"));
        Assert.Empty(admin.FindAll("[data-field=honeypot-enabled]"));
    }

    [Fact]
    public void TheSummaryNamesWhatALayerSetsAndLeavesAnEmptyRecordOut()
    {
        Assert.Equal(["Honeypot on"], SettingsSummary.Summary(new HostSettings { Honeypot = new HoneypotSettings { Enabled = true } }));
        Assert.Equal(["Honeypot off"], SettingsSummary.Summary(new HostSettings { Honeypot = new HoneypotSettings { Enabled = false, PowShare = 5 } }));
        Assert.Equal(
            ["Honeypot on (/notes/, named \"Field Notes\", 40% behind proof of work, 18 bits, indexable)"],
            SettingsSummary.Summary(new HostSettings { Honeypot = new HoneypotSettings { Enabled = true, Path = "/notes/", SiteName = "Field Notes", PowShare = 40, PowDifficulty = 18, NoIndex = false } }));
        Assert.Equal(["Honeypot when on (noindex)"], SettingsSummary.Summary(new HostSettings { Honeypot = new HoneypotSettings { NoIndex = true } }));
        Assert.Empty(SettingsSummary.Summary(new HostSettings { Honeypot = new HoneypotSettings() }));
        Assert.Equal(2, HostSettingsEdit.CountSet(new HostSettings { Honeypot = new HoneypotSettings { Enabled = true, PowShare = 10 } }));
        Assert.Null(HostSettingsEdit.WithHoneypot(new HostSettings { Honeypot = new HoneypotSettings { Enabled = true } }, h => h with { Enabled = null }).Honeypot);
    }

    [Fact]
    public void TheDocumentsHoneypotWordsAreSpelledForADiffAndAnError()
    {
        Assert.Equal("Honeypot", FieldLabels.Of("honeypot"));
        Assert.Equal("Site name", FieldLabels.Of("siteName"));
        Assert.Equal("Share behind proof of work", FieldLabels.Of("powShare"));
        Assert.Equal("Proof-of-work difficulty", FieldLabels.Of("powDifficulty"));
        Assert.Equal("Noindex header", FieldLabels.Of("noIndex"));
        Assert.Equal("Honeypot", FieldLabels.Value("honeypot"));
    }

    private static string? PlaceholderOf(IRenderedComponent<HostSettingsFields> fields, string field)
    {
        var element = fields.Find($"[data-field={field}]");
        return (element.LocalName is "input" ? element : element.QuerySelector("input")!).GetAttribute("placeholder");
    }

    private static IRenderedComponent<HostSettingsFields> Fields(
        DashboardTestContext context, HostSettings settings, HostSettings? inherited = null, HoneypotNames? derived = null, string? origin = null, bool showHoneypot = true) =>
        context.Render<HostSettingsFields>(parameters => parameters
            .Add(f => f.Settings, settings)
            .Add(f => f.Inherited, inherited ?? new HostSettings())
            .Add(f => f.DerivedTrap, derived)
            .Add(f => f.TrapOrigin, origin)
            .Add(f => f.ShowHoneypot, showHoneypot));

    private static IRenderedComponent<HostForm> Form(DashboardTestContext context, HostEditModel model) =>
        context.Render<HostForm>(parameters => parameters
            .Add(f => f.Model, model)
            .Add(f => f.DerivedTrap, Derived));
}
