namespace Paduraksa.Dashboard.SettingsArea;

/// <summary>The units a time span is typed in, and the split of a span into a number and the largest unit that holds it whole.</summary>
public static class DurationText
{
    public static IReadOnlyList<string> Units { get; } = ["seconds", "minutes", "hours", "days"];

    public static TimeSpan UnitSpan(string unit) => unit switch
    {
        "days" => TimeSpan.FromDays(1),
        "hours" => TimeSpan.FromHours(1),
        "minutes" => TimeSpan.FromMinutes(1),
        _ => TimeSpan.FromSeconds(1),
    };

    public static (double Number, string Unit) Split(TimeSpan span)
    {
        foreach (var unit in (string[])["days", "hours", "minutes"])
        {
            var size = UnitSpan(unit);
            if (span > TimeSpan.Zero && span.Ticks % size.Ticks == 0)
            {
                return (span.Ticks / size.Ticks, unit);
            }
        }

        return (span.TotalSeconds, "seconds");
    }

    /// <summary>The span of <paramref name="number"/> <paramref name="unit"/>, rounded to the millisecond; null when it is past what a span holds.</summary>
    public static TimeSpan? ToSpan(double number, string unit)
    {
        var milliseconds = Math.Round(number * UnitSpan(unit).TotalMilliseconds);
        return double.IsFinite(milliseconds) && Math.Abs(milliseconds) < TimeSpan.MaxValue.TotalMilliseconds / 2 ? TimeSpan.FromMilliseconds(milliseconds) : null;
    }
}
