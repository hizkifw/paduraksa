using System.Diagnostics;
using System.Text;

using Paduraksa.Application.Yaml;

using Xunit;

namespace Paduraksa.Tests.Unit.Yaml;

public sealed class YamlTreeReaderTests
{
    private static YamlProblem Fails(string yaml, YamlLimits? limits = null) => Fails(Encoding.UTF8.GetBytes(yaml), limits);

    private static YamlProblem Fails(byte[] yaml, YamlLimits? limits = null)
    {
        Assert.False(YamlTreeReader.TryRead(yaml, limits ?? YamlLimits.Default, out _, out var problem), "the YAML was accepted");
        return problem;
    }

    private static YamlNode Reads(string yaml, YamlLimits? limits = null)
    {
        Assert.True(YamlTreeReader.TryRead(Encoding.UTF8.GetBytes(yaml), limits ?? YamlLimits.Default, out var root, out var problem), problem.Located);
        return root!;
    }

    [Fact]
    public void ReadsMappingsSequencesAndScalarsWithTheirPositions()
    {
        var root = Assert.IsType<YamlMapping>(Reads("a: 1\nb: [x, 'y']\nc:\n  d: \"z\"\n"));

        Assert.Equal(["a", "b", "c"], root.Entries.Select(static e => e.Key));
        Assert.Equal(2, root.Entries[1].KeyLine);
        var one = Assert.IsType<YamlScalar>(root.Find("a")!.Value);
        Assert.True(one.Plain);
        var list = Assert.IsType<YamlSequence>(root.Find("b")!.Value);
        Assert.True(Assert.IsType<YamlScalar>(list.Items[0]).Plain);
        Assert.False(Assert.IsType<YamlScalar>(list.Items[1]).Plain);
        Assert.Equal("z", Assert.IsType<YamlScalar>(Assert.IsType<YamlMapping>(root.Find("c")!.Value).Find("d")!.Value).Text);
    }

    [Theory]
    [InlineData("a: !!str x")]
    [InlineData("a: !Foo {}")]
    [InlineData("a: !<tag:yaml.org,2002:python/object/apply:os.system> [id]")]
    [InlineData("a: !!python/object:System.Diagnostics.Process {}")]
    [InlineData("!!map {a: b}")]
    [InlineData("a: !!seq [1]")]
    public void RefusesEveryTagBecauseNoneMayNameATypeToBuild(string yaml) =>
        Assert.Equal(YamlErrorCodes.Tag, Fails(yaml).Code);

    [Fact]
    public void RefusesMoreThanOneDocumentAndNoDocument()
    {
        Assert.Equal(YamlErrorCodes.Documents, Fails("a: 1\n---\nb: 2\n").Code);
        Assert.Equal(YamlErrorCodes.Documents, Fails("").Code);
        Assert.Equal(YamlErrorCodes.Documents, Fails("# only a comment\n").Code);
    }

    [Fact]
    public void RefusesARepeatedKeyAndAKeyThatIsNotText()
    {
        var repeated = Fails("a: 1\nb: 2\na: 3\n");
        Assert.Equal(YamlErrorCodes.KeyDuplicate, repeated.Code);
        Assert.Equal(3, repeated.Line);

        Assert.Equal(YamlErrorCodes.KeyInvalid, Fails("? [a, b]\n: 1\n").Code);
        Assert.Equal(YamlErrorCodes.KeyInvalid, Fails("? {a: b}\n: 1\n").Code);
    }

    [Fact]
    public void AMergeKeyIsJustAKeyForTheSchemaToRefuse()
    {
        var root = Assert.IsType<YamlMapping>(Reads("base: &b {x: 1}\nmerged:\n  <<: *b\n"));

        Assert.Equal("<<", Assert.IsType<YamlMapping>(root.Find("merged")!.Value).Entries[0].Key);
    }

    [Fact]
    public void RefusesInputOverTheByteLimitBeforeReadingIt() =>
        Assert.Equal(YamlErrorCodes.TooLarge, Fails(new string('a', 101), new YamlLimits(MaxBytes: 100)).Code);

    [Fact]
    public void RefusesBytesThatAreNotUtf8ButAcceptsABom()
    {
        Assert.Equal(YamlErrorCodes.Encoding, Fails([(byte)'a', (byte)':', (byte)' ', 0xFF, 0xFE]).Code);

        byte[] bom = [0xEF, 0xBB, 0xBF, .. "a: 1\n"u8];
        Assert.True(YamlTreeReader.TryRead(bom, YamlLimits.Default, out var root, out _));
        Assert.Equal("a", Assert.IsType<YamlMapping>(root).Entries[0].Key);
    }

