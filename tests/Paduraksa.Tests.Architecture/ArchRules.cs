using ArchUnitNET.Fluent;
using static ArchUnitNET.Fluent.ArchRuleDefinition;
using static Paduraksa.Tests.Architecture.SolutionLayers;

namespace Paduraksa.Tests.Architecture;

/// <summary>
/// ArchUnitNET rules. Each rule is defined once and evaluated twice: against the real solution
/// (<see cref="SolutionLayers.SolutionModel"/>, must pass) and against the fixture types
/// (<see cref="SolutionLayers.FixtureModel"/>, must fail) so a rule that silently matches nothing is caught.
/// </summary>
/// <remarks>
/// <b>Empty layers.</b> ArchUnitNET 0.12 and later fail a rule whose <c>That()</c> clause selects no
/// types ("requires positive evaluation"). Most src projects are still empty, so every rule opts out
/// with <c>WithoutRequiringPositiveResults()</c>. The protection that positive-results would give
/// (the selector is not broken) comes instead from the fixture sanity tests, which require each
/// rule to select the fixture types and report the expected violations.
/// </remarks>
public static class ArchRules
{
    /// <summary>
    /// The layer graph: types in <paramref name="layer"/> may only use solution types from the
    /// layer itself and its allowed dependencies. This single rule covers: Core depends on nothing;
    /// Contracts/Acme/Proxy only on Core; Storage on Core; Data and Vault on Core and Storage; Admin and Dashboard never on Data; nothing on Server.
    /// </summary>
    public static IArchRule LayerDependencies(string layer)
    {
        return Types().That().ResideInNamespaceMatching(NamespacePattern(layer))
            .Should().NotDependOnAny(
                Types(true).That().ResideInNamespaceMatching(AnySolutionNamespacePattern)
                    .And().DoNotResideInNamespaceMatching(NamespacePattern(AllowedDependencies[layer].Append(layer))))
            .Because($"PLAN §1 allows {layer} to depend only on [{string.Join(", ", AllowedDependencies[layer])}]")
            .WithoutRequiringPositiveResults();
    }

    /// <summary>
    /// The hot path cannot reach the database: no EF Core types, and none of the shared EF/SQLite infrastructure
    /// (<c>Paduraksa.Storage</c>), in Proxy. (The layer graph already forbids the Storage edge; this states the intent.)
    /// </summary>
    public static IArchRule ProxyDoesNotUseEfCore { get; } =
        Types().That().ResideInNamespaceMatching(NamespacePattern(Layer.Proxy))
            .Should().NotDependOnAny(
                Types(true).That().ResideInNamespaceMatching(
                    $@"^(Microsoft\.EntityFrameworkCore|Microsoft\.Data\.Sqlite)(\..+)?$|{NamespacePattern(Layer.Storage)}"))
            .Because("PLAN §1: the hot path can't reach the DB")
            .WithoutRequiringPositiveResults();

    /// <summary>Hot-path allocation discipline: no LINQ in Proxy.</summary>
    /// <remarks>
    /// TODO: further hot-path bans, not yet encoded:
    /// - (M7, when the WAF path/UA policies bring the first regexes) <c>System.Text.RegularExpressions.Regex</c>
    ///   constructed without <c>RegexOptions.NonBacktracking</c> (needs a call-argument check, not just a type
    ///   dependency);
    /// - (M4, with the Admin API's serializer contexts) no <c>Paduraksa.Data</c> type registered in a
    ///   <c>System.Text.Json</c> serializer context.
    /// </remarks>
    public static IArchRule ProxyDoesNotUseLinq { get; } =
        Types().That().ResideInNamespaceMatching(NamespacePattern(Layer.Proxy))
            .Should().NotDependOnAny(Types(true).That().ResideInNamespaceMatching(@"^System\.Linq(\..+)?$"))
            .Because("docs/design/testing.md, Architecture tests: the hot path avoids LINQ (allocations, delegate calls)")
            .WithoutRequiringPositiveResults();

    /// <summary>
    /// Contracts are plain DTOs: only Core, Contracts itself and the BCL (<c>System.*</c>, plus the
    /// global namespace for compiler-generated helpers like <c>&lt;PrivateImplementationDetails&gt;</c>).
    /// </summary>
    public static IArchRule ContractsDependOnlyOnCoreAndBcl { get; } =
        Types().That().ResideInNamespaceMatching(NamespacePattern(Layer.Contracts))
            .Should().OnlyDependOn(
                Types(true).That().ResideInNamespaceMatching(
                    $@"^(|System(\..+)?|Paduraksa\.(Core|Contracts)(\..+)?)$"))
            .Because("PLAN §1: Contracts are DTOs (no EF, no entities, no framework types)")
            .WithoutRequiringPositiveResults();

    /// <summary>Allowed public type in Vault: a static class named <c>*ServiceCollectionExtensions</c>.</summary>
    public const string VaultEntryPointSuffix = "ServiceCollectionExtensions";

    /// <summary>
    /// Vault is a trust boundary: everything is internal except the DI-registration entry point.
    /// The one exemption is <c>Paduraksa.Vault.Migrations</c>: <c>dotnet ef</c> emits public migration classes and the
    /// repo commits its output verbatim; they hold schema only (the vault's own tests check that nothing else is there).
    /// Note: a public type nested in an internal type is also flagged (stricter than C# visibility).
    /// "Static class" is matched as abstract + sealed, which is how C# emits it in IL.
    /// </summary>
    public static IArchRule VaultHasNoPublicTypes { get; } =
        Types().That().ResideInNamespaceMatching(NamespacePattern(Layer.Vault)).And().ArePublic()
            .And().DoNotResideInNamespaceMatching(@"^Paduraksa\.Vault\.Migrations$")
            .Should().Be(Classes().That().AreAbstract().And().AreSealed().And().HaveNameEndingWith(VaultEntryPointSuffix))
            .Because("docs/design/vault-and-certificates.md, What the vault holds: Vault types are internal; only its DI registration entry point is public")
            .WithoutRequiringPositiveResults();
}
