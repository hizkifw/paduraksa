using System.Globalization;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Paduraksa.Storage;

/// <summary>
/// Stores a <see cref="DateTimeOffset"/> as fixed-width ISO 8601 UTC text with all seven fractional digits,
/// e.g. <c>2026-11-07T12:34:56.7890123Z</c>. Applied to every <see cref="DateTimeOffset"/> property by convention.
/// </summary>
/// <remarks>
/// <para>
/// EF's SQLite default is text with the original offset (<c>2026-11-07 20:34:56.789+08:00</c>), which SQLite compares
/// as a plain string, so EF refuses to translate <c>ORDER BY</c> and comparisons on it. This form fixes that: it is
/// always UTC and always 28 characters, so string order is time order and <c>&lt;</c>, <c>&gt;</c>, <c>ORDER BY</c> and
/// <c>MIN</c>/<c>MAX</c> work in SQL. It is lossless to the tick (100 ns, all of <see cref="DateTimeOffset"/>'s
/// precision), stays readable in the <c>sqlite3</c> shell, and SQLite's date functions accept it.
/// </para>
/// <para>
/// The offset is not kept: values are normalised to UTC on write and read back with offset zero. Equality
/// (<see cref="DateTimeOffset.Equals(DateTimeOffset)"/> compares instants) is unaffected. Years outside 0001-9999
/// can't occur in a <see cref="DateTimeOffset"/>, so the width really is fixed.
/// </para>
/// </remarks>
public sealed class UtcTimestampConverter()
    : ValueConverter<DateTimeOffset, string>(
        value => UtcTimestamp.Format(value),
        text => UtcTimestamp.Parse(text),
        new ConverterMappingHints(size: UtcTimestamp.Length, unicode: false));

/// <summary>The text form used by <see cref="UtcTimestampConverter"/>.</summary>
internal static class UtcTimestamp
{
    public const string Pattern = "yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fffffff'Z'";

    public const int Length = 28;

    public static string Format(DateTimeOffset value) =>
        value.UtcDateTime.ToString(Pattern, CultureInfo.InvariantCulture);

    public static DateTimeOffset Parse(string text) =>
        new(DateTime.ParseExact(text, Pattern, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal), TimeSpan.Zero);
}
