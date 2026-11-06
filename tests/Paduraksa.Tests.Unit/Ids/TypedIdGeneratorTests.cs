using System.Collections.Immutable;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using Paduraksa.Core.Ids;
using Paduraksa.Ids.Generator;

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
        GeneratorResult result = Run("""
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
        GeneratorResult result = Run("""
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
        string source = declaration.StartsWith("public partial class", StringComparison.Ordinal)
            ? declaration
            : $"[Paduraksa.Core.Ids.TypedId(\"host\")] {declaration}";

        GeneratorResult result = Run(source);

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
        GeneratorResult result = Run($$"""
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
        GeneratorResult result = Run($$"""
            [Paduraksa.Core.Ids.TypedId("{{prefix}}")] public readonly partial record struct SomeId;
            """);

        Assert.Empty(result.GeneratorDiagnostics);
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void NullPrefixReportsPDKID003()
    {
        GeneratorResult result = Run("""
            [Paduraksa.Core.Ids.TypedId(null!)] public readonly partial record struct HostId;
            """);

        Assert.Equal(["PDKID003"], result.GeneratorDiagnosticIds);
    }

    [Fact]
    public void UnknownKindReportsPDKID005()
    {
        GeneratorResult result = Run("""
            [Paduraksa.Core.Ids.TypedId("host", (Paduraksa.Core.Ids.IdKind)42)] public readonly partial record struct HostId;
            """);

        Assert.Equal(["PDKID005"], result.GeneratorDiagnosticIds);
    }

    [Fact]
    public void DuplicatePrefixReportsPDKID004OnEachDeclaration()
    {
        GeneratorResult result = Run("""
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
        GeneratorResult result = Run("""
            [Paduraksa.Core.Ids.TypedId("host")]
            public readonly record struct HostId;
            """);

        Diagnostic diagnostic = Assert.Single(result.GeneratorDiagnostics);
        Assert.Equal(1, diagnostic.Location.GetLineSpan().StartLinePosition.Line);
    }

    [Fact]
    public void GeneratorIsCachedAcrossUnrelatedEdits()
    {
        CSharpCompilation compilation = CreateCompilation("""
            [Paduraksa.Core.Ids.TypedId("host")] public readonly partial record struct HostId;
            """);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new TypedIdGenerator().AsSourceGenerator()],
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGenerators(compilation, TestContext.Current.CancellationToken);

        CSharpCompilation edited = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("public static class Unrelated { }", cancellationToken: TestContext.Current.CancellationToken));
        driver = driver.RunGenerators(edited, TestContext.Current.CancellationToken);

        GeneratorRunResult run = Assert.Single(driver.GetRunResult().Results);
        IEnumerable<IncrementalStepRunReason> reasons = run.TrackedOutputSteps
            .SelectMany(step => step.Value)
            .SelectMany(step => step.Outputs)
            .Select(output => output.Reason);
        Assert.All(reasons, reason => Assert.True(reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged));
    }

    [Fact]
    public void EmissionModelIsCachedWhenDeclarationMovesWithinFile()
    {
        CSharpCompilation compilation = CreateCompilation("""
            [Paduraksa.Core.Ids.TypedId("host")] public readonly partial record struct HostId;
            """);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new TypedIdGenerator().AsSourceGenerator()],
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGenerators(compilation, TestContext.Current.CancellationToken);

        // Insert lines above the declaration: its location changes, its emitted code must not be regenerated.
        SyntaxTree original = compilation.SyntaxTrees.Single();
        SyntaxTree moved = original.WithChangedText(original.GetText(TestContext.Current.CancellationToken).Replace(0, 0, "// moved\n// down\n"));
        driver = driver.RunGenerators(compilation.ReplaceSyntaxTree(original, moved), TestContext.Current.CancellationToken);

        GeneratorRunResult run = Assert.Single(driver.GetRunResult().Results);
        IncrementalGeneratorRunStep step = Assert.Single(run.TrackedSteps[TypedIdGenerator.ModelsTrackingName]);
        Assert.All(step.Outputs, output => Assert.True(output.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged));
        Assert.Single(run.GeneratedSources);
    }

    [Fact]
    public void KeywordIdentifiersAreEscaped()
    {
        GeneratorResult result = Run("""
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
    public void GeneratorKindEnumMatchesCore()
    {
        Assert.Equal(
            Enum.GetValues<IdKind>().Select(k => (k.ToString(), (int)k)),
            Enum.GetValues<IdKindValue>().Select(k => (k.ToString(), (int)k)));
    }

    private static CSharpCompilation CreateCompilation(string source) =>
        CSharpCompilation.Create(
            "Generated",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest), path: "Test.cs")],
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

    private static GeneratorResult Run(string source)
    {
        CSharpCompilation compilation = CreateCompilation(source);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new TypedIdGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation, out Compilation output, out ImmutableArray<Diagnostic> diagnostics, TestContext.Current.CancellationToken);

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
