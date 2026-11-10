using System.Collections.Immutable;
using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using Paduraksa.Core.Ids;
using Paduraksa.Ids.Generator;

// Paduraksa.Tests.Unit.Compilation (the config compiler tests) hides Roslyn's Compilation here.
using RoslynCompilation = Microsoft.CodeAnalysis.Compilation;

namespace Paduraksa.Tests.Unit.Ids;

public sealed class TypedIdGeneratorTests
{
    private static readonly Lazy<MetadataReference[]> References = new(() =>
    [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Append(typeof(TypedIdAttribute).Assembly.Location)
            .Distinct(StringComparer.Ordinal)
            .Select(path => MetadataReference.CreateFromFile(path)),
    ]);

    [Fact]
    public void ValidDeclarationsGenerateCompilingCode()
    {
        var result = Run("""
                         using Paduraksa.Core.Ids;

                         namespace Sample
                         {
                             [TypedId("host")] public readonly partial record struct HostId;
                             [TypedId("chl", IdKind.UuidV4)] public readonly partial record struct ChallengeId;
                             [TypedId("aud", IdKind.Int64Ordered)] internal readonly partial record struct AuditEventId;

                             public static class Use
                             {
                                 public static string Roundtrip() => HostId.Parse(HostId.New().ToString()).ToString()
                                     + ChallengeId.New() + AuditEventId.New().Value;
                             }
                         }

                         [TypedId("glob")] public readonly partial record struct GlobalId;
                         """);

        Assert.Empty(result.GeneratorDiagnostics);
        Assert.Empty(result.CompilationErrors);
        Assert.Equal(4, result.GeneratedSources.Length);
        Assert.Contains("Sample.HostId.TypedId.g.cs", result.GeneratedSources);
        Assert.Contains("GlobalId.TypedId.g.cs", result.GeneratedSources);
    }

    [Fact]
    public void NotPartialReportsPDKID001()
    {
        var result = Run("""
                         [Paduraksa.Core.Ids.TypedId("host")] public readonly record struct HostId;
                         """);

        Assert.Equal(["PDKID001"], result.GeneratorDiagnosticIds);
        Assert.Empty(result.GeneratedSources);
    }

    [Theory]
    [InlineData("public partial record struct HostId;")] // not readonly
    [InlineData("public readonly partial struct HostId { }")] // not a record
    [InlineData("public readonly partial record struct HostId(System.Guid Value);")] // positional
    [InlineData("public readonly partial record struct HostId<T>;")] // generic
    [InlineData("public partial class Outer { [Paduraksa.Core.Ids.TypedId(\"host\")] public readonly partial record struct HostId; }")] // nested
    public void NotReadonlyRecordStructReportsPDKID002(string declaration)
    {
        var source = declaration.StartsWith("public partial class", StringComparison.Ordinal)
            ? declaration
            : $"[Paduraksa.Core.Ids.TypedId(\"host\")] {declaration}";

        var result = Run(source);

        Assert.Equal(["PDKID002"], result.GeneratorDiagnosticIds);
        Assert.Empty(result.GeneratedSources);
    }

