using System.Reflection;
using Paduraksa.Tests.Architecture.Fixtures;
using static Paduraksa.Tests.Architecture.SolutionLayers;

namespace Paduraksa.Tests.Architecture;

public sealed class TypedIdRuleTests
{
    private static readonly HashSet<Type> UntypedIdTypes = [typeof(Guid), typeof(string), typeof(long), typeof(int)];

    public static TheoryData<string> Layers => [Layer.Contracts, Layer.Data];

    [Theory]
    [MemberData(nameof(Layers))]
    public void IdPropertiesAreTyped(string layer)
    {
        var violations = UntypedIdProperties(LoadAssembly(layer).GetTypes());
        Assert.True(
            violations.Count == 0,
            "Use a [TypedId] struct (Paduraksa.Core.Ids) instead of a primitive for: " + string.Join(", ", violations));
    }

    [Fact]
    public void SanityRuleFlagsPrimitiveIdsOnly()
    {
        Assert.Equal(
            ["UntypedIdDto.HostId", "UntypedIdDto.Id", "UntypedIdDto.RouteId", "UntypedIdDto.TenantId"],
            UntypedIdProperties([typeof(UntypedIdDto), typeof(TypedIdDto)]).Order(StringComparer.Ordinal));
    }

    public static List<string> UntypedIdProperties(IEnumerable<Type> types) =>
    [
        .. from type in types
           where !type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), inherit: false)
           from p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
           where p.Name.EndsWith("Id", StringComparison.Ordinal)
           let propertyType = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType
           where UntypedIdTypes.Contains(propertyType)
           select $"{type.Name}.{p.Name}",
    ];
}
