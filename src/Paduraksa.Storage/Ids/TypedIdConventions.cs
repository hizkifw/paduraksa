using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Paduraksa.Storage.Ids;

/// <summary>
/// EF Core's property discovery, extended to treat every typed ID as a scalar property and every collection of
/// typed IDs as a primitive collection.
/// </summary>
internal sealed class TypedIdPropertyDiscoveryConvention(ProviderConventionSetBuilderDependencies dependencies)
    : PropertyDiscoveryConvention(dependencies)
{
    protected override bool IsCandidatePrimitiveProperty(
        MemberInfo memberInfo, IConventionTypeBase structuralType, out CoreTypeMapping? mapping)
    {
        // The same member rules EF applies to a Guid property: public instance getter, not an indexer, and a setter
        // of any accessibility (possibly declared on a base type). Computed properties like `HostId Other => Id`
        // stay unmapped; fields are never discovered by convention.
        if (memberInfo is PropertyInfo { GetMethod: { IsPublic: true, IsStatic: false } } property
            && property.GetIndexParameters().Length == 0
            && (TypedIdMapping.TryGet(property.PropertyType, out _) || TypedIdMapping.TryGetElement(property.PropertyType, out _)))
        {
            mapping = null;
            return HasSetter(property);
        }

        return base.IsCandidatePrimitiveProperty(memberInfo, structuralType, out mapping);
    }

    private static bool HasSetter(PropertyInfo property)
    {
        const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        for (var type = property.DeclaringType; type is not null; type = type.BaseType)
        {
            if (type.GetProperty(property.Name, Declared)?.SetMethod is not null)
            {
                return true;
            }
        }

        return false;
    }
}

internal sealed class TypedIdConvention : IPropertyAddedConvention, IModelFinalizingConvention
{
    public void ProcessPropertyAdded(
        IConventionPropertyBuilder propertyBuilder, IConventionContext<IConventionPropertyBuilder> context)
    {
        var clrType = propertyBuilder.Metadata.ClrType;
        if (TypedIdMapping.TryGet(clrType, out var mapping))
        {
            propertyBuilder.HasConversion(mapping.ColumnConverter);
            propertyBuilder.HasValueComparer(mapping.Comparer);
            propertyBuilder.ValueGenerated(ValueGenerated.Never);
        }
        else if (TypedIdMapping.TryGetElement(clrType, out mapping))
        {
            ConfigureElements(propertyBuilder, mapping);
        }
    }

    /// <summary>
    /// Completes the mapping once the model is final. Typed IDs inside JSON complex types switch to their string form
    /// only now, because a complex type's members are discovered before <c>ToJson()</c> marks it as a JSON document;
    /// typed IDs that lose their converter to a model rebuild (the TPT link from a derived table's key to the base
    /// table's key) get it back; owned types mapped to JSON are rejected, since their typed IDs would silently keep
    /// the column form inside the document.
    /// </summary>
    public void ProcessModelFinalizing(
        IConventionModelBuilder modelBuilder, IConventionContext<IConventionModelBuilder> context)
    {
        foreach (var entityType in modelBuilder.Metadata.GetEntityTypes())
        {
            if (entityType.IsOwned() && entityType.IsMappedToJson()
                && entityType.GetDeclaredProperties().FirstOrDefault(p => !p.IsShadowProperty() && IsTypedIdOrCollection(p.ClrType)) is { } owned)
            {
                throw new InvalidOperationException(
                    $"'{entityType.DisplayName()}.{owned.Name}' is a typed ID inside an owned type mapped to JSON. Typed IDs in "
                    + "JSON are only supported in complex types: use ComplexProperty(...).ToJson() instead of OwnsOne/OwnsMany(...).ToJson().");
            }

            Apply(entityType, inJson: false);
        }
    }

    private static void Apply(IConventionTypeBase type, bool inJson)
    {
        foreach (var property in type.GetDeclaredProperties())
        {
            if (TypedIdMapping.TryGet(property.ClrType, out var mapping))
            {
                if (inJson)
                {
                    property.Builder.HasConversion(mapping.JsonConverter); // no-op if configured explicitly
                }
                else if (property.GetValueConverter() is null && property.GetProviderClrType() is null)
                {
                    property.Builder.HasConversion(mapping.ColumnConverter);
                    property.Builder.HasValueComparer(mapping.Comparer);
                }
            }
            else if (TypedIdMapping.TryGetElement(property.ClrType, out mapping)
                && property.GetElementType()?.GetValueConverter() is null)
            {
                ConfigureElements(property.Builder, mapping);
            }
        }

        foreach (var complexProperty in type.GetDeclaredComplexProperties())
        {
            Apply(complexProperty.ComplexType, inJson || complexProperty.ComplexType.IsMappedToJson());
        }
    }

    /// <summary>
    /// A primitive collection is stored as a JSON array, so its elements always use the string form.
    /// </summary>
    private static void ConfigureElements(IConventionPropertyBuilder propertyBuilder, TypedIdMapping mapping)
    {
        var elements = propertyBuilder.Metadata.SetElementType(mapping.IdType)?.Builder;
        elements?.HasConversion(mapping.JsonConverter);
        elements?.HasValueComparer(mapping.Comparer);
    }

    private static bool IsTypedIdOrCollection(Type type) =>
        TypedIdMapping.TryGet(type, out _) || TypedIdMapping.TryGetElement(type, out _);
}
