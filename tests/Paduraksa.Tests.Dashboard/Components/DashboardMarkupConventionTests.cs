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

    // What is allowed to spell a number or a time itself: machine values and geometry, never something a person reads. Everything a person
    // reads goes through DisplayFormat, so the zone, culture and clock they chose apply everywhere.
    private static readonly (string File, string Why)[] MayFormatItself =
    [
        ("Components/TimeSeriesChart.razor", "SVG coordinates"),
        ("Components/ShareList.razor", "a CSS width"),
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

    [Fact]
    public void TheScannerSeesTheMarkupItGuards()
    {
        Assert.True(Markup().Count() > 100);
        Assert.True(Markup().Sum(file => NumericField().Count(file.Text)) >= 15);
        Assert.True(Sources().Count() > 200);
        Assert.Contains(Sources(), file => DisplayFormatting().IsMatch(file.Text));
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

    [GeneratedRegex(@"<MudNumericField\b.*?/>", RegexOptions.Singleline, matchTimeoutMilliseconds: 2000)]
    private static partial Regex NumericField();
}