    [Theory]
    [InlineData("h")]
    [InlineData("")]
    [InlineData("Host")]
    [InlineData("1host")]
    [InlineData("ho_st")]
    [InlineData("ho-st")]
    [InlineData("toolongpx")]
    [InlineData("hóst")]
    public void InvalidPrefixReportsPDKID003(string prefix)
    {
        var result = Run($$"""
                           [Paduraksa.Core.Ids.TypedId("{{prefix}}")] public readonly partial record struct HostId;
                           """);

        Assert.Equal(["PDKID003"], result.GeneratorDiagnosticIds);
        Assert.Empty(result.GeneratedSources);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("a1")]
    [InlineData("abcdefgh")]
    [InlineData("usr2fa")]
    public void ValidPrefixesAreAccepted(string prefix)
    {
        var result = Run($$"""
                           [Paduraksa.Core.Ids.TypedId("{{prefix}}")] public readonly partial record struct SomeId;
                           """);

        Assert.Empty(result.GeneratorDiagnostics);
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void NullPrefixReportsPDKID003()
    {
        var result = Run("""
                         [Paduraksa.Core.Ids.TypedId(null!)] public readonly partial record struct HostId;
                         """);

        Assert.Equal(["PDKID003"], result.GeneratorDiagnosticIds);
    }

    [Fact]
    public void UnknownKindReportsPDKID005()
    {
        var result = Run("""
                         [Paduraksa.Core.Ids.TypedId("host", (Paduraksa.Core.Ids.IdKind)42)] public readonly partial record struct HostId;
                         """);

        Assert.Equal(["PDKID005"], result.GeneratorDiagnosticIds);
    }

    [Fact]
    public void DuplicatePrefixReportsPDKID004OnEachDeclaration()
    {
        var result = Run("""
                         using Paduraksa.Core.Ids;
                         namespace A { [TypedId("host")] public readonly partial record struct HostId; }
                         namespace B { [TypedId("host", IdKind.UuidV4)] public readonly partial record struct OtherHostId; }
                         namespace C { [TypedId("usr")] public readonly partial record struct UserId; }
                         """);

        Assert.Equal(["PDKID004", "PDKID004"], result.GeneratorDiagnosticIds);
        Assert.Contains(result.GeneratorDiagnostics, d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture).Contains("B.OtherHostId", StringComparison.Ordinal));
        Assert.All(result.GeneratorDiagnostics, d => Assert.Equal("Test.cs", d.Location.GetLineSpan().Path));
    }

    [Fact]
    public void DiagnosticsPointAtTheDeclaration()
    {
        var result = Run("""
                         [Paduraksa.Core.Ids.TypedId("host")]
                         public readonly record struct HostId;
                         """);

        var diagnostic = Assert.Single(result.GeneratorDiagnostics);
        Assert.Equal(1, diagnostic.Location.GetLineSpan().StartLinePosition.Line);
    }

    [Fact]
    public void GeneratorIsCachedAcrossUnrelatedEdits()
    {
        var compilation = CreateCompilation("""
                                            [Paduraksa.Core.Ids.TypedId("host")] public readonly partial record struct HostId;
                                            """);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new TypedIdGenerator().AsSourceGenerator()],
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGenerators(compilation, TestContext.Current.CancellationToken);

