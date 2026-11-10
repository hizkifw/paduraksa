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
    public async Task TheToolbarHoldsTheFilterTheCountAndTheWrapToggle()
    {
        await using DashboardTestContext context = new();
        var store = SignIn(context, Role.Owner);
        var drawer = await Open(context, store, "error.log", ["warn: one"]);

        Assert.Single(drawer.FindAll("input[data-field=filter]"));
        Assert.Single(drawer.FindAll("[data-field=count]"));
        var wrap = drawer.Find("button[data-action=wrap]");
        Assert.Equal("false", wrap.GetAttribute("aria-pressed"));
        Assert.Equal("Wrap lines", wrap.GetAttribute("aria-label"));

        await wrap.ClickAsync(new());
        Assert.Equal("true", drawer.Find("button[data-action=wrap]").GetAttribute("aria-pressed"));
        Assert.Contains("pdk-log-wrapped", drawer.Find("[data-section=tail]").ClassList);

        var select = drawer.FindComponent<MudSelect<int>>();
        await select.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync(500));
        await drawer.WaitForStateAsync(() => store.Reads.Exists(read => read.MaxLines == 500));
    }

    [Fact]
    public async Task EachLineIsItsOwnElementAndIsEncodedAsText()
    {
        await using DashboardTestContext context = new();
        var store = SignIn(context, Role.Owner);
        const string Access = "2026-01-31T12:34:56.789Z 198.51.100.7 \"app.example\" \"GET /a HTTP/2\" 503 512 1.234 0.987";
        const string Hostile = "<img src=x onerror=alert(1)>";
        var drawer = await Open(context, store, "access.log", [Access, Hostile]);

        var lines = drawer.FindAll(".pdk-log-line");
        Assert.Equal(2, lines.Count);
        Assert.Equal(Access, lines[0].TextContent);
        Assert.Equal("2026-01-31T12:34:56.789Z", lines[0].QuerySelector(".pdk-log-time")!.TextContent);
        Assert.Equal("503", lines[0].QuerySelector(".pdk-log-status-error")!.TextContent);
        Assert.Equal(Hostile, lines[1].TextContent);
        Assert.Empty(drawer.FindAll("img"));
        Assert.Contains("&lt;img", drawer.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A4xxStatusIsMarkedWarningAndA5xxError()
    {
        await using DashboardTestContext context = new();
        var store = SignIn(context, Role.Owner);
        var drawer = await Open(context, store, "access.log",
            [
                "2026-01-31T12:34:56.789Z 198.51.100.7 \"app.example\" \"GET /a HTTP/2\" 404 512 1.234 0.987",
                "2026-01-31T12:34:57.789Z 198.51.100.7 \"app.example\" \"GET /b HTTP/2\" 500 512 1.234 0.987",
                "2026-01-31T12:34:58.789Z 198.51.100.7 \"app.example\" \"GET 500 HTTP/2\" 200 512 1.234 0.987",
                "2026-01-31T12:34:59.789Z info: the upstream answered 500",
            ]);

        var statuses = drawer.FindAll(".pdk-log-status-warning, .pdk-log-status-error");
        Assert.Equal(2, statuses.Count);
        Assert.Contains(statuses, status => status.ClassList.Contains("pdk-log-status-warning") && status.TextContent == "404");
        Assert.Contains(statuses, status => status.ClassList.Contains("pdk-log-status-error") && status.TextContent == "500");
    }

    [Fact]
    public async Task AnEmptyFileSaysSoInsteadOfShowingAnEmptyPane()
    {
        await using DashboardTestContext context = new();
        var store = SignIn(context, Role.Owner);
        var drawer = await Open(context, store, "error.log", []);

        Assert.Contains("The file is empty.", drawer.Markup, StringComparison.Ordinal);
        Assert.Empty(drawer.FindAll("[data-section=tail]"));
    }

    [Fact]
    public async Task AnUnreadableFileIsReportedAndNoTailIsShown()
    {
        await using DashboardTestContext context = new();
        var store = SignIn(context, Role.Owner);
        var file = new LogFileSummary("rotated-away.log", "error", 128, DateTimeOffset.UtcNow, false);
        store.Files.Add(file);

        var drawer = context.Render<LogFileDrawer>(parameters => parameters.Add(drawer => drawer.File, file));
        await drawer.WaitForStateAsync(() => drawer.FindAll("[data-section=tail-error]").Count == 1);

        Assert.Contains("couldn't be read", drawer.Find("[data-section=tail-error]").TextContent, StringComparison.Ordinal);
        Assert.Empty(drawer.FindAll("[data-section=tail]"));
    }

    [Fact]
    public async Task FollowReReadsTheTailWithTheFilterAndCountUntilItIsTurnedOff()
    {
        await using DashboardTestContext context = new();
        var store = SignIn(context, Role.Owner);
        var drawer = await Open(context, store, "error.log", ["warn: one", "error: boom"], TimeSpan.FromMilliseconds(25));

        var follow = drawer.Find("button[data-action=follow]");
        Assert.Equal("false", follow.GetAttribute("aria-pressed"));
        Assert.Empty(drawer.FindAll(".pdk-log-live"));

        await drawer.Find("input[data-field=filter]").InputAsync(new() { Value = "boom" });
        await drawer.WaitForStateAsync(() => store.Reads.Exists(read => read.Filter == "boom"));
        var select = drawer.FindComponent<MudSelect<int>>();
        await select.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync(500));
        await drawer.WaitForStateAsync(() => store.Reads.Exists(read => read.MaxLines == 500 && read.Filter == "boom"));

        await follow.ClickAsync(new());
        Assert.Equal("true", drawer.Find("button[data-action=follow]").GetAttribute("aria-pressed"));
        Assert.Single(drawer.FindAll(".pdk-log-live"));
        drawer.WaitForAssertion(() => context.JSInterop.VerifyInvoke("paduraksa.logPane.watch"));
        drawer.WaitForAssertion(() => context.JSInterop.VerifyInvoke("paduraksa.logPane.toEnd"));

        var afterOn = store.Reads.Count;
        await drawer.WaitForStateAsync(() => store.Reads.Count > afterOn);
        Assert.Equal(500, store.Reads[^1].MaxLines);
        Assert.Equal("boom", store.Reads[^1].Filter);

        await drawer.Find("button[data-action=follow]").ClickAsync(new());
        Assert.Equal("false", drawer.Find("button[data-action=follow]").GetAttribute("aria-pressed"));
        Assert.Empty(drawer.FindAll(".pdk-log-live"));

        await Task.Delay(150, Xunit.TestContext.Current.CancellationToken);
        var settled = store.Reads.Count;
        await Task.Delay(150, Xunit.TestContext.Current.CancellationToken);
        Assert.Equal(settled, store.Reads.Count);
    }

    [Fact]
    public async Task FollowKeepsThePaneAtTheEndWhenThePersonWasAlreadyThere()
    {
        await using DashboardTestContext context = new();
        var store = SignIn(context, Role.Owner);
        context.JSInterop.Setup<bool>("paduraksa.logPane.atEnd", _ => true).SetResult(true);
        var drawer = await Open(context, store, "error.log", ["warn: one"], TimeSpan.FromMilliseconds(25));

        await drawer.Find("button[data-action=follow]").ClickAsync(new());
        drawer.WaitForAssertion(() => context.JSInterop.VerifyInvoke("paduraksa.logPane.toEnd"));
        var afterOn = context.JSInterop.Invocations["paduraksa.logPane.toEnd"].Count;
        store.Lines["error.log"].Add("warn: two");

        await drawer.WaitForStateAsync(() => context.JSInterop.Invocations["paduraksa.logPane.toEnd"].Count > afterOn);
        Assert.Equal(2, drawer.FindAll(".pdk-log-line").Count);
    }

    [Fact]
    public async Task ScrollingUpWhileFollowingShowsTheWayBackToTheLatestLines()
    {
        await using DashboardTestContext context = new();
        var store = SignIn(context, Role.Owner);
        var drawer = await Open(context, store, "error.log", ["warn: one"], TimeSpan.FromMilliseconds(25));

        await drawer.Find("button[data-action=follow]").ClickAsync(new());
        await drawer.InvokeAsync(() => drawer.Instance.LogScrolledAsync(false));
        await drawer.WaitForStateAsync(() => drawer.FindAll("[data-action=jump-latest]").Count == 1);
        Assert.Contains("Jump to latest", drawer.Find("[data-action=jump-latest]").TextContent, StringComparison.Ordinal);

        store.Lines["error.log"].Add("warn: two");
        await drawer.WaitForStateAsync(() => store.Reads.Count >= 3);
        await drawer.WaitForStateAsync(() => drawer.FindAll("[data-action=jump-latest]").Count == 1
            && drawer.Find("[data-action=jump-latest]").TextContent.Contains("New lines below", StringComparison.Ordinal));

        await drawer.InvokeAsync(() => drawer.Instance.LogScrolledAsync(true));
        await drawer.WaitForStateAsync(() => drawer.FindAll("[data-action=jump-latest]").Count == 0);
    }

    [Fact]
    public async Task FollowUpdatesTheLastWrittenTimeFromTheFreshSummary()
    {
        await using DashboardTestContext context = new();
        var store = SignIn(context, Role.Owner);
        var file = new LogFileSummary("error.log", "error", 128, DateTimeOffset.UtcNow.AddHours(-2), false);
        store.Files.Add(file);
        store.Lines["error.log"] = ["warn: one"];
        var drawer = context.Render<LogFileDrawer>(parameters => parameters
            .Add(drawer => drawer.File, file)
            .Add(drawer => drawer.FollowInterval, TimeSpan.FromMilliseconds(25)));
        await drawer.WaitForStateAsync(() => drawer.FindAll("[data-section=tail]").Count == 1);
        Assert.Contains("2 h ago", drawer.Find(".pdk-drawer-subtitle").TextContent, StringComparison.Ordinal);

        await drawer.Find("button[data-action=follow]").ClickAsync(new());
        store.Files[0] = file with { LastModified = DateTimeOffset.UtcNow };

        await drawer.WaitForStateAsync(() => drawer.Find(".pdk-drawer-subtitle").TextContent.Contains("just now", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailedReadStopsFollowing()
    {
        await using DashboardTestContext context = new();
        var store = SignIn(context, Role.Owner);
        var drawer = await Open(context, store, "error.log", ["warn: one"], TimeSpan.FromMilliseconds(25));

        await drawer.Find("button[data-action=follow]").ClickAsync(new());
        var reads = store.Reads.Count;
        await drawer.WaitForStateAsync(() => store.Reads.Count > reads);
        store.ReadFailing = true;

        await drawer.WaitForStateAsync(() => drawer.FindAll("[data-section=tail-error]").Count == 1);
        Assert.Equal("false", drawer.Find("button[data-action=follow]").GetAttribute("aria-pressed"));
        Assert.Empty(drawer.FindAll(".pdk-log-live"));
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

    private static async Task<IRenderedComponent<LogFileDrawer>> Open(
        DashboardTestContext context, FakeLogFiles store, string name, string[] lines, TimeSpan? followInterval = null)
    {
        var file = new LogFileSummary(name, name.StartsWith("access", StringComparison.Ordinal) ? "access" : "error", 128, DateTimeOffset.UtcNow, false);
        store.Files.Add(file);
        store.Lines[name] = [.. lines];
        var drawer = context.Render<LogFileDrawer>(parameters =>
        {
            parameters.Add(drawer => drawer.File, file);
            if (followInterval is { } interval)
            {
                parameters.Add(drawer => drawer.FollowInterval, interval);
            }
        });
        await drawer.WaitForStateAsync(() => drawer.FindAll("[data-section=tail], .pdk-empty").Count > 0);
        return drawer;
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

        /// <summary>The next tail reads fail, as a file rotated away between polls does.</summary>
        public bool ReadFailing { get; set; }

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
            if (ReadFailing)
            {
                throw new IOException("the file was rotated away");
            }

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
