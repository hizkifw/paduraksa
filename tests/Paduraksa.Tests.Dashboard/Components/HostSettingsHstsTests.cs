using Bunit;

using MudBlazor;

using Paduraksa.Application.Compilation;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Config;
using Paduraksa.Dashboard.Pages;
using Paduraksa.Dashboard.SettingsArea;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>The HSTS group of the settings fields: the switch, the max-age choices, the preload pair, and
/// what "inherit" says the layer would get.</summary>
public sealed class HostSettingsHstsTests
{
    [Fact]
    public async Task Off_ShowsOnlyTheSwitch_AndTurningItOnShowsTheRest()
    {
        HostSettings settings = new();
        await using DashboardTestContext context = new();

        var fields = Render(context, settings, changed: value => settings = value);

        Assert.Single(fields.FindAll("[data-field=hsts-enabled]"));
        Assert.Empty(fields.FindAll("[data-field=hsts-max-age]"));
        Assert.Empty(fields.FindAll("[data-field=hsts-preload]"));

        await Set(fields.FindComponents<MudSelect<bool?>>().Single(s => s.Instance.Label == "HSTS"), true);
        fields.Render(parameters => parameters.Add(f => f.Settings, settings));

        Assert.Single(fields.FindAll("[data-field=hsts-max-age]"));
        Assert.Single(fields.FindAll("[data-field=hsts-include-subdomains]"));
        Assert.Single(fields.FindAll("[data-field=hsts-preload]"));
        Assert.Equal(true, settings.Hsts?.Enabled);
    }

    [Fact]
    public async Task OnAlready_ShowsTheRestWithoutTheLayerSettingIt()
    {
        // The layer below has HSTS on: the form shows the fields that inherit from it, not an empty group.
        HostSettings settings = new();
        await using DashboardTestContext context = new();

        var fields = Render(context, settings, inherited: Inherited(enabled: true));

        Assert.Single(fields.FindAll("[data-field=hsts-max-age]"));
        Assert.Contains("Inherit (1 year)", fields.Markup, StringComparison.Ordinal);
        Assert.True(settings.Hsts?.Enabled is null);
    }

    [Fact]
    public async Task OffWithItsOwnMaxAge_StillShowsTheAgeSoItsErrorShows()
    {
        // The validator refuses a nonsense number even with HSTS off, so the field has to be there to show it at.
        HostSettings settings = new() { Hsts = new HstsSettings { Enabled = false, MaxAge = 99_999_999 } };
        await using DashboardTestContext context = new();

        var fields = Render(
            context,
            settings,
            errors: Errors(new HstsSettings { Enabled = false, MaxAge = 99_999_999 }),
            showAll: true);

        Assert.Single(fields.FindAll("[data-field=hsts-max-age]"));
        Assert.Contains("max-age", Row(fields, "hsts-max-age-custom"), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fields.FindAll("[data-field=hsts-include-subdomains]"));
        Assert.Empty(fields.FindAll("[data-field=hsts-preload]"));
    }

