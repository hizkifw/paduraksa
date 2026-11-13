using ArchUnitNET.Fluent;
using ArchUnitNET.xUnitV3;
using static Paduraksa.Tests.Architecture.SolutionLayers;

namespace Paduraksa.Tests.Architecture;

public sealed class DependencyRuleTests
{
    public static TheoryData<string> Layers => [.. All];

    [Theory]
    [MemberData(nameof(Layers))]
    public void LayerRespectsDependencyGraph(string layer) =>
        ArchRules.LayerDependencies(layer).Check(SolutionModel);

    [Fact]
    public void ProxyDoesNotUseEfCore() => ArchRules.ProxyDoesNotUseEfCore.Check(SolutionModel);

    [Fact]
    public void ProxyDoesNotUseLinq() => ArchRules.ProxyDoesNotUseLinq.Check(SolutionModel);

    [Fact]
    public void ContractsDependOnlyOnCoreAndBcl() => ArchRules.ContractsDependOnlyOnCoreAndBcl.Check(SolutionModel);

    [Fact]
    public void VaultHasNoPublicTypesExceptEntryPoint() => ArchRules.VaultHasNoPublicTypes.Check(SolutionModel);

    // ---- Sanity: the same rules must fire on the violating fixtures (see Fixtures/). ----

    [Theory]
    [MemberData(nameof(Layers))]
    public void SanityLayerRuleFlagsExactlyTheForbiddenLayers(string layer)
    {
        var failures = Failures(ArchRules.LayerDependencies(layer));

        // Non-layer Paduraksa types (tests, the ID generator) are off-limits to every layer.
        Assert.Contains(typeof(Fixtures.NonLayerType).FullName!, failures, StringComparison.Ordinal);

        foreach (var other in All.Where(o => o != layer))
        {
            var fake = FakeTypeName(other);
            if (AllowedDependencies[layer].Contains(other))
            {
                Assert.DoesNotContain(fake, failures, StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains(fake, failures, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void SanityProxyEfRuleFlagsDbContextAndStorage()
    {
        var failures = Failures(ArchRules.ProxyDoesNotUseEfCore);
        Assert.Contains("Microsoft.EntityFrameworkCore.DbContext", failures, StringComparison.Ordinal);
        Assert.Contains(FakeTypeName(Layer.Storage), failures, StringComparison.Ordinal);
    }

    [Fact]
    public void SanityProxyLinqRuleFlagsTypesAndCalls()
    {
        var failures = Failures(ArchRules.ProxyDoesNotUseLinq);
        Assert.Contains("System.Linq.ILookup", failures, StringComparison.Ordinal);
        Assert.Contains("System.Linq.Enumerable", failures, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Paduraksa.Dashboard", true)]
    [InlineData("Paduraksa.Dashboard.Pages", true)]
    [InlineData("__Blazor.Paduraksa.Dashboard.Pages.Routes", true)]
    [InlineData("Paduraksa.DashboardX", false)]
    [InlineData("__Blazor.Paduraksa.Data", false)]
    [InlineData("Other.Paduraksa.Dashboard", false)]
    public void SanityLayerMatchingAcceptsRazorPrefix(string @namespace, bool expected)
    {
        Assert.Equal(expected, InLayer(@namespace, Layer.Dashboard));
        Assert.Equal(expected, System.Text.RegularExpressions.Regex.IsMatch(@namespace, NamespacePattern(Layer.Dashboard)));
    }

    [Fact]
    public void SanityContractsRuleFlagsNonBclAndOtherLayers()
    {
        var failures = Failures(ArchRules.ContractsDependOnlyOnCoreAndBcl);
        Assert.Contains("Microsoft.EntityFrameworkCore.DbContext", failures, StringComparison.Ordinal);
        Assert.Contains(FakeTypeName(Layer.Data), failures, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeTypeName(Layer.Core), failures, StringComparison.Ordinal);
    }

    [Fact]
    public void SanityVaultRuleFlagsPublicTypesButNotEntryPoint()
    {
        var failures = Failures(ArchRules.VaultHasNoPublicTypes);
        Assert.Contains(FakeTypeName(Layer.Vault), failures, StringComparison.Ordinal);
        Assert.Contains("Paduraksa.Vault.ArchFixtures.NonStaticServiceCollectionExtensions", failures, StringComparison.Ordinal);
        Assert.DoesNotContain("FixtureVaultServiceCollectionExtensions", failures, StringComparison.Ordinal);
    }

    private static string FakeTypeName(string layer) => $"Paduraksa.{layer}.ArchFixtures.Fake{layer.Replace('.', ' ').Replace(" ", "")}";

    /// <summary>Evaluates a rule against the fixtures; it must fail, and the failure text is returned.</summary>
    private static string Failures(IArchRule rule)
    {
        var results = rule.Evaluate(FixtureModel).ToList();
        Assert.Contains(results, r => !r.Passed);
        return string.Join(Environment.NewLine, results.Where(r => !r.Passed).Select(r => r.Description));
    }
}
