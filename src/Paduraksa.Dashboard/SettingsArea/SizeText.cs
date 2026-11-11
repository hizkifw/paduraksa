using System.Globalization;

namespace Paduraksa.Dashboard.SettingsArea;

/// <summary>A size in bytes as the dashboard says it, and the units it is typed in (KiB, MiB, GiB: the config holds bytes).</summary>
public static class SizeText
{
    public const long Kibibyte = 1024;

    public const long Mebibyte = 1024 * Kibibyte;

    public const long Gibibyte = 1024 * Mebibyte;

    /// <summary>The units a size is typed in, smallest first.</summary>
    public static IReadOnlyList<string> Units { get; } = ["KiB", "MiB", "GiB"];

    /// <summary>The number of bytes in <paramref name="unit"/> (one of <see cref="Units"/>).</summary>
    public static long BytesIn(string unit) => unit switch
    {
        "GiB" => Gibibyte,
        "MiB" => Mebibyte,
        _ => Kibibyte,
    };

    /// <summary>The largest unit that holds <paramref name="bytes"/> exactly, else bytes: "100 MiB", "1536 KiB", "700 bytes".</summary>
    public static string Format(long bytes)
    {
        if (bytes > 0)
        {
            foreach (var unit in (string[])["GiB", "MiB", "KiB"])
            {
                var size = BytesIn(unit);
                if (bytes % size == 0)
                {
                    return $"{(bytes / size).ToString(CultureInfo.InvariantCulture)} {unit}";
                }
            }
        }

        return $"{bytes.ToString(CultureInfo.InvariantCulture)} bytes";
    }

    /// <summary>The unit a custom size starts in, and its number in that unit: whole when the unit holds the size exactly, else in KiB with up to three decimals.</summary>
    public static (double Number, string Unit) Split(long bytes)
    {
        if (bytes > 0)
        {
            foreach (var unit in (string[])["GiB", "MiB", "KiB"])
            {
                var size = BytesIn(unit);
                if (bytes % size == 0)
                {
                    return (bytes / size, unit);
                }
            }
        }

        return (Math.Round(bytes / (double)Kibibyte, 3), "KiB");
    }

    /// <summary>The bytes of <paramref name="number"/> in <paramref name="unit"/>, rounded to a whole byte and saturating at the largest size a long holds.</summary>
    public static long ToBytes(double number, string unit)
    {
        var bytes = Math.Round(number * BytesIn(unit), MidpointRounding.AwayFromZero);
        return bytes >= long.MaxValue ? long.MaxValue : bytes <= long.MinValue ? long.MinValue : (long)bytes;
    }

    /// <summary>A size for a field the config keeps in an <c>int</c>: saturating, so a size too big for it stays too big (the validator says so, with the real figure) instead of wrapping into a small one.</summary>
    public static int? ToInt(long? bytes) => bytes is { } size ? (int)Math.Clamp(size, int.MinValue, int.MaxValue) : null;
}

/// <summary>The sizes the request-limit fields offer by name; anything else is typed with a unit.</summary>
public static class RequestLimitPresets
{
    public static IReadOnlyList<long> Body { get; } = [SizeText.Mebibyte, 10 * SizeText.Mebibyte, 100 * SizeText.Mebibyte, SizeText.Gibibyte];

    public static IReadOnlyList<long> HeaderBytes { get; } = [8 * SizeText.Kibibyte, 16 * SizeText.Kibibyte, 32 * SizeText.Kibibyte, 64 * SizeText.Kibibyte];

    public static IReadOnlyList<long> Url { get; } = [2 * SizeText.Kibibyte, 4 * SizeText.Kibibyte, 8 * SizeText.Kibibyte, 16 * SizeText.Kibibyte];
}
