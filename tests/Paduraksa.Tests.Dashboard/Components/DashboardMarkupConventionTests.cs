using System.Text.RegularExpressions;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>
/// The conventions of docs/design/dashboard.md that a component can break by writing one line. A page that needs its own look for one
/// of these is a sign the shared piece is missing something, so the fix is in the shared piece (a class in the stylesheets, a component), never in
/// the call site.
/// </summary>
public sealed partial class DashboardMarkupConventionTests
{
    [Fact]
    public void NoComponentStylesItselfInline()
    {
        var inline = Markup().SelectMany(file => InlineStyle().Matches(file.Text).Select(m => $"{file.Path}: {m.Value}")).ToList();

        Assert.True(inline.Count == 0, "These set a size or colour on the call site; use a class from the stylesheets:\n" + string.Join('\n', inline));
    }

    [Fact]
    public void EveryNumberFieldHidesItsSpinButtons()
    {
        var shown = Markup().SelectMany(file => NumericField().Matches(file.Text)
                .Where(m => !m.Value.Contains("HideSpinButtons", StringComparison.Ordinal))
                .Select(m => $"{file.Path}: {m.Value.Split('\n')[0].Trim()}"))
            .ToList();

        Assert.True(shown.Count == 0, "Number fields show no up and down arrows (a field beside text fields would be the only one with them):\n" + string.Join('\n', shown));
    }

    // The statistics surfaces: each draws its loading state from the shared frame (a tile with Pending, a StatsCard, a chart with Pending), never
    // from a free-standing box whose height is a number in the call site, which is what made the page jump when the figures arrived.
    private static readonly string[] StatisticsSurfaces =
    [
        "Components/TrafficStrip.razor",
        "Components/TrafficStatistics.razor",
        "Components/VisitorsStatistics.razor",
        "Components/SecurityStatistics.razor",
        "Components/StatCard.razor",
        "Pages/SecurityPage.razor",
    ];

    [Fact]
    public void AStatisticsSurfaceHasNoSkeletonBoxOfItsOwn()
    {
        var boxes = Markup()
            .Where(file => StatisticsSurfaces.Any(surface => file.Path.EndsWith(surface, StringComparison.Ordinal)))
            .Where(file => file.Text.Contains("<MudSkeleton", StringComparison.Ordinal))
            .Select(file => file.Path)
            .ToList();

        Assert.True(boxes.Count == 0, "These draw a skeleton box of their own; render the loaded markup with Pending (SecurityTile, StatsCard, TimeSeriesChart) so both share one size:\n" + string.Join('\n', boxes));
    }

    [Fact]
    public void EveryStatisticsComponentLoadsThroughTheSharedFrame()
    {
        var files = Markup().ToDictionary(file => file.Path, file => file.Text);
        var missing = new[]
            {
                ("Components/TrafficStrip.razor", "<SecurityTile"),
                ("Components/TrafficStatistics.razor", "<StatsCard"),
                ("Components/VisitorsStatistics.razor", "<StatsCard"),
                ("Components/SecurityStatistics.razor", "<StatsCard"),
            }
            .Where(use => !files.Single(file => file.Key.EndsWith(use.Item1, StringComparison.Ordinal)).Value.Contains(use.Item2 + " ", StringComparison.Ordinal))
            .Select(use => $"{use.Item1} should use {use.Item2}")
            .ToList();

        Assert.True(missing.Count == 0, string.Join('\n', missing));
    }

    // What is allowed to spell a number or a time itself: machine values and geometry, never something a person reads. Everything a person
    // reads goes through DisplayFormat, so the zone, culture and clock they chose apply everywhere.
    private static readonly (string File, string Why)[] MayFormatItself =
    [
        ("Components/TimeSeriesChart.razor", "SVG coordinates"),
        ("Components/ShareList.razor", "a CSS width"),
        ("LogFiles/RequestWaterfall.cs", "a CSS offset and width"),
        ("Components/QrCode.cs", "SVG path data"),
        ("Pages/Audit.razor", "the export's file name"),
        ("Audit/AuditText.cs", "the export's lines"),
        ("DisplayFormat.cs", "the formatter"),
    ];

