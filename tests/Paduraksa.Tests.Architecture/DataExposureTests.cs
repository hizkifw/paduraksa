using System.Reflection;
using Paduraksa.Tests.Architecture.Fixtures;
using static Paduraksa.Tests.Architecture.SolutionLayers;

namespace Paduraksa.Tests.Architecture;

/// <summary>
/// PLAN §2: DTOs are decoupled from entities. No <c>Paduraksa.Data*</c> type may appear in the
/// public surface (properties, fields, method/constructor parameters and returns, events, base
/// types, interfaces, including generic arguments and arrays) of Contracts, Admin or Dashboard.
/// </summary>
/// <remarks>
/// The dependency-graph rule already forbids these layers from using Data at all; this check is
/// the explicit DTO-isolation guard and also covers exposure via inherited/generic signatures.
/// </remarks>
public sealed class DataExposureTests
{
    private const BindingFlags VisibleMembers =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    public static TheoryData<string> Layers => [Layer.Contracts, Layer.Admin, Layer.Dashboard];

    [Theory]
    [MemberData(nameof(Layers))]
    public void PublicSurfaceExposesNoDataTypes(string layer)
    {
        var leaks = DataLeaks(LoadAssembly(layer).GetExportedTypes());
        Assert.True(leaks.Count == 0, "Data (entity) types leak into the public surface: " + string.Join("; ", leaks));
    }

    [Fact]
    public void SanityRuleFlagsEveryLeakShape()
    {
        var leaks = DataLeaks([typeof(LeakyDto), typeof(CleanDto)]);
        foreach (var member in new[] { ".ctor", "Entity", "Entities", "EntityArray", "Accept", "Produce", "ProduceAsync" })
        {
            Assert.Contains(leaks, l => l.StartsWith($"LeakyDto.{member} ", StringComparison.Ordinal));
        }

        Assert.DoesNotContain(leaks, l => l.Contains("Hidden", StringComparison.Ordinal));
        Assert.DoesNotContain(leaks, l => l.StartsWith("CleanDto", StringComparison.Ordinal));
    }

    public static List<string> DataLeaks(IEnumerable<Type> types)
    {
        var leaks = new List<string>();
        foreach (var type in types.Where(t => t.IsVisible))
        {
            void Check(string member, Type? exposed)
            {
                if (exposed is not null && FindDataType(exposed) is { } data)
                {
                    leaks.Add($"{type.Name}.{member} exposes {data.FullName}");
                }
            }

            Check("<base>", type.BaseType);
            foreach (var i in type.GetInterfaces())
            {
                Check("<interface>", i);
            }

            foreach (var member in type.GetMembers(VisibleMembers).Where(IsVisibleOutside))
            {
                switch (member)
                {
                    case PropertyInfo p:
                        Check(p.Name, p.PropertyType);
                        break;
                    case FieldInfo f:
                        Check(f.Name, f.FieldType);
                        break;
                    case EventInfo e:
                        Check(e.Name, e.EventHandlerType);
                        break;
                    case MethodBase m:
                        if (m is MethodInfo mi)
                        {
                            Check(m.Name, mi.ReturnType);
                        }

                        foreach (var parameter in m.GetParameters())
                        {
                            Check(m.Name, parameter.ParameterType);
                        }

                        break;
                }
            }
        }

        return leaks;
    }

    /// <summary>Public or protected (i.e. reachable from another assembly).</summary>
    private static bool IsVisibleOutside(MemberInfo member) => member switch
    {
        PropertyInfo p => p.GetAccessors(nonPublic: true).Any(a => a.IsPublic || a.IsFamily || a.IsFamilyOrAssembly),
        FieldInfo f => f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly,
        EventInfo e => e.AddMethod is { } add && (add.IsPublic || add.IsFamily || add.IsFamilyOrAssembly),
        // Property/event accessors are covered by their PropertyInfo/EventInfo; constructors and operators are not.
        MethodBase m => (m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly)
            && (m is ConstructorInfo || !m.IsSpecialName || m.Name.StartsWith("op_", StringComparison.Ordinal)),
        _ => false,
    };

    private static Type? FindDataType(Type type)
    {
        if (type.HasElementType)
        {
            return FindDataType(type.GetElementType()!);
        }

        if (InLayer(type.Namespace, Layer.Data))
        {
            return type;
        }

        return type.IsGenericType
            ? type.GetGenericArguments().Select(FindDataType).FirstOrDefault(t => t is not null)
            : null;
    }
}
