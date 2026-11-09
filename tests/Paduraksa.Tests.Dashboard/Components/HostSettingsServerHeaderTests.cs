using Bunit;

using MudBlazor;

using Paduraksa.Application.Identity;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Config;
using Paduraksa.Dashboard.Pages;
using Paduraksa.Dashboard.SettingsArea;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>The Server header group of the settings fields (PLAN §14 M6): the mode, the inherited words, the custom
/// value only when it applies, and the card summary.</summary>
public sealed class HostSettingsServerHeaderTests
{
    [Fact]
    public async Task TheModeRowNamesWhatItInherits()
    {
        HostSettingsModel model = HostSettingsModel.From(new HostSettings());
        await using DashboardTestContext context = new();

        IRenderedComponent<HostSettingsFields> fields = Render(context, model);

        Assert.Single(fields.FindAll("[data-field=server-header-mode]"));
        Assert.Empty(fields.FindAll("[data-field=server-header-value]"));
        Assert.Contains("Inherit (Paduraksa)", fields.Markup, StringComparison.Ordinal);

        await Set(fields.FindComponents<MudSelect<ServerHeaderMode?>>().Single(s => s.Instance.Label == "Server header"), ServerHeaderMode.Custom);

        Assert.Single(fields.FindAll("[data-field=server-header-value]"));
        Assert.Equal(ServerHeaderMode.Custom, model.ServerMode);
    }

