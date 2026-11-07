using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Paduraksa.Core.Ids;

namespace Paduraksa.Storage.Ids;

/// <summary>
/// Plain generic helpers called from converter expression trees. Expression trees can't call static abstract
/// interface members (CS8927), so <c>TId.FromValue</c>/<c>TId.Parse</c> are wrapped in ordinary generic methods.
/// </summary>
internal static class TypedIdValue
{
    public const int GuidByteCount = 16;

    public static TId Wrap<TId, TValue>(TValue value)
        where TId : struct, ITypedId<TId, TValue>
        where TValue : struct, IEquatable<TValue> =>
        TId.FromValue(value);

    public static string Format<TId>(TId id)
        where TId : struct, ITypedId<TId> =>
        id.ToString(null, null);

    public static TId Parse<TId>(string value)
        where TId : struct, ITypedId<TId> =>
        TId.Parse(value.AsSpan());

    /// <summary>Big-endian (RFC 9562 network order) bytes, so BLOB order = time order = string order.</summary>
    public static byte[] ToBigEndianBytes(Guid value)
    {
        var bytes = new byte[GuidByteCount];
        value.TryWriteBytes(bytes, bigEndian: true, out _);
        return bytes;
    }

    public static Guid FromBigEndianBytes(byte[] bytes) => new(bytes, bigEndian: true);
}

/// <summary>Stores a Guid-backed typed ID (<see cref="IdKind.UuidV7"/>/<see cref="IdKind.UuidV4"/>) as a 16-byte big-endian BLOB.</summary>
/// <typeparam name="TId">The typed ID.</typeparam>
internal sealed class TypedIdToBytesConverter<TId>()
    : ValueConverter<TId, byte[]>(
        id => TypedIdValue.ToBigEndianBytes(id.Value),
        bytes => TypedIdValue.Wrap<TId, Guid>(TypedIdValue.FromBigEndianBytes(bytes)),
        new ConverterMappingHints(size: TypedIdValue.GuidByteCount))
    where TId : struct, ITypedId<TId, Guid>;

/// <summary>Stores an <see cref="IdKind.Int64Ordered"/> typed ID as its <see cref="long"/> value (SQLite <c>INTEGER</c>).</summary>
/// <typeparam name="TId">The typed ID.</typeparam>
internal sealed class TypedIdToInt64Converter<TId>()
    : ValueConverter<TId, long>(
        id => id.Value,
        value => TypedIdValue.Wrap<TId, long>(value))
    where TId : struct, ITypedId<TId, long>;

/// <summary>
/// Stores a typed ID as its canonical string (<c>prefix_base62</c>). Used inside JSON columns, where the document
/// is also what people and exports see. Fixed-width base62 keeps ordinal string order equal to ID order.
/// </summary>
/// <typeparam name="TId">The typed ID.</typeparam>
internal sealed class TypedIdToStringConverter<TId>()
    : ValueConverter<TId, string>(
        id => TypedIdValue.Format(id),
        value => TypedIdValue.Parse<TId>(value),
        new ConverterMappingHints(size: TId.FormattedLength, unicode: false))
    where TId : struct, ITypedId<TId>;

/// <summary>
/// Value comparer for typed IDs: immutable value types, so equality is <see cref="IEquatable{T}"/>, hashing is
/// <see cref="object.GetHashCode"/> and a snapshot is the value itself. Spelled out (rather than EF's
/// reflection-built default) so every typed-ID property, key and foreign key uses the same comparer.
/// </summary>
/// <typeparam name="TId">The typed ID.</typeparam>
internal sealed class TypedIdValueComparer<TId>()
    : ValueComparer<TId>(
        (left, right) => left.Equals(right),
        id => id.GetHashCode(),
        id => id)
    where TId : struct, ITypedId<TId>;
