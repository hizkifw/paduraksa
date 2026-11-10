using System.Runtime.CompilerServices;
using static Paduraksa.Tests.Architecture.SolutionLayers;

namespace Paduraksa.Tests.Architecture;

public sealed class NamespaceConsistencyTests
{
    public static TheoryData<string> Layers => [.. All];

    [Theory]
    [MemberData(nameof(Layers))]
    public void TypesLiveInTheirLayerNamespace(string layer)
    {
        var offenders = LoadAssembly(layer).GetTypes()
            .Where(t => !IsToolingGenerated(t) && !InLayer(t.Namespace, layer))
            .Select(t => t.FullName)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"Types in {AssemblyNameOf(layer)} must be in namespace Paduraksa.{layer}[.*] so the "
            + $"architecture rules see them: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void SanityToolingFilterDoesNotHideRealTypes() =>
        Assert.False(IsToolingGenerated(typeof(NamespaceConsistencyTests)));

    /// <summary>
    /// Compiler/SDK-emitted types that can't follow the layer namespace: top-level-statement
    /// <c>Program</c>, embedded types marked <c>[Microsoft.CodeAnalysis.Embedded]</c> (nullable
    /// attributes, <c>Microsoft.Extensions.Validation.Embedded.*</c>, ...), closures and other
    /// <c>&lt;...&gt;</c> helpers.
    /// </summary>
    private static bool IsToolingGenerated(Type t) =>
        t.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
        || t.GetCustomAttributesData().Any(a => a.AttributeType.FullName == "Microsoft.CodeAnalysis.EmbeddedAttribute")
        || t.FullName is null
        || t.FullName.Contains('<', StringComparison.Ordinal)
        || (t.Namespace is null && t.Name == "Program")
        || t.Namespace is "Microsoft.CodeAnalysis" or "System.Runtime.CompilerServices";
}