    [Fact]
    public async Task TheSwitchRowIsTitledLikeItsControl()
    {
        // The heading already says HSTS; the row repeats the control's own label, as every row in the list does.
        await using DashboardTestContext context = new();

        var row = Row(Render(context, new HostSettings()), "hsts-enabled");

        Assert.Contains(">HSTS<", row, StringComparison.Ordinal);
        Assert.DoesNotContain("Strict transport security", row, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheInheritOptionsNameWhatWouldBeInherited()
    {
        HostSettings settings = new();
        await using DashboardTestContext context = new();

        var markup = Render(context, settings, inherited: Inherited(enabled: true, subdomains: true, preload: true, maxAge: HstsMaxAgePresets.SixMonths)).Markup;

        Assert.Contains("Inherit (6 months)", markup, StringComparison.Ordinal);
        Assert.Contains("Inherit (on)", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACustomChoiceWithNoSeconds_GoesBackToInheritWhenTheLayerIsReset()
    {
        // "Custom with nothing typed" and "Inherit" both leave no max-age: only a fresh record says the form was reset.
        HostSettings settings = new() { Hsts = new HstsSettings { Enabled = true } };
        await using DashboardTestContext context = new();
        var fields = Render(context, settings, changed: value => settings = value);

        await Set(fields.FindComponents<MudSelect<HstsMaxAgeChoice>>().Single(s => s.Instance.Label == "Remembered for"), HstsMaxAgeChoice.Custom);
        Assert.Single(fields.FindAll("[data-field=hsts-max-age-custom]"));

        // The same bytes in a new record: the layer below was reloaded, so the choice follows it back to Inherit.
        HostSettings fresh = new() { Hsts = new HstsSettings { Enabled = true } };
        fields.Render(parameters => parameters.Add(f => f.Settings, fresh));

        var select = fields.Find("[data-field=hsts-max-age]").TextContent;
        Assert.Contains("Inherit (1 year)", select, StringComparison.Ordinal);
        Assert.DoesNotContain("Custom seconds", select, StringComparison.Ordinal);
        Assert.Empty(fields.FindAll("[data-field=hsts-max-age-custom]"));
    }

    [Fact]
    public async Task TheValidationErrorShowsAtThePreloadField()
    {
        HostSettings settings = new() { Hsts = new HstsSettings { Enabled = true, MaxAge = 300, Preload = true } };
        await using DashboardTestContext context = new();

        var fields = Render(
            context,
            settings,
            errors: Errors(new HstsSettings { Enabled = true, MaxAge = 300, Preload = true }),
            showAll: true);

        var preload = Row(fields, "hsts-preload");
        Assert.Contains("includeSubDomains", preload, StringComparison.Ordinal);
        Assert.Contains("mud-input-error", preload, StringComparison.Ordinal);

        // The max-age's own number field carries its refusal.
        HostSettings tooLong = new() { Hsts = new HstsSettings { Enabled = true, MaxAge = 99_999_999 } };
        var numeric = Render(
            context,
            tooLong,
            errors: Errors(new HstsSettings { Enabled = true, MaxAge = 99_999_999 }),
            showAll: true);
        Assert.Contains("max-age", Row(numeric, "hsts-max-age-custom"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheSummarySaysWhatTheLayerSets()
    {
        Assert.Equal(
            ["HSTS: 1 year, subdomains, preload"],
            SettingsSummary.Summary(new HostSettings { Hsts = new HstsSettings { Enabled = true, IncludeSubDomains = true, Preload = true } }));
        Assert.Equal(["HSTS off"], SettingsSummary.Summary(new HostSettings { Hsts = new HstsSettings { Enabled = false } }));
        Assert.Empty(SettingsSummary.Summary(new HostSettings { Hsts = new HstsSettings() }));
        Assert.Equal("0 seconds", SettingsSummary.AgeText(0));

        // Values without the switch only take effect if something below turns HSTS on, and the summary says so.
        Assert.Equal(["HSTS when on: 5 minutes"], SettingsSummary.Summary(new HostSettings { Hsts = new HstsSettings { MaxAge = 300 } }));
        Assert.Equal(
            ["HSTS when on: 1 year, subdomains"],
            SettingsSummary.Summary(new HostSettings { Hsts = new HstsSettings { IncludeSubDomains = true } }));
    }

    [Fact]
    public async Task AnInheritedZeroReadsAsSeconds()
    {
        // 0 is a value, not a word: "Inherit (0 seconds)" says which age it would inherit.
        await using DashboardTestContext context = new();

        var markup = Render(context, new HostSettings(), inherited: Inherited(enabled: true, maxAge: 0)).Markup;

        Assert.Contains("Inherit (0 seconds)", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGlobalDefaultsChangeGoesThroughReview()
    {
        await using DashboardTestContext context = new();
        var (_, _, config, _) = IdentityPages.SignIn(context, Role.Operator);
        config.Document = new ConfigDocument(Settings: new GlobalSettings(Version: 3));
        context.Render<MudPopoverProvider>();
        context.Render<Paduraksa.Dashboard.Drawers.DrawerHost>();
        var page = context.Render<GlobalDefaults>();
        await page.WaitForStateAsync(() => page.FindAll("[data-form=defaults]").Count == 1);

        await Set(page.FindComponents<MudSelect<bool?>>().Single(s => s.Instance.Label == "HSTS"), true);
        await page.Find("[data-action=review]").ClickAsync(new());

        await page.WaitForStateAsync(() => config.Executed.Count == 1);
        var command = Assert.IsType<UpdateSettingsCommand>(config.Executed[0]);
        Assert.Equal(3, command.Settings.Version);
        Assert.Equal(new HstsSettings { Enabled = true }, command.Settings.Defaults.Hsts);
    }

    /// <summary>The settings row around a field: the row MudBlazor puts the error text and class in.</summary>
    private static string Row(IRenderedComponent<HostSettingsFields> fields, string field)
    {
        var element = fields.Find($"[data-field={field}]");
        while (!element.ClassList.Contains("pdk-setting") && element.ParentElement is { } parent)
        {
            element = parent;
        }

        return element.OuterHtml;
    }

    private static HostSettings Inherited(bool enabled, bool subdomains = false, bool preload = false, int? maxAge = null) => new()
    {
        ForceHttps = false,
        Tls = new TlsSettings { MinimumVersion = TlsMinimumVersion.Tls12, TcpAlpn = TcpAlpn.Http2AndHttp11, Http3 = true },
        Hsts = new HstsSettings { Enabled = enabled, MaxAge = maxAge ?? HstsMaxAgePresets.OneYear, IncludeSubDomains = subdomains, Preload = preload },
    };

    /// <summary>The validator's errors for a host carrying <paramref name="hsts"/>, as a host form shows them.</summary>
    private static IReadOnlyDictionary<string, string> Errors(HstsSettings hsts) =>
        InlineValidation.ForHost(
            new ConfigDocument(),
            new HostConfig(HostId.New(), ["h.example.com"], Kind: HostKind.Respond, Respond: new RespondConfig(200), Settings: new HostSettings { Hsts = hsts }));

    private static IRenderedComponent<HostSettingsFields> Render(
        DashboardTestContext context,
        HostSettings settings,
        HostSettings? inherited = null,
        IReadOnlyDictionary<string, string>? errors = null,
        bool showAll = false,
        Action<HostSettings>? changed = null) =>
        context.Render<HostSettingsFields>(parameters => parameters
            .Add(f => f.Settings, settings)
            .Add(f => f.SettingsChanged, (Action<HostSettings>)(value => changed?.Invoke(value)))
            .Add(f => f.Inherited, inherited ?? HostSettingsLayers.BuiltIn)
            .Add(f => f.InheritWord, "Inherit")
            .Add(f => f.PathPrefix, "/settings")
            .Add(f => f.Errors, errors ?? new Dictionary<string, string>())
            .Add(f => f.ShowAll, showAll));

    private static Task Set<T>(IRenderedComponent<MudSelect<T>> select, T value) => select.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync(value));
}
