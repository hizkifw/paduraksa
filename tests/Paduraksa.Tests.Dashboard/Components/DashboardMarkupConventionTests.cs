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

    [Fact]
    public void TheScannerSeesTheMarkupItGuards()
    {
        Assert.True(Markup().Count() > 100);
        Assert.True(Markup().Sum(file => NumericField().Count(file.Text)) >= 15);
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

    [GeneratedRegex("""\sStyle="[^"]*"|<[A-Za-z]+[^>]*\sstyle="[^"@]*"[^>]*>""", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex InlineStyle();

    [GeneratedRegex(@"<MudNumericField\b.*?/>", RegexOptions.Singleline, matchTimeoutMilliseconds: 2000)]
    private static partial Regex NumericField();
}
