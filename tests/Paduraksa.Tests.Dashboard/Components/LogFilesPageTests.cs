using Bunit;

using Microsoft.Extensions.DependencyInjection;

using MudBlazor;

using Paduraksa.Application.Logging;
using Paduraksa.Contracts.Logging;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Drawers;
using Paduraksa.Dashboard.LogFiles;
using Paduraksa.Dashboard.Pages;

namespace Paduraksa.Tests.Dashboard.Components;

public sealed class LogFilesPageTests
{
    [Fact]
    public async Task ThePageGroupsTheFilesByLogAndOffersEachOneForReadingAndDownload()
    {
        await using DashboardTestContext context = new();
        var store = SignIn(context, Role.Owner);
        store.Files.Add(new LogFileSummary("error.log", "error", 2048, DateTimeOffset.UtcNow.AddSeconds(-30), false));
        store.Files.Add(new LogFileSummary("error-20261109T120000Z.log.gz", "error", 1024, DateTimeOffset.UtcNow.AddDays(-1), true));
        store.Files.Add(new LogFileSummary("access.log", "access", 4096, DateTimeOffset.UtcNow, false));
        var page = context.Render<LogFiles>();

        Assert.Equal(["error.log", "error-20261109T120000Z.log.gz", "access.log"], page.FindAll("[data-file]").Select(row => row.GetAttribute("data-file")));
        Assert.Single(page.FindAll("[data-log=error]"));
        Assert.Single(page.FindAll("[data-log=access]"));
        Assert.Contains("Live: being written now", page.Markup, StringComparison.Ordinal);
        Assert.Contains("Rotated, compressed", page.Markup, StringComparison.Ordinal);
        Assert.Contains("2 KB", page.Markup, StringComparison.Ordinal);
        Assert.Contains("/api/v1/logs/error.log", page.Find("[data-file='error.log'] [data-action=download]").GetAttribute("href"), StringComparison.Ordinal);
        Assert.Equal(3, page.FindAll("[data-action=download]").Count);
    }

    [Fact]
    public async Task AnEmptyDirectorySaysWhereTheLogsWillAppear()
    {
        await using DashboardTestContext context = new();
        SignIn(context, Role.Owner);
        var page = context.Render<LogFiles>();

        Assert.Contains("No log files yet", page.Markup, StringComparison.Ordinal);
        Assert.Contains(FakeLogFiles.LogDirectory, page.Markup, StringComparison.Ordinal);
        Assert.Empty(page.FindAll("[data-file]"));
    }

    [Fact]
    public async Task AFileOpensInADrawerWithItsLastLinesAndItsFilterReachesTheStore()
    {
        await using DashboardTestContext context = new();
        var store = SignIn(context, Role.Owner);
        store.Files.Add(new LogFileSummary("error.log", "error", 128, DateTimeOffset.UtcNow, false));
        store.Lines["error.log"] = ["warn: one", "error: boom", "warn: two"];
        var drawers = context.Render<DrawerHost>();
        var page = context.Render<LogFiles>();

        await page.Find("[data-file='error.log']").ClickAsync(new());
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-section=tail]").Count == 1);
        var drawer = drawers.FindComponent<LogFileDrawer>();

        Assert.Equal("error.log", drawer.Find(".pdk-drawer-title").TextContent.Trim());
        Assert.Contains("warn: one", drawer.Find("[data-section=tail]").TextContent, StringComparison.Ordinal);
        Assert.Contains("error: boom", drawer.Find("[data-section=tail]").TextContent, StringComparison.Ordinal);
        Assert.Contains("/api/v1/logs/error.log", drawer.Find("[data-action=download]").GetAttribute("href"), StringComparison.Ordinal);

