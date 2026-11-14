using Bunit;

using Microsoft.Extensions.DependencyInjection;

using MudBlazor;

using Paduraksa.Application.Settings;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Pages;
using Paduraksa.Dashboard.SettingsArea;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>Settings, Server: one card per group, a change through the review sheet, the environment's values read-only, and who may edit.</summary>
public sealed class ServerSettingsPageTests
{
    private static async Task<IRenderedComponent<ServerSettingsPage>> RenderAsync(DashboardTestContext context)
    {
        context.Render<MudPopoverProvider>();
        context.Render<Paduraksa.Dashboard.Drawers.DrawerHost>();
        var page = context.Render<ServerSettingsPage>();
        await page.WaitForStateAsync(() => page.FindAll("[data-form=server-settings]").Count == 1);
        return page;
    }

    [Fact]
    public async Task ARetentionIsChangedThroughTheReviewSheetWithTheRestOfTheSettingsKept()
    {
        await using DashboardTestContext context = new();
        var (_, _, config, _) = IdentityPages.SignIn(context, Role.Admin);
        config.Document = new ConfigDocument(Settings: new GlobalSettings(Version: 7, UnknownHost: UnknownHostBehaviour.Close));
        var page = await RenderAsync(context);

        Assert.Single(page.FindAll("[data-card=retention]"));
        var field = page.FindComponents<DurationField>().Single(f => f.Instance.Field == "server-metricsRetention");
        Assert.Equal("Default (90 days)", field.Instance.Placeholder);
        await page.InvokeAsync(() => field.Instance.ValueChanged.InvokeAsync(TimeSpan.FromDays(30)));
        await page.Find("[data-action=review]").ClickAsync(new());

        await page.WaitForStateAsync(() => config.Executed.Count == 1);
        var command = Assert.IsType<UpdateSettingsCommand>(config.Executed[0]);
        Assert.True(command.DryRun);
        Assert.Equal((7L, UnknownHostBehaviour.Close), (command.Settings.Version, command.Settings.UnknownHost));
        Assert.Equal(new ServerSettings { MetricsRetention = TimeSpan.FromDays(30) }, command.Settings.Server);
    }

    [Fact]
    public async Task AValueOutsideItsBoundsIsShownAtItsFieldAndNotReviewed()
    {
        await using DashboardTestContext context = new();
        var (_, _, config, _) = IdentityPages.SignIn(context, Role.Owner);
        config.Document = new ConfigDocument(Settings: new GlobalSettings(Version: 1));
        var page = await RenderAsync(context);

        var field = page.FindComponents<DurationField>().Single(f => f.Instance.Field == "server-metricsRetention");
        await page.InvokeAsync(() => field.Instance.ValueChanged.InvokeAsync(TimeSpan.FromMinutes(10)));
        await page.Find("[data-action=review]").ClickAsync(new());

        Assert.Contains("from 1 hour to 5 years", field.Instance.Error, StringComparison.Ordinal);
        Assert.Empty(config.Executed);
    }

    [Fact]
    public async Task ASettingTheEnvironmentSetsIsReadOnlyAndSaysWhere()
    {
        await using DashboardTestContext context = new();
        context.Services.AddSingleton(new ServerSettingsState(new Dictionary<string, string> { ["metricsRetention"] = "7.00:00:00" }));
        var (_, _, config, _) = IdentityPages.SignIn(context, Role.Owner);
        config.Document = new ConfigDocument(Settings: new GlobalSettings(Version: 1));
        var page = await RenderAsync(context);

        var row = page.Find("[data-setting=metricsRetention]");
        Assert.Contains("Set by the server's environment", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("Paduraksa__Metrics__Retention", row.TextContent, StringComparison.Ordinal);
        var field = page.FindComponents<DurationField>().Single(f => f.Instance.Field == "server-metricsRetention");
        Assert.True(field.Instance.ReadOnly);
        Assert.Equal(TimeSpan.FromDays(7), field.Instance.Value);
        Assert.False(page.FindComponents<DurationField>().Single(f => f.Instance.Field == "server-analyticsRetention").Instance.ReadOnly);
    }

    [Theory]
    [InlineData(Role.Operator)]
    [InlineData(Role.Viewer)]
    public async Task OnlyTheOwnerAndTheAdminMayEdit(Role role)
    {
        await using DashboardTestContext context = new();
        var (_, _, config, _) = IdentityPages.SignIn(context, role);
        config.Document = new ConfigDocument(Settings: new GlobalSettings(Version: 1));
        var page = await RenderAsync(context);

        Assert.Empty(page.FindAll("[data-action=review]"));
        Assert.Single(page.FindAll("[data-banner=read-only]"));
        Assert.All(page.FindComponents<DurationField>(), f => Assert.True(f.Instance.ReadOnly));
    }
}