        var edited = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("public static class Unrelated { }", cancellationToken: TestContext.Current.CancellationToken));
        driver = driver.RunGenerators(edited, TestContext.Current.CancellationToken);

        var run = Assert.Single(driver.GetRunResult().Results);
        var reasons = run.TrackedOutputSteps
            .SelectMany(step => step.Value)
            .SelectMany(step => step.Outputs)
            .Select(output => output.Reason);
        Assert.All(reasons, reason => Assert.True(reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged));
    }

    [Fact]
    public void EmissionModelIsCachedWhenDeclarationMovesWithinFile()
    {
        var compilation = CreateCompilation("""
                                            [Paduraksa.Core.Ids.TypedId("host")] public readonly partial record struct HostId;
                                            """);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new TypedIdGenerator().AsSourceGenerator()],
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGenerators(compilation, TestContext.Current.CancellationToken);

        // Insert lines above the declaration: its location changes, its emitted code must not be regenerated.
        var original = compilation.SyntaxTrees.Single();
        var moved = original.WithChangedText(original.GetText(TestContext.Current.CancellationToken).Replace(0, 0, "// moved\n// down\n"));
        driver = driver.RunGenerators(compilation.ReplaceSyntaxTree(original, moved), TestContext.Current.CancellationToken);

        var run = Assert.Single(driver.GetRunResult().Results);
        var step = Assert.Single(run.TrackedSteps[TypedIdGenerator.ModelsTrackingName]);
        Assert.All(step.Outputs, output => Assert.True(output.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged));
        Assert.Single(run.GeneratedSources);
    }

    [Fact]
    public void KeywordIdentifiersAreEscaped()
    {
        var result = Run("""
                         namespace Bar.@event
                         {
                             [Paduraksa.Core.Ids.TypedId("ev")] public readonly partial record struct @class;

                             public static class Use
                             {
                                 public static string Roundtrip() => @class.Parse(@class.New().ToString()).ToString();
                             }
                         }

                         [Paduraksa.Core.Ids.TypedId("st", Paduraksa.Core.Ids.IdKind.Int64Ordered)] public readonly partial record struct @struct;
                         """);

        Assert.Empty(result.GeneratorDiagnostics);
        Assert.Empty(result.CompilationErrors);
        Assert.Equal(["Bar.event.class.TypedId.g.cs", "struct.TypedId.g.cs"], result.GeneratedSources.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void FileLocalTypedIdsReportPDKID006()
    {
        // Same name and namespace in two files is legal for file-local types; it used to collide on the hint name.
        const string Declaration = """
            namespace Sample { [Paduraksa.Core.Ids.TypedId("$PREFIX$")] file readonly partial record struct HostId; }
            """;

        var result = Run(
            ("A.cs", Declaration.Replace("$PREFIX$", "hosta", StringComparison.Ordinal)),
            ("B.cs", Declaration.Replace("$PREFIX$", "hostb", StringComparison.Ordinal)));

        Assert.Equal(["PDKID006", "PDKID006"], result.GeneratorDiagnosticIds);
        Assert.Equal(["A.cs", "B.cs"], result.GeneratorDiagnostics.Select(d => d.Location.GetLineSpan().Path).Order(StringComparer.Ordinal));
        Assert.Empty(result.CompilationErrors);
        Assert.Empty(result.GeneratedSources);
    }

    [Fact]
    public void GeneratedCodeAttributeCarriesGeneratorVersion()
    {
        const string Source = """
            [Paduraksa.Core.Ids.TypedId("host")] public readonly partial record struct HostId;
            """;

        var first = GenerateSingleSource(Source);
        var match = Regex.Match(first, @"GeneratedCode\(""Paduraksa\.Ids\.Generator"", ""(?<version>[^""]*)""\)");

        Assert.True(match.Success);
        var version = match.Groups["version"].Value;
        Assert.Matches(@"^\d+\.\d+\.\d+\.\d+$", version);
        Assert.NotEqual("0.0.0.0", version);
        Assert.Equal(typeof(TypedIdGenerator).Assembly.GetName().Version?.ToString(), version);
        Assert.Equal(first, GenerateSingleSource(Source));
    }

    [Fact]
    public void GeneratorKindEnumMatchesCore()
    {
        Assert.Equal(
            Enum.GetValues<IdKind>().Select(k => (k.ToString(), (int)k)),
            Enum.GetValues<IdKindValue>().Select(k => (k.ToString(), (int)k)));
    }

    private static CSharpCompilation CreateCompilation(string source) => CreateCompilation(("Test.cs", source));

    private static CSharpCompilation CreateCompilation(params (string Path, string Source)[] files) =>
        CSharpCompilation.Create(
            "Generated",
            files.Select(file => CSharpSyntaxTree.ParseText(
                file.Source, new CSharpParseOptions(LanguageVersion.Latest), file.Path, cancellationToken: TestContext.Current.CancellationToken)),
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

    private static string GenerateSingleSource(string source)
    {
        var driver = CSharpGeneratorDriver.Create(new TypedIdGenerator())
            .RunGenerators(CreateCompilation(source), TestContext.Current.CancellationToken);
        return Assert.Single(driver.GetRunResult().Results.SelectMany(r => r.GeneratedSources)).SourceText.ToString();
    }

    private static GeneratorResult Run(string source) => Run(("Test.cs", source));

    private static GeneratorResult Run(params (string Path, string Source)[] files)
    {
        var compilation = CreateCompilation(files);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new TypedIdGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation, out var output, out var diagnostics, TestContext.Current.CancellationToken);

        return new GeneratorResult(
            diagnostics,
            [.. output.GetDiagnostics(TestContext.Current.CancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error)],
            [.. driver.GetRunResult().Results.SelectMany(r => r.GeneratedSources).Select(s => s.HintName)]);
    }

    private sealed record GeneratorResult(
        ImmutableArray<Diagnostic> GeneratorDiagnostics,
        Diagnostic[] CompilationErrors,
        string[] GeneratedSources)
    {
        public string[] GeneratorDiagnosticIds => [.. GeneratorDiagnostics.Select(d => d.Id).Order(StringComparer.Ordinal)];
    }
}
