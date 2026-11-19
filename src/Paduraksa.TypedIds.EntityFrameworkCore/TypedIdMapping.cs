using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Paduraksa.TypedIds;

namespace Paduraksa.TypedIds.EntityFrameworkCore;

/// <summary>
/// Recognizes typed-ID CLR types and hands out their cached, immutable converters and comparers.
/// </summary>
internal sealed class TypedIdMapping
{
    private static readonly ConcurrentDictionary<Type, TypedIdMapping?> Cache = new();

    private TypedIdMapping(Type idType, Type valueType)
    {
        IdType = idType;
        ValueType = valueType;
        ColumnConverter = (ValueConverter)Activator.CreateInstance(
            (valueType == typeof(Guid) ? typeof(TypedIdToBytesConverter<>) : typeof(TypedIdToInt64Converter<>))
                .MakeGenericType(idType))!;
        NativeGuidConverter = valueType == typeof(Guid)
            ? (ValueConverter)Activator.CreateInstance(typeof(TypedIdToGuidConverter<>).MakeGenericType(idType))!
            : null;
        JsonConverter = (ValueConverter)Activator.CreateInstance(typeof(TypedIdToStringConverter<>).MakeGenericType(idType))!;
        Comparer = (ValueComparer)Activator.CreateInstance(typeof(TypedIdValueComparer<>).MakeGenericType(idType))!;
    }

    /// <summary>The typed-ID struct, never its nullable form.</summary>
    public Type IdType { get; }

    /// <summary><see cref="Guid"/> or <see cref="long"/>.</summary>
    public Type ValueType { get; }

    /// <summary>Default column storage: 16-byte big-endian BLOB for Guid-backed IDs, INTEGER for Int64Ordered.</summary>
    public ValueConverter ColumnConverter { get; }

    /// <summary>The provider's own Guid column for Guid-backed IDs; null for Int64Ordered.</summary>
    public ValueConverter? NativeGuidConverter { get; }

    /// <summary>
    /// The column converter for <paramref name="guidColumns"/>. The mapping is shared by every context in the process, so
    /// the choice is made per call here and never stored on the mapping.
    /// </summary>
    public ValueConverter ColumnConverterFor(TypedIdGuidColumns guidColumns) =>
        guidColumns == TypedIdGuidColumns.Native ? NativeGuidConverter ?? ColumnConverter : ColumnConverter;

    /// <summary>Storage inside JSON documents: the canonical string form.</summary>
    public ValueConverter JsonConverter { get; }

    public ValueComparer Comparer { get; }

    /// <summary>Finds the typed-ID mapping for <paramref name="clrType"/> (or its nullable form).</summary>
    public static bool TryGet(Type clrType, [NotNullWhen(true)] out TypedIdMapping? mapping)
    {
        mapping = Cache.GetOrAdd(Nullable.GetUnderlyingType(clrType) ?? clrType, Create);
        return mapping is not null;
    }

    /// <summary>
    /// Finds the typed-ID element mapping of a primitive collection type: an array, or a generic collection type
    /// implementing <see cref="IEnumerable{T}"/> of a typed ID (<c>List&lt;RouteId&gt;</c>, <c>IReadOnlyList&lt;RouteId&gt;</c>, ...).
    /// </summary>
    public static bool TryGetElement(Type clrType, [NotNullWhen(true)] out TypedIdMapping? mapping)
    {
        mapping = null;
        var elementType = clrType.IsArray
            ? clrType.GetElementType()
            : clrType.IsGenericType
                ? (clrType.IsInterface ? [clrType, .. clrType.GetInterfaces()] : clrType.GetInterfaces())
                    .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))?.GenericTypeArguments[0]
                : null;
        return elementType is not null && TryGet(elementType, out mapping) && Nullable.GetUnderlyingType(elementType) is null;
    }

    private static TypedIdMapping? Create(Type type)
    {
        if (!type.IsValueType || type.IsGenericTypeDefinition)
        {
            return null;
        }

        foreach (var contract in type.GetInterfaces())
        {
            if (contract.IsGenericType
                && contract.GetGenericTypeDefinition() == typeof(ITypedId<,>)
                && contract.GenericTypeArguments[0] == type
                && contract.GenericTypeArguments[1] is var valueType
                && (valueType == typeof(Guid) || valueType == typeof(long)))
            {
                return new TypedIdMapping(type, valueType);
            }
        }

        return null;
    }
}
