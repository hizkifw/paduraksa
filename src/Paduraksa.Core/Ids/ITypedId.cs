using System.Diagnostics.CodeAnalysis;

namespace Paduraksa.Core.Ids;

/// <summary>
/// Contract implemented by every generated typed ID. Static abstract members let generic infrastructure
/// (EF Core converters, OpenAPI schemas, YAML, JSON) work over any ID without reflection.
/// </summary>
/// <remarks>
/// <see cref="IComparable{T}"/> ordering is consistent with ordinal ordering of the string form,
/// which for time-ordered kinds is creation order.
/// </remarks>
/// <typeparam name="TSelf">The ID type itself.</typeparam>
public interface ITypedId<TSelf>
    : IEquatable<TSelf>, IComparable<TSelf>, ISpanFormattable, IUtf8SpanFormattable, ISpanParsable<TSelf>, IUtf8SpanParsable<TSelf>
    where TSelf : struct, ITypedId<TSelf>
{
    /// <summary>The prefix without the trailing underscore, e.g. <c>host</c>.</summary>
    static abstract string Prefix { get; }

    static abstract IdKind Kind { get; }

    /// <summary>Exact length of the string form in chars (and UTF-8 bytes): prefix + <c>_</c> + 22 or 11.</summary>
    static abstract int FormattedLength { get; }

    /// <summary>The all-zero ID (<c>default</c>).</summary>
    static abstract TSelf Empty { get; }

    bool IsEmpty { get; }

    /// <summary>Creates a new ID according to <see cref="Kind"/>.</summary>
    [SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "C#-only codebase; New() is the documented typed-ID factory name.")]
    static abstract TSelf New();

    static abstract TSelf Parse(ReadOnlySpan<char> s);

    static abstract bool TryParse(ReadOnlySpan<char> s, out TSelf result);

    static abstract TSelf Parse(ReadOnlySpan<byte> utf8Text);

    static abstract bool TryParse(ReadOnlySpan<byte> utf8Text, out TSelf result);
}

/// <summary>Typed ID with access to its underlying value (<see cref="Guid"/> or <see cref="long"/>).</summary>
/// <typeparam name="TSelf">The ID type itself.</typeparam>
/// <typeparam name="TValue">The underlying value type.</typeparam>
public interface ITypedId<TSelf, TValue> : ITypedId<TSelf>
    where TSelf : struct, ITypedId<TSelf, TValue>
    where TValue : struct, IEquatable<TValue>
{
    TValue Value { get; }

    /// <summary>Wraps an existing value (e.g. read from storage). No validation beyond the type.</summary>
    static abstract TSelf FromValue(TValue value);
}
