using System.Reflection;
using Paduraksa.Core.Ids;
using Paduraksa.Tests.Architecture.Fixtures;
using static Paduraksa.Tests.Architecture.SolutionLayers;

namespace Paduraksa.Tests.Architecture;

/// <summary>
/// A typed ID's string prefix identifies its type, so prefixes must be unique across the whole solution.
/// The generator (PDKID004) only sees one assembly at a time; this test covers IDs declared in different
/// assemblies (Core, Data, Contracts...).
/// </summary>
public sealed class TypedIdPrefixTests
{
    private static readonly MethodInfo PrefixOfMethod =
        typeof(TypedIdPrefixTests).GetMethod(nameof(PrefixOf), BindingFlags.NonPublic | BindingFlags.Static)!;

    [Fact]
    public void TypedIdPrefixesAreUniqueAcrossSrcAssemblies()
    {
        var duplicates = Duplicates(TypedIds(SrcAssemblies.SelectMany(a => a.GetTypes())));
        Assert.True(duplicates.Count == 0, "Typed-ID prefixes must be unique: " + string.Join("; ", duplicates));
    }

    [Fact]
    public void SanityScanFindsGeneratedIdsAndFlagsDuplicates()
    {
        // The scan finds real generated IDs (here: fixtures in this test assembly) and reads their prefixes.
        var fixtures = TypedIds(typeof(TypedIdPrefixTests).Assembly.GetTypes());
        Assert.Equal(
            [(typeof(FixtureAuditId), "fxaud"), (typeof(FixtureHostId), "fxhost")],
            fixtures.OrderBy(f => f.Prefix, StringComparer.Ordinal));

        // Non-ID look-alikes are ignored.
        Assert.Empty(TypedIds([typeof(FixtureTypedId), typeof(Guid)]));

        // One assembly can't contain a duplicate (PDKID004), so simulate one across assemblies.
        Assert.Equal(
            ["fxhost: Paduraksa.Tests.Architecture.Fixtures.FixtureAuditId, Paduraksa.Tests.Architecture.Fixtures.FixtureHostId"],
            Duplicates([(typeof(FixtureHostId), "fxhost"), (typeof(FixtureAuditId), "fxhost")]));
        Assert.Empty(Duplicates(fixtures));
    }

    /// <summary>Every type that implements <c>ITypedId&lt;itself&gt;</c>, with its prefix.</summary>
    public static List<(Type Type, string Prefix)> TypedIds(IEnumerable<Type> types) =>
    [
        .. from type in types
           where type.IsValueType && !type.IsGenericTypeDefinition
           where type.GetInterfaces().Any(i => i.IsGenericType
               && i.GetGenericTypeDefinition() == typeof(ITypedId<>)
               && i.GetGenericArguments()[0] == type)
           select (type, (string)PrefixOfMethod.MakeGenericMethod(type).Invoke(null, null)!),
    ];

    public static List<string> Duplicates(IEnumerable<(Type Type, string Prefix)> ids) =>
    [
        .. from id in ids
           group id.Type by id.Prefix into g
           where g.Count() > 1
           orderby g.Key
           select $"{g.Key}: {string.Join(", ", g.Select(t => t.FullName).Order(StringComparer.Ordinal))}",
    ];

    // Reads the prefix through the static-abstract contract rather than by member name.
    private static string PrefixOf<T>()
        where T : struct, ITypedId<T> => T.Prefix;
}