        await drawer.Find("input[data-field=filter]").InputAsync(new() { Value = "boom" });
        await drawers.WaitForStateAsync(() => store.Reads.Exists(read => read.Filter == "boom"));
        await drawers.WaitForStateAsync(() => drawer.Find("[data-section=tail]").TextContent.Trim() == "error: boom");
    }

    [Fact]
    public async Task TheFileNameLinkOpensOneDrawerNotTwo()
    {
        await using DashboardTestContext context = new();
        var store = SignIn(context, Role.Owner);
        store.Files.Add(new LogFileSummary("error.log", "error", 64, DateTimeOffset.UtcNow, false));
        store.Lines["error.log"] = ["warn: one"];
        var drawers = context.Render<DrawerHost>();
        var page = context.Render<LogFiles>();

        // The row opens the drawer too; the link must stop its click there instead of opening a second level on top.
        await page.Find("[data-file='error.log'] .pdk-row-link").ClickAsync(new());
        await drawers.WaitForStateAsync(() => drawers.FindAll(".pdk-drawer").Count == 1);

        Assert.Single(drawers.FindAll(".pdk-drawer"));
        Assert.Single(context.Services.GetRequiredService<DrawerStack>().Levels);
    }

    [Fact]
    public async Task AFullTailSaysThatItIsOnlyTheEndAndPointsAtTheDownload()
    {
        await using DashboardTestContext context = new();
        var store = SignIn(context, Role.Owner);
        store.Files.Add(new LogFileSummary("error.log", "error", 10_000_000, DateTimeOffset.UtcNow, false));
        store.Lines["error.log"] = ["warn: the last line"];
        store.Truncated = true;
        var drawers = context.Render<DrawerHost>();
        var page = context.Render<LogFiles>();

        await page.Find("[data-file='error.log']").ClickAsync(new());
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-section=tail-truncated]").Count == 1);

        Assert.Contains("Download the file for all of it", drawers.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARoleWithoutThePermissionIsToldWhyAndNothingIsRead()
    {
        await using DashboardTestContext context = new();
        var store = SignIn(context, Role.Viewer);
        store.Files.Add(new LogFileSummary("error.log", "error", 12, DateTimeOffset.UtcNow, false));
        var page = context.Render<LogFiles>();

        Assert.Contains("Your role can't read the logs", page.Markup, StringComparison.Ordinal);
        Assert.Empty(page.FindAll("[data-file]"));
        Assert.Equal(0, store.Lists);
    }

    [Fact]
    public async Task AnUnreadableDirectoryIsReportedWithAWayToTryAgain()
    {
        await using DashboardTestContext context = new();
        var store = SignIn(context, Role.Owner);
        store.Failing = true;
        var page = context.Render<LogFiles>();

        Assert.Contains("couldn't be read", page.Markup, StringComparison.Ordinal);

        store.Failing = false;
        store.Files.Add(new LogFileSummary("error.log", "error", 12, DateTimeOffset.UtcNow, false));
        await page.FindAll("button").First(button => button.TextContent.Contains("Try again", StringComparison.Ordinal)).ClickAsync(new());

        Assert.Contains("error.log", page.Markup, StringComparison.Ordinal);
    }

    private static FakeLogFiles SignIn(DashboardTestContext context, Role role)
    {
        FakeLogFiles store = new();
        context.Services.AddSingleton<ILogFileStore>(store);
        IdentityPages.SignIn(context, role);
        context.Render<MudPopoverProvider>();
        return store;
    }

    /// <summary>The read side in memory: the files, their lines, and what the page asked for.</summary>
    internal sealed class FakeLogFiles : ILogFileStore
    {
        public const string LogDirectory = "/data/logs";

        public List<LogFileSummary> Files { get; } = [];

        public Dictionary<string, List<string>> Lines { get; } = new(StringComparer.Ordinal);

        public List<(string Name, int MaxLines, string? Filter)> Reads { get; } = [];

        public int Lists { get; private set; }

        public bool Failing { get; set; }

        /// <summary>The file is longer than the lines the fake hands back.</summary>
        public bool Truncated { get; set; }

        public string Directory => LogDirectory;

        public IReadOnlyList<LogFileSummary> List()
        {
            Lists++;
            if (Failing)
            {
                throw new IOException("the directory is unreadable");
            }

            return Files;
        }

        public LogFileTail? ReadTail(string name, int maxLines, string? filter = null)
        {
            Reads.Add((name, maxLines, filter));
            if (!Lines.TryGetValue(name, out var lines))
            {
                return null;
            }

            List<string> matching = [.. lines.Where(line => filter is null || line.Contains(filter, StringComparison.OrdinalIgnoreCase))];
            var file = Files.First(summary => summary.Name == name);
            return new LogFileTail(name, file.Log, [.. matching.TakeLast(maxLines)], Truncated || matching.Count > maxLines, file.Size);
        }

        public Stream? OpenRead(string name, out long length)
        {
            length = 0;
            return null;
        }
    }
}
