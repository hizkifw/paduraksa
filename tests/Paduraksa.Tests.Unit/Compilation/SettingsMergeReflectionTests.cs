using System.Reflection;

using Paduraksa.Application.Compilation;
using Paduraksa.Contracts.Config;

namespace Paduraksa.Tests.Unit.Compilation;

/// <summary>
/// The merge walked from the contract itself, not from a table: every leaf of <see cref="HostSettings"/> (nested records
/// included) must survive a merge from either layer. A field added to the contract and forgotten in
/// <see cref="HostSettingsLayers.Merge(HostSettings?, HostSettings?)"/> fails here without anyone writing a row.
/// </summary>
public sealed class SettingsMergeReflectionTests
{
    [Fact]
    public void EveryLeafSurvivesAMergeFromEitherLayer()
    {
        foreach (Leaf leaf in Leaves())
        {
            object sample = leaf.Sample();

            object? fromUpper = leaf.Read(HostSettingsLayers.Merge(new HostSettings(), leaf.Set(sample)));
            Assert.True(Equals(sample, fromUpper), $"{leaf.Name} did not survive a merge from the upper layer (got {fromUpper ?? "null"})");

            object? fromLower = leaf.Read(HostSettingsLayers.Merge(leaf.Set(sample), new HostSettings()));
            Assert.True(Equals(sample, fromLower), $"{leaf.Name} did not survive a merge from the lower layer (got {fromLower ?? "null"})");
        }
    }

    [Fact]
    public void TheBuiltInDefaultsStateAValueForEveryLeafThatHasOne()
    {
        foreach (Leaf leaf in Leaves())
        {
            object? value = leaf.Read(HostSettingsLayers.BuiltIn);
            if (leaf.Name is nameof(HostSettings.IpAllowList) or $"{nameof(HostSettings.ServerHeader)}.{nameof(ServerHeaderSettings.Value)}")
            {
                // The absence is the default: no restriction, and no custom Server value.
                Assert.Null(value);
                continue;
            }

            Assert.NotNull(value);
        }
    }

    /// <summary>Every leaf of the contract, nested settings records opened up.</summary>
    private static IEnumerable<Leaf> Leaves()
    {
        foreach (PropertyInfo property in typeof(HostSettings).GetProperties())
        {
            if (!typeof(ISettingsLayer).IsAssignableFrom(property.PropertyType))
            {
                yield return new Leaf(
                    property.Name,
                    () => Sample(property.PropertyType),
                    sample => Set(property, sample),
                    settings => property.GetValue(settings));
                continue;
            }

            foreach (PropertyInfo inner in property.PropertyType.GetProperties().Where(p => p.Name != nameof(ISettingsLayer.SetsNothing)))
            {
                yield return new Leaf(
                    $"{property.Name}.{inner.Name}",
                    () => Sample(inner.PropertyType),
                    sample =>
                    {
                        object record = Activator.CreateInstance(property.PropertyType)!;
                        inner.SetValue(record, sample);
                        return Set(property, record);
                    },
                    settings => property.GetValue(settings) is { } record ? inner.GetValue(record) : null);
            }
        }
    }

    private static HostSettings Set(PropertyInfo property, object value)
    {
        HostSettings settings = new();
        property.SetValue(settings, value);
        return settings;
    }

    /// <summary>A non-null sample of <paramref name="type"/>; a new leaf type has to be added here.</summary>
    private static object Sample(Type type)
    {
        Type value = Nullable.GetUnderlyingType(type) ?? type;
        if (value == typeof(bool))
        {
            return true;
        }

        if (value == typeof(int))
        {
            return 1;
        }

        if (value == typeof(string))
        {
            return "x";
        }

        if (value == typeof(IpAllowListSettings))
        {
            return new IpAllowListSettings(["192.0.2.1"], DropDenied: true);
        }

        if (value.IsEnum)
        {
            object zero = Activator.CreateInstance(value)!;
            return Enum.GetValues(value).Cast<object>().First(member => !member.Equals(zero));
        }

        throw new InvalidOperationException($"No sample for {type}: extend Sample so a new setting is covered.");
    }

    /// <summary>One leaf of the contract: how to set it on a layer and read it back.</summary>
    private sealed record Leaf(string Name, Func<object> Sample, Func<object, HostSettings> Set, Func<HostSettings, object?> Read);
}
