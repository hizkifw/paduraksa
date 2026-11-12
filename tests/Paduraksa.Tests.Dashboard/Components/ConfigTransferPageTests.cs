using System.Text;

using Bunit;

using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

using MudBlazor;

using Paduraksa.Application.Yaml;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard;
using Paduraksa.Dashboard.Drawers;
using Paduraksa.Dashboard.Layout;
using Paduraksa.Dashboard.Pages;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>The Export and import page: the download, the mode, the pasted or chosen YAML, its errors with lines, and the review sheet.</summary>
public sealed class ConfigTransferPageTests
{
    private static async Task<(IRenderedComponent<ConfigTransfer> Page, IRenderedComponent<DrawerHost> Drawers, FakeDashboardConfig Config)> OpenAsync(DashboardTestContext context, Role role)
    {
        context.Services.AddScoped<DashboardBrowser>();
        var config = ConfigPages.SignIn(context, role);
        config.Revision = new RevisionRef(ConfigRevisionId.New(), 7);
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        var page = context.Render<ConfigTransfer>();
        await page.WaitForStateAsync(() => page.FindAll("[data-card=import]").Count == 1);
        return (page, drawers, config);
    }

    [Fact]
    public async Task ExportDownloadsTheCurrentConfigAsYamlNamedAfterItsRevision()
    {
        await using DashboardTestContext context = new();
        var (page, _, config) = await OpenAsync(context, Role.Viewer);
        config.Export = new ConfigExport("apiVersion: paduraksa.dev/v1\nkind: Config\nhosts: []\n", config.Revision);

        Assert.Contains("Revision #7", page.Find("[data-card=export]").TextContent, StringComparison.Ordinal);
        await page.Find("[data-action=export]").ClickAsync(new());

        var download = Assert.Single(context.JSInterop.Invocations["paduraksa.download"]);
        Assert.Equal("paduraksa-config-r7.yaml", download.Arguments[0]);
        Assert.StartsWith("application/yaml", (string)download.Arguments[1]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReadOnlyRoleSeesTheExportAndNoWayToImport()
    {
        await using DashboardTestContext context = new();
        var (page, _, _) = await OpenAsync(context, Role.Viewer);

        Assert.Single(page.FindAll("[data-action=export]"));
        Assert.Empty(page.FindAll("[data-action=review-import]"));
        Assert.Single(page.FindAll("[data-banner=read-only]"));
    }

    [Fact]
    public async Task APastedYamlIsPreparedInTheChosenModeAndReviewedInTheDrawerAsADryRun()
    {
        await using DashboardTestContext context = new();
        var (page, drawers, config) = await OpenAsync(context, Role.Operator);
        var document = new ConfigDocument(Hosts: [new HostConfig(HostId.New(), ["a.example.com"], Kind: HostKind.Respond, Respond: new RespondConfig(200))]);
        config.Prepared = (_, _) => new ConfigImportPreparation(new ReplaceConfigCommand(document, config.Revision?.Id) { Message = "YAML import (replace)" });
        config.Results = command => command.DryRun
            ? new ConfigCommandResult(ConfigCommandStatus.DryRun, Changes: [new HostChange(ConfigChangeKind.Added, document.Hosts[0].Id, "/hosts/0", [])])
            : new ConfigCommandResult(ConfigCommandStatus.Committed, new RevisionRef(ConfigRevisionId.New(), 8));

        await page.FindAll("[data-field=import-mode] [data-value=replace]")[0].ClickAsync(new());
        await page.Find("textarea").ChangeAsync(new() { Value = "apiVersion: paduraksa.dev/v1\nkind: Config\n" });
        await page.Find("[data-action=review-import]").ClickAsync(new());

        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-review=apply]").Count == 1);
        var imported = Assert.Single(config.Imports);
        Assert.Equal(ConfigImportMode.Replace, imported.Mode);
        Assert.Contains("kind: Config", imported.Yaml, StringComparison.Ordinal);
        Assert.True(Assert.Single(config.Executed).DryRun);
        Assert.Contains("a.example.com", drawers.Markup, StringComparison.Ordinal);

        await drawers.Find("[data-review=apply]").ClickAsync(new());
        await drawers.WaitForStateAsync(() => config.Executed.Count == 2);
        var applied = Assert.IsType<ReplaceConfigCommand>(config.Executed[1]);
        Assert.False(applied.DryRun);
        Assert.Equal("YAML import (replace)", applied.Message);
        // The inputs are cleared once it is in.
        await page.WaitForStateAsync(() => page.Find("textarea").GetAttribute("value") is null or "");
    }

    [Fact]
    public async Task YamlErrorsAreListedWithTheirPathAndLineAndNothingIsReviewed()
    {
        await using DashboardTestContext context = new();
        var (page, drawers, config) = await OpenAsync(context, Role.Operator);
        config.Prepared = (_, _) => new ConfigImportPreparation(
            null,
            [
                new ConfigError("/hosts/0/profile", YamlErrorCodes.ReferenceUnknown, "There is no profile named 'nope' in the YAML. (line 5, column 14)"),
                new ConfigError("/hosts/1", YamlErrorCodes.KeyRequired, "Missing 'names'. (line 9, column 5)"),
            ]);

        await page.Find("textarea").ChangeAsync(new() { Value = "kind: Config" });
        await page.Find("[data-action=review-import]").ClickAsync(new());

        var errors = page.Find("[data-form=errors]");
        Assert.Contains("2 things to fix", errors.TextContent, StringComparison.Ordinal);
        Assert.Contains("/hosts/0/profile", errors.TextContent, StringComparison.Ordinal);
        Assert.Contains("line 5, column 14", errors.TextContent, StringComparison.Ordinal);
        Assert.Empty(drawers.FindAll("[data-review]"));
        Assert.Empty(config.Executed);
    }

    [Fact]
    public async Task ThePasteIsCappedUnderTheCircuitsMessageEvenAtThreeBytesACharacter()
    {
        await using DashboardTestContext context = new();
        var (page, _, _) = await OpenAsync(context, Role.Operator);

        var limit = int.Parse(page.Find("textarea").GetAttribute("maxlength")!, System.Globalization.CultureInfo.InvariantCulture);

        Assert.True(limit * 3 < 32 * 1024, $"{limit} characters of 3 bytes each don't fit a 32 KiB message");
    }

    [Fact]
    public async Task NothingToImportAsksForAFileOrPasteWithoutCallingTheServer()
    {
        await using DashboardTestContext context = new();
        var (page, _, config) = await OpenAsync(context, Role.Operator);

        await page.Find("[data-action=review-import]").ClickAsync(new());

        Assert.Contains("Choose a file or paste the YAML first", page.Find("[data-form=errors]").TextContent, StringComparison.Ordinal);
        Assert.Empty(config.Imports);
    }

    [Fact]
    public async Task AChosenFileIsUsedInPlaceOfThePasteAndCanBeRemoved()
    {
        await using DashboardTestContext context = new();
        var (page, _, config) = await OpenAsync(context, Role.Operator);
        config.Prepared = (_, _) => new ConfigImportPreparation(null, [new ConfigError(string.Empty, YamlErrorCodes.Syntax, "stop here")]);

        page.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("apiVersion: paduraksa.dev/v1\nkind: Config\n", "config.yaml"));

        Assert.Equal("config.yaml", page.Find("[data-field=import-file]").TextContent);
        Assert.Empty(page.FindAll("textarea"));
        await page.Find("[data-action=review-import]").ClickAsync(new());
        Assert.Equal("apiVersion: paduraksa.dev/v1\nkind: Config\n", Assert.Single(config.Imports).Yaml);

        await page.Find("[data-action=clear-file]").ClickAsync(new());
        Assert.Single(page.FindAll("textarea"));
    }

    [Fact]
    public async Task AFileOverTheLimitIsRefusedBeforeItIsRead()
    {
        await using DashboardTestContext context = new();
        var (page, _, _) = await OpenAsync(context, Role.Operator);

        page.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText(new string('x', ConfigImportService.MaxBytes + 1), "huge.yaml"));

        Assert.Contains("larger than", page.Find(".pdk-field-error").TextContent, StringComparison.Ordinal);
        Assert.Empty(page.FindAll("[data-field=import-file]"));
    }

    [Fact]
    public void TheNavigationListsItUnderSettingsForWhoeverMayReadTheConfig()
    {
        var entry = Assert.Single(DashboardNavigation.Entries, static e => e.Href == SettingsRoutes.Transfer);

        Assert.Equal(DashboardNavigation.SettingsGroup, entry.Group);
        Assert.Equal(Permission.ConfigRead, entry.Requires);
        Assert.Equal("Export and import", entry.Title);
    }
}
