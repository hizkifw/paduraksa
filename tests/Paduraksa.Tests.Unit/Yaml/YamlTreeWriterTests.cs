using System.Text;

using CsCheck;

using Paduraksa.Application.Yaml;
using Paduraksa.Tests.Shared;

using Xunit;

namespace Paduraksa.Tests.Unit.Yaml;

public sealed class YamlTreeWriterTests
{
    // The characters that trip YAML writers: indicators, quotes, whitespace in every place, line ends, controls, the Unicode line separators and ordinary text (no lone surrogate: UTF-8 can't hold one).
    private const string Alphabet = "ab0.-_ :#'\"[]{},&*!|>%@`\\?\n\n\t\r\u0001\u0085\u2028\u00e9\u65e5";

    private static YamlNode Read(string yaml)
    {
        Assert.True(YamlTreeReader.TryRead(Encoding.UTF8.GetBytes(yaml), YamlLimits.Default, out var root, out var problem), problem.Located + "\n" + yaml);
        return root!;
    }

    private static YamlMapping Mapping(params (string Key, YamlNode Value)[] entries)
    {
        YamlMapping mapping = new(0, 0);
        foreach (var (key, value) in entries)
        {
            mapping.Entries.Add(new YamlEntry(key, 0, 0, value));
        }

        return mapping;
    }

    private static YamlScalar Text(string text) => new(text, plain: false, 0, 0);

    [Fact]
    public void AnyTextReadsBackAsTheSameTextWhateverItLooksLike()
    {
        Property.StringOf(Alphabet, 0, 12).Check(
            3_000,
            text =>
            {
                var yaml = YamlTreeWriter.Write(Mapping(("value", Text(text)), ("list", new YamlSequence(0, 0) { Items = { Text(text), Text("tail") } })));
                var read = Assert.IsType<YamlMapping>(Read(yaml));

                var value = Assert.IsType<YamlScalar>(read.Find("value")!.Value);
                Assert.Equal(text, value.Text);
                Assert.False(YamlScalars.IsNull(value) || YamlScalars.TryBool(value, out _) || YamlScalars.TryInteger(value, out _) || YamlScalars.TryNumber(value, out _), "text read back as another type");
                Assert.Equal(text, Assert.IsType<YamlScalar>(Assert.IsType<YamlSequence>(read.Find("list")!.Value).Items[0]).Text);
            });
    }

    [Fact]
    public void AnAstralCharacterSurvives()
    {
        var yaml = YamlTreeWriter.Write(Mapping(("v", Text("smile \ud83d\ude00 and \ud83d\ude00"))));

        Assert.Equal("smile \ud83d\ude00 and \ud83d\ude00", Assert.IsType<YamlScalar>(Assert.IsType<YamlMapping>(Read(yaml)).Entries[0].Value).Text);
    }

    [Fact]
    public void AnyKeyReadsBackAsTheSameKey()
    {
        Property.StringOf(Alphabet, 1, 10).Check(
            2_000,
            key =>
            {
                var yaml = YamlTreeWriter.Write(Mapping((key, Text("v"))));

                Assert.Equal(key, Assert.IsType<YamlMapping>(Read(yaml)).Entries[0].Key);
            });
    }

    [Theory]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("~")]
    [InlineData("123")]
    [InlineData("1.5")]
    [InlineData("1e3")]
    [InlineData("0x1F")]
    [InlineData("00:00:15")]
    [InlineData("NO")]
    [InlineData("no")]
    [InlineData("off")]
    [InlineData("Y")]
    [InlineData("")]
    [InlineData("<<")]
    public void TextThatLooksLikeAnotherTypeIsQuotedSoEveryReaderTakesItAsText(string text)
    {
        var yaml = YamlTreeWriter.Write(Mapping(("v", Text(text))));

        Assert.True(yaml.Contains('\'') || yaml.Contains('"'), yaml);
        Assert.False(Assert.IsType<YamlScalar>(Assert.IsType<YamlMapping>(Read(yaml)).Entries[0].Value).Plain);
    }

    [Fact]
    public void NumbersAndBooleansStayPlainAndText_WhichIsntOneOfThem_IsNotQuoted()
    {
        var yaml = YamlTreeWriter.Write(Mapping(
            ("enabled", new YamlScalar("true", plain: true, 0, 0)),
            ("count", new YamlScalar("42", plain: true, 0, 0)),
            ("host", Text("app.example.com")),
            ("ip", Text("10.0.0.1")),
            ("names", new YamlSequence(0, 0) { Items = { Text("a.example.com"), Text("b.example.com") } })));

        Assert.Equal("enabled: true\ncount: 42\nhost: app.example.com\nip: 10.0.0.1\nnames: [a.example.com, b.example.com]\n", yaml);
    }

    [Fact]
    public void ALongLineIsNotWrappedAndAMultiLineTextIsALiteralBlock()
    {
        var long_ = string.Join(' ', Enumerable.Repeat("word", 80));
        var yaml = YamlTreeWriter.Write(Mapping(("long", Text(long_)), ("page", Text("<p>one</p>\n<p>two</p>"))));

        Assert.Contains(long_, yaml, StringComparison.Ordinal);
        Assert.Contains("page: |-\n  <p>one</p>\n  <p>two</p>\n", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyCollectionsAreWrittenInFlowForm()
    {
        var yaml = YamlTreeWriter.Write(Mapping(("a", new YamlMapping(0, 0)), ("b", new YamlSequence(0, 0))));

        Assert.Equal("a: {}\nb: []\n", yaml);
    }

    [Fact]
    public void SequencesAreIndentedUnderTheirKey()
    {
        var item = Mapping(("id", Text("x")), ("name", Text("zed")));
        var yaml = YamlTreeWriter.Write(Mapping(("items", new YamlSequence(0, 0) { Items = { item } })));

        Assert.Equal("items:\n  - id: x\n    name: zed\n", yaml);
    }
}