    [Fact]
    public async Task AnInheritedCustomModeNamesItsValue()
    {
        await using DashboardTestContext context = new();

        string markup = Render(
            context,
            HostSettingsModel.From(new HostSettings()),
            inherited: Inherited(ServerHeaderMode.Custom, "edge/1.0")).Markup;

        Assert.Contains("Inherit (custom: edge/1.0)", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALayerValueStaysVisibleWithoutACustomModeSoAnErrorHasAField()
    {
        HostSettingsModel model = new() { ServerValue = "edge/1.0" };
        await using DashboardTestContext context = new();

        IRenderedComponent<HostSettingsFields> fields = Render(context, model, errors: Errors(new ServerHeaderSettings { Mode = ServerHeaderMode.Custom }));

        Assert.Single(fields.FindAll("[data-field=server-header-value]"));
        Assert.Contains("needs a value", Row(fields, "server-header-value"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInvalidValueShowsAtItsField()
    {
        HostSettingsModel model = new() { ServerMode = ServerHeaderMode.Custom, ServerValue = "edge\t1.0" };
        await using DashboardTestContext context = new();

        string row = Row(Render(context, model, errors: Errors(new ServerHeaderSettings { Mode = ServerHeaderMode.Custom, Value = "edge\t1.0" })), "server-header-value");

        Assert.Contains("visible ASCII", row, StringComparison.Ordinal);
        Assert.Contains("mud-input-error", row, StringComparison.Ordinal);
    }

    [Fact]
    public void TheModeAndValueRoundTripAndSummarise()
    {
        HostSettingsModel model = new() { ServerMode = ServerHeaderMode.Custom, ServerValue = "edge/1.0" };
        HostSettings set = model.ApplyTo(new HostSettings());

        Assert.Equal(new ServerHeaderSettings { Mode = ServerHeaderMode.Custom, Value = "edge/1.0" }, set.ServerHeader);
        Assert.Equal(model.ServerMode, HostSettingsModel.From(set).ServerMode);
        Assert.Equal(model.ServerValue, HostSettingsModel.From(set).ServerValue);
        Assert.Null(HostSettingsModel.From(new HostSettings()).ApplyTo(new HostSettings()).ServerHeader);
        Assert.Equal("the layer below's", HostSettingsModel.ServerHeaderText(null, null));

        Assert.Equal(["Server header: Paduraksa"], HostSettingsModel.Summary(new HostSettings { ServerHeader = new ServerHeaderSettings { Mode = ServerHeaderMode.Paduraksa } }));
        Assert.Equal(["Server header: edge/1.0"], HostSettingsModel.Summary(new HostSettings { ServerHeader = new ServerHeaderSettings { Mode = ServerHeaderMode.Custom, Value = "edge/1.0" } }));
        Assert.Equal(["Server header: the upstream's header"], HostSettingsModel.Summary(new HostSettings { ServerHeader = new ServerHeaderSettings { Mode = ServerHeaderMode.Upstream } }));
        Assert.Equal(["Server header: none"], HostSettingsModel.Summary(new HostSettings { ServerHeader = new ServerHeaderSettings { Mode = ServerHeaderMode.Remove } }));
        Assert.Equal(["Server header value: edge/1.0"], HostSettingsModel.Summary(new HostSettings { ServerHeader = new ServerHeaderSettings { Value = "edge/1.0" } }));
    }

    [Fact]
    public async Task AGlobalDefaultsChangeGoesThroughReview()
    {
        await using DashboardTestContext context = new();
        (_, _, FakeDashboardConfig config, _) = IdentityPages.SignIn(context, Role.Operator);
        config.Document = new ConfigDocument(Settings: new GlobalSettings(Version: 4));
        context.Render<MudPopoverProvider>();
        context.Render<Paduraksa.Dashboard.Drawers.DrawerHost>();
        IRenderedComponent<GlobalDefaults> page = context.Render<GlobalDefaults>();
        await page.WaitForStateAsync(() => page.FindAll("[data-form=defaults]").Count == 1);

        await Set(page.FindComponents<MudSelect<ServerHeaderMode?>>().Single(s => s.Instance.Label == "Server header"), ServerHeaderMode.Remove);
        await page.Find("[data-action=review]").ClickAsync(new());

        await page.WaitForStateAsync(() => config.Executed.Count == 1);
        UpdateSettingsCommand command = Assert.IsType<UpdateSettingsCommand>(config.Executed[0]);
        Assert.Equal(4, command.Settings.Version);
        Assert.Equal(new ServerHeaderSettings { Mode = ServerHeaderMode.Remove }, command.Settings.Defaults.ServerHeader);
    }

    private static string Row(IRenderedComponent<HostSettingsFields> fields, string field)
    {
        AngleSharp.Dom.IElement element = fields.Find($"[data-field={field}]");
        while (element.ParentElement is { } parent && !parent.ClassList.Contains("pdk-setting"))
        {
            element = parent;
        }

        return element.OuterHtml;
    }

    private static HostSettings Inherited(ServerHeaderMode mode, string? value) => new()
    {
        ForceHttps = false,
        Tls = new TlsSettings { MinimumVersion = TlsMinimumVersion.Tls12, TcpAlpn = Contracts.Config.TcpAlpn.Http2AndHttp11, Http3 = true },
        Hsts = new HstsSettings { Enabled = false, MaxAge = HstsMaxAgePresets.OneYear, IncludeSubDomains = false, Preload = false },
        ServerHeader = new ServerHeaderSettings { Mode = mode, Value = value },
    };

    private static IReadOnlyDictionary<string, string> Errors(ServerHeaderSettings serverHeader) =>
        InlineValidation.ForHost(
            new ConfigDocument(),
            new HostConfig(HostId.New(), ["h.example.com"], Kind: HostKind.Respond, Respond: new RespondConfig(200), Settings: new HostSettings { ServerHeader = serverHeader }));

    private static IRenderedComponent<HostSettingsFields> Render(
        DashboardTestContext context,
        HostSettingsModel model,
        HostSettings? inherited = null,
        IReadOnlyDictionary<string, string>? errors = null) =>
        context.Render<HostSettingsFields>(parameters => parameters
            .Add(f => f.Model, model)
            .Add(f => f.Inherited, inherited ?? HostSettingsModel.BuiltIn)
            .Add(f => f.InheritWord, "Inherit")
            .Add(f => f.PathPrefix, "/settings")
            .Add(f => f.Errors, errors ?? new Dictionary<string, string>())
            .Add(f => f.ShowAll, true));

    private static Task Set<T>(IRenderedComponent<MudSelect<T>> select, T value) => select.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync(value));
}