    [Fact]
    public void RefusesNestingOverTheDepthLimitInBlockAndFlowForm()
    {
        Assert.Equal(YamlErrorCodes.Depth, Fails(new string('[', 40) + new string(']', 40)).Code);
        Assert.Equal(YamlErrorCodes.Depth, Fails(string.Concat(Enumerable.Range(0, 40).Select(static i => new string(' ', i * 2) + "k:\n"))).Code);
        Reads(new string('[', 20) + new string(']', 20));
    }

    [Fact]
    public void RefusesAScalarOrAKeyOverItsLimit()
    {
        Assert.Equal(YamlErrorCodes.ScalarTooLong, Fails("a: " + new string('x', 101), new YamlLimits(MaxScalarLength: 100)).Code);
        Assert.Equal(YamlErrorCodes.KeyInvalid, Fails(new string('k', 21) + ": 1", new YamlLimits(MaxKeyLength: 20)).Code);
    }

    [Fact]
    public void ABillionLaughsDocumentStopsAtTheNodeBudgetQuicklyAndWithoutMemory()
    {
        // Each level lists the previous one nine times: a few hundred bytes of text, 9^9 nodes if expanded.
        StringBuilder text = new("a: &a [x, x, x, x, x, x, x, x, x]\n");
        var previous = "a";
        foreach (var next in "bcdefghi")
        {
            text.Append(next).Append(": &").Append(next).Append(" [").Append(string.Join(", ", Enumerable.Repeat('*' + previous, 9))).Append("]\n");
            previous = next.ToString();
        }

        var before = GC.GetTotalAllocatedBytes(precise: false);
        var clock = Stopwatch.StartNew();
        var problem = Fails(text.ToString());
        clock.Stop();

        Assert.Equal(YamlErrorCodes.Nodes, problem.Code);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
        Assert.True(GC.GetTotalAllocatedBytes(precise: false) - before < 200L * 1024 * 1024, "allocated too much");
    }

    [Fact]
    public void AnAliasExpandsWithinTheBudgetAndCountsEachUse()
    {
        var root = Assert.IsType<YamlMapping>(Reads("a: &x [1, 2]\nb: *x\nc: *x\n"));

        Assert.Equal(2, Assert.IsType<YamlSequence>(root.Find("c")!.Value).Items.Count);
        Assert.Equal(YamlErrorCodes.Nodes, Fails("a: &x [1, 2]\nb: *x\nc: *x\n", new YamlLimits(MaxNodes: 8)).Code);
    }

    [Fact]
    public void RefusesTooManyAliasesTooManyAnchorsAnUnknownAliasAndALoop()
    {
        Assert.Equal(YamlErrorCodes.AliasLimit, Fails("a: &x 1\nb: [*x, *x, *x]\n", new YamlLimits(MaxAliases: 2)).Code);
        Assert.Equal(YamlErrorCodes.AnchorLimit, Fails("a: &x 1\nb: &y 2\n", new YamlLimits(MaxAnchors: 1)).Code);
        Assert.Equal(YamlErrorCodes.AliasUnknown, Fails("a: *nowhere\n").Code);
        Assert.Equal(YamlErrorCodes.AliasUnknown, Fails("a: &x [*x]\n").Code);
    }

    [Fact]
    public void AnUnknownAliasWithAVeryLongNameIsReportedWithoutEchoingItAll()
    {
        // Found by the yaml-import fuzz target: the message was as long as the name.
        var problem = Fails("a: *" + new string('x', 3000) + "\n");

        Assert.Equal(YamlErrorCodes.AliasUnknown, problem.Code);
        Assert.True(problem.Located.Length < 200, problem.Located);
    }

    [Fact]
    public void ASyntaxErrorCarriesItsPositionAndNoParserTypeNames()
    {
        var problem = Fails("x: 1\ny: [1, 2}\n");

        Assert.Equal(YamlErrorCodes.Syntax, problem.Code);
        Assert.Equal(2, problem.Line);
        Assert.DoesNotContain("YamlDotNet", problem.Located, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnterminatedFlowCollectionIsASyntaxErrorEvenWhereTheScannerThrowsSomethingElse()
    {
        // YamlDotNet's scanner throws InvalidOperationException for this, not its own exception.
        Assert.Equal(YamlErrorCodes.Syntax, Fails("a: [1, 2\nb: 3\n").Code);
    }

    [Fact]
    public void AnAliasedScalarKeepsItsKindAndPositionsPointAtTheAlias()
    {
        var root = Assert.IsType<YamlMapping>(Reads("a: &x \"q\"\nb: *x\n"));

        var copy = Assert.IsType<YamlScalar>(root.Find("b")!.Value);
        Assert.False(copy.Plain);
        Assert.Equal(2, copy.Line);
    }
}
