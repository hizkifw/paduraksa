using System.Reflection;
using System.Text.RegularExpressions;
using ArchUnitNET.Loader;
using ArchitectureModel = ArchUnitNET.Domain.Architecture;

namespace Paduraksa.Tests.Architecture;

/// <summary>Layer names. Always written <c>Layer.X</c>: a bare <c>Proxy</c> etc. would bind to the <c>Paduraksa.Proxy</c> namespace.</summary>
public static class Layer
{
    public const string Core = "Core";
    public const string Contracts = "Contracts";
    public const string Storage = "Storage";
    public const string Data = "Data";
    public const string Vault = "Vault";
    public const string Acme = "Acme";
    public const string Notifications = "Notifications";
    public const string Mcp = "Mcp";
    public const string Proxy = "Proxy";
    public const string Application = "Application";
    public const string Admin = "Admin";
    public const string Dashboard = "Dashboard";
    public const string Server = "Server";
}

/// <summary>
/// The solution's layers and the allowed dependency graph from docs/PLAN.md §1.
/// </summary>
/// <remarks>
/// Layers are identified by <b>namespace</b> (<c>Paduraksa.&lt;Layer&gt;</c> and below), not by assembly.
/// <see cref="NamespaceConsistencyTests"/> pins every type in each src assembly to its layer's
/// namespace, so the two are equivalent for production code. Matching by namespace lets the
/// sanity tests run the <em>exact same</em> rule objects against deliberately violating fixture
/// types that live in the test assembly (see <c>Fixtures/</c>), without putting bad code in src.
/// </remarks>
public static class SolutionLayers
{
    /// <summary>Allowed direct dependencies per layer. A layer may always use itself.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> AllowedDependencies = new Dictionary<string, string[]>
    {
        [Layer.Core] = [],
        [Layer.Contracts] = [Layer.Core],
        [Layer.Storage] = [Layer.Core],
        [Layer.Data] = [Layer.Core, Layer.Storage],
        [Layer.Vault] = [Layer.Core, Layer.Storage],
        [Layer.Acme] = [Layer.Core],
        [Layer.Notifications] = [Layer.Core],
        [Layer.Mcp] = [],
        [Layer.Proxy] = [Layer.Core],
        [Layer.Application] = [Layer.Core, Layer.Contracts, Layer.Data, Layer.Proxy, Layer.Acme, Layer.Notifications],
        [Layer.Admin] = [Layer.Core, Layer.Application, Layer.Contracts, Layer.Mcp],
        [Layer.Dashboard] = [Layer.Core, Layer.Application, Layer.Contracts],
        [Layer.Server] =
        [
            Layer.Core, Layer.Contracts, Layer.Storage, Layer.Data, Layer.Vault, Layer.Acme, Layer.Notifications,
            Layer.Proxy, Layer.Application, Layer.Admin, Layer.Dashboard,
        ],
    };

    public static IEnumerable<string> All => AllowedDependencies.Keys;

    /// <summary>Assembly names of the src projects (Server's assembly is the <c>paduraksa</c> binary).</summary>
    public static string AssemblyNameOf(string layer) => layer == Layer.Server ? "paduraksa" : $"Paduraksa.{layer}";

    /// <summary>
    /// Prefix the Razor compiler puts in front of the component namespace for its generated helpers
    /// (e.g. <c>__Blazor.Paduraksa.Dashboard.Pages.Foo.TypeInference</c>); those belong to the layer too.
    /// </summary>
    public const string RazorGeneratedPrefix = "__Blazor.";

    /// <summary>Regex for "any solution namespace" (optionally Razor-prefixed).</summary>
    public const string AnySolutionNamespacePattern = @"^(__Blazor\.)?Paduraksa(\..+)?$";

    /// <summary>Namespace regex for a layer: <c>Paduraksa.X</c> or anything below it (optionally Razor-prefixed).</summary>
    public static string NamespacePattern(string layer) => NamespacePattern([layer]);

    /// <summary>Namespace regex matching any of <paramref name="layers"/>.</summary>
    public static string NamespacePattern(IEnumerable<string> layers) =>
        $@"^(__Blazor\.)?Paduraksa\.({string.Join('|', layers.Select(l => Regex.Escape(l)))})(\..+)?$";

    public static bool InLayer(string? @namespace, string layer)
    {
        if (@namespace is null)
        {
            return false;
        }

        if (@namespace.StartsWith(RazorGeneratedPrefix, StringComparison.Ordinal))
        {
            @namespace = @namespace[RazorGeneratedPrefix.Length..];
        }

        return @namespace == $"Paduraksa.{layer}" || @namespace.StartsWith($"Paduraksa.{layer}.", StringComparison.Ordinal);
    }

    public static Assembly LoadAssembly(string layer) => Assembly.Load(new AssemblyName(AssemblyNameOf(layer)));

    public static IReadOnlyList<Assembly> SrcAssemblies { get; } = [.. All.Select(LoadAssembly)];

    /// <summary>The real solution: every src assembly (and nothing from tests).</summary>
    public static ArchitectureModel SolutionModel { get; } =
        new ArchLoader().LoadAssemblies([.. SrcAssemblies]).Build();

    /// <summary>
    /// This test assembly, which contains intentionally violating fixture types in the
    /// <c>Paduraksa.&lt;Layer&gt;.ArchFixtures</c> namespaces. Used only to prove rules can fail.
    /// </summary>
    public static ArchitectureModel FixtureModel { get; } =
        new ArchLoader().LoadAssemblies(typeof(SolutionLayers).Assembly).Build();
}
