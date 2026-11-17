using System.Globalization;

namespace Paduraksa.Dashboard.Components;

/// <summary>Small pieces of wording every page shares.</summary>
public static class Wording
{
    /// <summary>"1 certificate", "3 certificates". A <paramref name="format"/> groups the digits of a large count ("1,204 requests").</summary>
    public static string Plural(long count, string one, string many, DisplayFormat? format = null) =>
        string.Create(CultureInfo.InvariantCulture, $"{(format is null ? count.ToString(CultureInfo.InvariantCulture) : format.Count(count))} {(count == 1 ? one : many)}");

    /// <summary>"a", "a and b", "a, b and c". Nothing for an empty list.</summary>
    public static string List(IReadOnlyList<string> parts) => parts.Count switch
    {
        0 => string.Empty,
        1 => parts[0],
        _ => $"{string.Join(", ", parts.Take(parts.Count - 1))} and {parts[^1]}",
    };
}