    [Fact]
    public void NothingSpellsOutATimeOrANumberForAPersonOutsideDisplayFormat()
    {
        var spelled = Sources()
            .Where(file => !MayFormatItself.Any(allowed => file.Path.EndsWith(allowed.File, StringComparison.Ordinal)))
            .SelectMany(file => DisplayFormatting().Matches(file.Text).Select(m => $"{file.Path}: {m.Value}"))
            .ToList();

        Assert.True(spelled.Count == 0, "These format a time or a number themselves; call DisplayFormat (inject it as Fmt) so the person's zone and culture apply:\n" + string.Join('\n', spelled));
    }

    // Pages whose prerender may load: a redirect, and one that only has links (which work without a circuit).
    private static readonly (string File, string Why)[] MayLoadWhilePrerendering =
    [
        ("Pages/Settings.razor", "it only redirects"),
        ("Pages/Home.razor", "the overview has links, no handler to lose, and its numbers are better prerendered"),
    ];

    [Fact]
    public void NoPageLoadsItsDataWhilePrerendering()
    {
        var loading = Markup()
            .Where(file => file.Path.Contains("/Pages/", StringComparison.Ordinal) && !file.Path.Contains("/Pages/Account/", StringComparison.Ordinal))
            .Where(file => LifecycleLoad().Matches(file.Text).Any(method => !method.Value.Contains("!RendererInfo.IsInteractive", StringComparison.Ordinal)))
            .Where(file => !MayLoadWhilePrerendering.Any(allowed => file.Path.EndsWith(allowed.File, StringComparison.Ordinal)))
            .Select(file => file.Path)
            .ToList();

        Assert.True(
            loading.Count == 0,
            "These load data before the circuit is live, so they show buttons that do nothing until it is (a click made then is lost). "
            + "Return from the lifecycle method while !RendererInfo.IsInteractive, as Notifications does:\n" + string.Join('\n', loading));
    }

    [Fact]
    public void TheScannerSeesTheMarkupItGuards()
    {
        Assert.True(Markup().Count() > 100);
        Assert.True(Markup().Sum(file => NumericField().Count(file.Text)) >= 15);
        Assert.True(Sources().Count() > 200);
        Assert.Contains(Sources(), file => DisplayFormatting().IsMatch(file.Text));
        Assert.True(Markup().Count(file => LifecycleLoad().IsMatch(file.Text)) >= 20);
    }

    private static IEnumerable<(string Path, string Text)> Markup()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Paduraksa.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        return Directory.EnumerateFiles(Path.Combine(root.FullName, "src", "Paduraksa.Dashboard"), "*.razor", SearchOption.AllDirectories)
            .Select(path => (Path.GetRelativePath(root.FullName, path).Replace('\\', '/'), File.ReadAllText(path)));
    }

    private static IEnumerable<(string Path, string Text)> Sources()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Paduraksa.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var dashboard = Path.Combine(root.FullName, "src", "Paduraksa.Dashboard");
        return Directory.EnumerateFiles(dashboard, "*.*", SearchOption.AllDirectories)
            .Where(path => (path.EndsWith(".razor", StringComparison.Ordinal) || path.EndsWith(".cs", StringComparison.Ordinal))
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(path => (Path.GetRelativePath(dashboard, path).Replace('\\', '/'), File.ReadAllText(path)));
    }

    // A format string given to ToString ("N0", "yyyy-MM-dd") or to an interpolation hole ({value:0.#}), and UtcDateTime read for display.
    [GeneratedRegex("""\.ToString\(\s*"[^"]*"|\{[^{}:"]+:(?:N\d|[0#][.,#0]*|yyyy|d MMM|HH)[^}]*\}|\.UtcDateTime\b""", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex DisplayFormatting();

    [GeneratedRegex("""\sStyle="[^"]*"|<[A-Za-z]+[^>]*\sstyle="[^"@]*"[^>]*>""", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex InlineStyle();

    [GeneratedRegex(@"override\s+(?:async\s+)?Task\s+(?:OnInitializedAsync|OnParametersSetAsync)\s*\(.*?\n    \}", RegexOptions.Singleline, matchTimeoutMilliseconds: 2000)]
    private static partial Regex LifecycleLoad();

    [GeneratedRegex(@"<MudNumericField\b.*?/>", RegexOptions.Singleline, matchTimeoutMilliseconds: 2000)]
    private static partial Regex NumericField();
}
