using System.Text.Json;
using System.Text.RegularExpressions;

using Paduraksa.Admin.OpenApi;

namespace Paduraksa.Tests.Unit.Admin;

/// <summary>
/// The descriptions of the API reference are read by people in Scalar, not by contributors in an IDE: the XML comments they come from keep their
/// <c>cref</c>s and their line breaks, and the shipped document says the plain thing.
/// </summary>
public sealed partial class OpenApiDescriptionTextTests
{
    [Theory]
    [InlineData("While bool? MaintenanceSettings.Enabled, every request", "While `enabled`, every request")]
    [InlineData("It needs bool? HstsSettings.IncludeSubDomains and a int? HstsSettings.MaxAge of a year", "It needs `includeSubDomains` and a `maxAge` of a year")]
    [InlineData("Its long GlobalSettings.Version is the version edited", "Its `version` is the version edited")]
    [InlineData("With RateLimitKey.Header: the request header", "With `header`: the request header")]
    [InlineData("Default: LoadBalancingPolicy.PowerOfTwoChoices.", "Default: `powerOfTwoChoices`.")]
    [InlineData("IReadOnlyList&lt;ConfigChange&gt; ConfigCommandResult.Changes holds what", "`changes` holds what")]
    [InlineData("TimeSpan ActiveHealthConfig.DefaultInterval (canonical)", "`ActiveHealthConfig.DefaultInterval` (canonical)")]
    [InlineData("ResolvedForwardAuth ForwardAuthPresets.Resolve(ForwardAuthConfig config) decides", "`ForwardAuthPresets.Resolve` decides")]
    public void ACrefIsWrittenAsTheMembersNameInTheApi(string rendered, string expected) =>
        Assert.Equal(expected, DescriptionText.Tidy(rendered));

    [Theory]
    [InlineData("A name such as e.g. Foo.Bar or Paduraksa. Only text.")]
    [InlineData("Already code: `ServerSettings.SetsNothing` stays.")]
    [InlineData("Sentence ending in HostConfig. Then HostConfig.Nothing.")]
    public void TextThatIsNotACrefIsLeftAlone(string text) =>
        Assert.Equal(text, DescriptionText.Tidy(text));

    [Fact]
    public void SourceLineBreaksAndIndentationBecomeSpacesAndBlankLinesStayParagraphs()
    {
        Assert.Equal("one two three", DescriptionText.Tidy("one\n            two \r\n  three"));
        Assert.Equal("first line\n\nsecond", DescriptionText.Tidy("first\n      line\n   \n\n      second"));
        Assert.Null(DescriptionText.Tidy(null));
    }

    [Fact]
    public void XmlEntitiesAndInlineHtmlBecomeText()
    {
        Assert.Equal("a **new** revision & the *inherit* rule <draft>", DescriptionText.Tidy("a <b>new</b> revision &amp; the <i>inherit</i> rule &lt;draft&gt;"));
    }

    [Fact]
    public void TidyingTwiceChangesNothing()
    {
        const string Rendered = "While bool? MaintenanceSettings.Enabled,\n            every request &amp; the <b>rest</b>\n\n   second";
        var once = DescriptionText.Tidy(Rendered);
        Assert.Equal(once, DescriptionText.Tidy(once));
    }

    [Fact]
    public void NoDescriptionInTheShippedDocumentKeepsItsSourceFormatting()
    {
        List<string> problems = [];
        foreach (var (path, text) in Descriptions(JsonDocument.Parse(AdminOpenApi.ShippedDocument()!).RootElement, "$"))
        {
            // What the XML doc rendering leaves: symbol prefixes, type paths, a member written with its type, entities, markup, source indentation.
            foreach (var (name, pattern) in Checks)
            {
                if (pattern.IsMatch(text))
                {
                    problems.Add($"{path} ({name}): {text[..Math.Min(text.Length, 100)].ReplaceLineEndings("\\n")}");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems.Take(20)));
    }

    private static readonly (string Name, Regex Pattern)[] Checks =
    [
        ("symbol prefix", SymbolPrefix()),
        ("type path", TypePath()),
        ("member with its type", MemberWithType()),
        ("newline and indentation", NewlineIndentation()),
        ("trailing space before newline", SpaceBeforeNewline()),
        ("doubled space", DoubledSpace()),
        ("entity", Entity()),
        ("markup", Markup()),
    ];

    private static IEnumerable<(string Path, string Text)> Descriptions(JsonElement element, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name is "description" or "summary" && property.Value.ValueKind == JsonValueKind.String)
                    {
                        yield return ($"{path}.{property.Name}", property.Value.GetString()!);
                    }
                    else
                    {
                        foreach (var inner in Descriptions(property.Value, $"{path}.{property.Name}"))
                        {
                            yield return inner;
                        }
                    }
                }

                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var inner in Descriptions(item, $"{path}[{index++}]"))
                    {
                        yield return inner;
                    }
                }

                break;
            default:
                break;
        }
    }

    [GeneratedRegex(@"(?<![\w`])[TPMFENR]:[A-Z]\w")]
    private static partial Regex SymbolPrefix();

    [GeneratedRegex(@"\bSystem\.[A-Za-z]|\bPaduraksa\.[A-Z]")]
    private static partial Regex TypePath();

    [GeneratedRegex(@"\b(?:bool|byte|short|int|long|double|decimal|string|object|TimeSpan|[A-Z]\w*)\??(?:&lt;|<)?[\w, ]*(?:&gt;|>)?\??\s+(?<![`\w])[A-Z]\w+\.[A-Z]\w+\b(?!\.)")]
    private static partial Regex MemberWithType();

    [GeneratedRegex(@"\n[ \t]")]
    private static partial Regex NewlineIndentation();

    [GeneratedRegex(@"[ \t]\n")]
    private static partial Regex SpaceBeforeNewline();

    [GeneratedRegex(@"[^\n ] {2,}\S")]
    private static partial Regex DoubledSpace();

    [GeneratedRegex(@"&(?:lt|gt|amp|quot|apos);")]
    private static partial Regex Entity();

    [GeneratedRegex(@"</?[a-z][a-z0-9]*[ />]")]
    private static partial Regex Markup();
}
