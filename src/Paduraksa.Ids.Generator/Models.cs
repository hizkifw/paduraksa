using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Paduraksa.Ids.Generator;

// Pipeline models must be value-equatable (no symbols or syntax nodes) so incremental caching works. Locations
// change on every edit above a declaration, so only the diagnostic branches carry them; TypedIdModel (which drives
// code emission) is location-free and stays cached.

// Mirrors Paduraksa.Core.Ids.IdKind (the generator can't reference Core); a unit test keeps them in sync.
internal enum IdKindValue
{
    UuidV7 = 0,
    UuidV4 = 1,
    Int64Ordered = 2,
}

/// <param name="Namespace">Escaped C# namespace (e.g. <c>Bar.@event</c>), or null for the global namespace.</param>
/// <param name="Name">Escaped C# identifier (e.g. <c>@class</c>).</param>
/// <param name="MetadataName">Unescaped full metadata name (e.g. <c>Bar.event.class</c>), for hint names and messages.</param>
/// <param name="DisplayName">Unescaped simple type name, for messages.</param>
internal sealed record TypedIdModel(string? Namespace, string Name, string MetadataName, string DisplayName, string Prefix, IdKindValue Kind);

internal sealed record Candidate(TypedIdModel? Model, LocationInfo? Location, EquatableArray<DiagnosticInfo> Diagnostics);

internal sealed record PrefixUse(string Prefix, string FullName, LocationInfo? Location);

internal sealed record LocationInfo(string FilePath, TextSpan TextSpan, LinePositionSpan LineSpan)
{
    public Location ToLocation() => Location.Create(FilePath, TextSpan, LineSpan);

    public static LocationInfo? From(Location? location) =>
        location?.SourceTree is null ? null : new LocationInfo(location.SourceTree.FilePath, location.SourceSpan, location.GetLineSpan().Span);
}

internal sealed record DiagnosticInfo(DiagnosticDescriptor Descriptor, LocationInfo? Location, string Arg0, string Arg1 = "")
{
    public Diagnostic ToDiagnostic() => Diagnostic.Create(Descriptor, Location?.ToLocation(), Arg0, Arg1);
}

/// <summary>Immutable array with structural equality, for use in incremental pipeline models.</summary>
internal readonly struct EquatableArray<T>(T[]? items) : IEquatable<EquatableArray<T>>, IEnumerable<T>
    where T : IEquatable<T>
{
    private readonly T[]? _items = items;

    public int Length => _items?.Length ?? 0;

    public bool Equals(EquatableArray<T> other) =>
        (_items ?? []).SequenceEqual(other._items ?? []);

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        int hash = 17;
        foreach (T item in _items ?? [])
        {
            hash = unchecked((hash * 31) + (item?.GetHashCode() ?? 0));
        }

        return hash;
    }

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)(_items ?? [])).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
