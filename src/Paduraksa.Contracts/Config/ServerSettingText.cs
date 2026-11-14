using System.Globalization;

namespace Paduraksa.Contracts.Config;

/// <summary>A server setting's value in words (<c>90 days</c>, <c>1 GiB</c>), for messages and the dashboard.</summary>
public static class ServerSettingText
{
    private const long Kibibyte = 1024;

    /// <summary><paramref name="text"/> (a value as the config writes it) in words; text that doesn't read is returned as it is.</summary>
    public static string Describe(ServerSettingKind kind, string text) => kind switch
    {
        ServerSettingKind.Duration when TimeSpan.TryParseExact(text, "c", CultureInfo.InvariantCulture, out var span) => Duration(span),
        ServerSettingKind.Size when long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var bytes) => Size(bytes),
        ServerSettingKind.Number when long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) =>
            number.ToString("N0", CultureInfo.InvariantCulture),
        ServerSettingKind.Boolean => text == "true" ? "on" : "off",
        ServerSettingKind.Text when text.Length == 0 => "none",
        _ => text,
    };

    /// <summary>What a value of <paramref name="kind"/> looks like, for a message about one that doesn't read.</summary>
    public static string KindWord(ServerSettingKind kind) => kind switch
    {
        ServerSettingKind.Duration => "a duration ([d.]hh:mm:ss)",
        ServerSettingKind.Size => "a size in bytes",
        ServerSettingKind.Number => "a whole number",
        ServerSettingKind.Boolean => "true or false",
        _ => "text",
    };

    /// <summary>A span in the largest unit that holds it whole (<c>90 days</c>, <c>36 hours</c>, <c>1.5 seconds</c>).</summary>
    public static string Duration(TimeSpan span)
    {
        static string Unit(double count, string unit) =>
            string.Create(CultureInfo.InvariantCulture, $"{count:0.##} {unit}{(count == 1 ? string.Empty : "s")}");

        if (span == TimeSpan.Zero)
        {
            return "0 seconds";
        }

        if (span.Ticks % TimeSpan.TicksPerDay == 0)
        {
            return span.TotalDays % 365 == 0 ? Unit(span.TotalDays / 365, "year") : Unit(span.TotalDays, "day");
        }

        if (span.Ticks % TimeSpan.TicksPerHour == 0)
        {
            return Unit(span.TotalHours, "hour");
        }

        return span.Ticks % TimeSpan.TicksPerMinute == 0 ? Unit(span.TotalMinutes, "minute") : Unit(span.TotalSeconds, "second");
    }

    /// <summary>Bytes in the largest binary unit that holds them to two decimals (<c>1 GiB</c>, <c>64 MiB</c>).</summary>
    public static string Size(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes;
        var unit = 0;
        while (Math.Abs(value) >= Kibibyte && unit < units.Length - 1)
        {
            value /= Kibibyte;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.##} {units[unit]}");
    }
}
