using System.Diagnostics;
using System.Text.Json;
using static Paduraksa.Tests.Architecture.SolutionLayers;

namespace Paduraksa.Tests.Architecture;

/// <summary>
/// Enforces the PLAN §1 graph at the <b>project</b> level, so it holds even before there is any
/// code (type-level rules pass vacuously on empty projects, and the compiler drops unused
/// references from <c>Assembly.GetReferencedAssemblies()</c>).
/// </summary>
/// <remarks>
/// References are read from MSBuild's <em>evaluated</em> items (<c>dotnet msbuild -getItem</c>), so
/// references added by <c>Directory.Build.props</c>, <c>Update</c>/<c>Remove</c>, conditions and
/// metadata are all seen exactly as the build sees them. Only direct references are checked;
/// transitive ones (e.g. Admin → Application → Data) are unavoidable at the project level and the
/// type-level rules forbid actually using them.
/// </remarks>
public sealed class ProjectReferenceTests
{
    /// <summary>The typed-ID source generator; any project may use it, but only as an analyzer.</summary>
    private const string IdsGenerator = "Ids.Generator";

    public static TheoryData<string> Layers => [.. All];

    [Theory]
    [MemberData(nameof(Layers))]
    public async Task ProjectReferencesFollowDependencyGraph(string layer)
    {
        var violations = Violations(layer, await ProjectReferencesAsync($"Paduraksa.{layer}"));

        Assert.True(
            violations.Count == 0,
            $"Paduraksa.{layer} may reference only [{string.Join(", ", AllowedDependencies[layer])}] "
            + $"(PLAN §1) plus {IdsGenerator} as an analyzer, but references [{string.Join(", ", violations)}]");
    }

    [Fact]
    public async Task IdsGeneratorReferencesNoSolutionProjects() =>
        Assert.Empty(await ProjectReferencesAsync($"Paduraksa.{IdsGenerator}"));

    [Fact]
    public void EveryLayerHasAProject()
    {
        var projects = Directory.GetDirectories(Path.Combine(RepoRoot, "src"))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToList();
        var expected = All.Select(l => $"Paduraksa.{l}").Append($"Paduraksa.{IdsGenerator}").Order(StringComparer.Ordinal);

        // A new src project must be added to the graph in SolutionLayers (and PLAN §1).
        Assert.Equal(expected, projects);
    }

    [Fact]
    public async Task SanityEvaluationSeesReferences()
    {
        // Holds whether the generator is referenced from Core.csproj or from Directory.Build.props.
        var core = await ProjectReferencesAsync($"Paduraksa.{Layer.Core}");
        Assert.Contains(new ProjectReference(IdsGenerator, IsAnalyzerOnly: true), core);

        var application = (await ProjectReferencesAsync($"Paduraksa.{Layer.Application}")).Select(r => r.Name).ToList();
        Assert.Contains(Layer.Data, application);
        Assert.Contains(Layer.Proxy, application);
    }

    [Fact]
    public void SanityRuleFlagsForbiddenAndRuntimeGeneratorReferences()
    {
        Assert.Empty(Violations(Layer.Proxy, [new(Layer.Core, false), new(IdsGenerator, true)]));
        Assert.Equal(
            [Layer.Data, $"{IdsGenerator} (runtime reference)", $"{Layer.Contracts} (analyzer)"],
            Violations(Layer.Proxy, [new(Layer.Data, false), new(IdsGenerator, false), new(Layer.Contracts, true)]));
    }

    /// <summary>A direct project reference, named by layer (the part after <c>Paduraksa.</c>).</summary>
    public sealed record ProjectReference(string Name, bool IsAnalyzerOnly);

    public static List<string> Violations(string layer, IEnumerable<ProjectReference> references) =>
    [
        .. references.Select(r => r switch
        {
            { Name: IdsGenerator, IsAnalyzerOnly: true } => null,
            { Name: IdsGenerator } => $"{r.Name} (runtime reference)",
            { IsAnalyzerOnly: true } => $"{r.Name} (analyzer)",
            _ when AllowedDependencies[layer].Contains(r.Name) => null,
            _ => r.Name,
        }).OfType<string>(),
    ];

    private static async Task<List<ProjectReference>> ProjectReferencesAsync(string project)
    {
        using var json = JsonDocument.Parse(await GetEvaluatedItemsAsync(CsprojPath(project), "ProjectReference"));
        if (!json.RootElement.GetProperty("Items").TryGetProperty("ProjectReference", out var items))
        {
            return [];
        }

        return
        [
            .. items.EnumerateArray().Select(i => new ProjectReference(
                Name: i.GetProperty("Filename").GetString()!.Replace("Paduraksa.", "", StringComparison.Ordinal),
                IsAnalyzerOnly: Metadata(i, "ReferenceOutputAssembly").Equals("false", StringComparison.OrdinalIgnoreCase)
                    && Metadata(i, "OutputItemType").Equals("Analyzer", StringComparison.OrdinalIgnoreCase))),
        ];
    }

    private static string Metadata(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";

    /// <summary>Runs <c>dotnet msbuild &lt;project&gt; -getItem:&lt;item&gt;</c> (evaluation only, no build).</summary>
    private static async Task<string> GetEvaluatedItemsAsync(string csproj, string item)
    {
        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "msbuild", csproj, $"-getItem:{item}", "-nologo" },
        };

        // Don't let the test host's MSBuild environment leak into the child evaluation.
        foreach (var name in new[] { "MSBuildExtensionsPath", "MSBuildSDKsPath", "MSBUILD_EXE_PATH", "MSBuildLoadMicrosoftTargetsReadOnly" })
        {
            psi.Environment.Remove(name);
        }

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.True(process.ExitCode == 0, $"dotnet msbuild {csproj} -getItem:{item} failed:\n{await stdout}\n{await stderr}");
        return await stdout;
    }

    private static string CsprojPath(string project) => Path.Combine(RepoRoot, "src", project, $"{project}.csproj");

    private static string RepoRoot { get; } = FindRepoRoot();

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Paduraksa.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"Paduraksa.slnx not found above {AppContext.BaseDirectory}");
    }
}
