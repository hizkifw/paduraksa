using System.Diagnostics.CodeAnalysis;

namespace Paduraksa.TypedIds;

/// <summary>Contract implemented by every generated typed ID.</summary>
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

    /// <summary>How values are generated and stored.</summary>
    static abstract IdKind Kind { get; }

    /// <summary>Exact length of the string form in chars (and UTF-8 bytes): prefix + <c>_</c> + 22 or 11.</summary>
    static abstract int FormattedLength { get; }

    /// <summary>The all-zero ID (<c>default</c>).</summary>
    static abstract TSelf Empty { get; }

    /// <summary>Whether this is <see cref="Empty"/>.</summary>
    bool IsEmpty { get; }

    /// <summary>Creates a new ID according to <see cref="Kind"/>.</summary>
    [SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "C#-only codebase; New() is the documented typed-ID factory name.")]
    static abstract TSelf New();

    /// <summary>Parses the string form (<c>prefix_</c> plus base62). Throws <see cref="FormatException"/> when it is not one.</summary>
    static abstract TSelf Parse(ReadOnlySpan<char> s);

    /// <summary>Parses the string form without throwing.</summary>
    static abstract bool TryParse(ReadOnlySpan<char> s, out TSelf result);

    /// <summary>Parses the string form from UTF-8 bytes. Throws <see cref="FormatException"/> when it is not one.</summary>
    static abstract TSelf Parse(ReadOnlySpan<byte> utf8Text);

    /// <summary>Parses the string form from UTF-8 bytes without throwing.</summary>
    static abstract bool TryParse(ReadOnlySpan<byte> utf8Text, out TSelf result);
}

/// <summary>Typed ID with access to its underlying value (<see cref="Guid"/> or <see cref="long"/>).</summary>
/// <typeparam name="TSelf">The ID type itself.</typeparam>
/// <typeparam name="TValue">The underlying value type.</typeparam>
public interface ITypedId<TSelf, TValue> : ITypedId<TSelf>
    where TSelf : struct, ITypedId<TSelf, TValue>
    where TValue : struct, IEquatable<TValue>
{
    /// <summary>The underlying value.</summary>
    TValue Value { get; }

    /// <summary>Wraps an existing value (e.g. read from storage). No validation beyond the type.</summary>
    static abstract TSelf FromValue(TValue value);
}
