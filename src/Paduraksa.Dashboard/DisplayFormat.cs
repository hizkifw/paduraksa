using System.Globalization;

using Paduraksa.Contracts.Config;

namespace Paduraksa.Dashboard;

/// <summary>
/// How one person reads times and numbers: the time zone to show them in, the culture that spells them, and whether the clock has 24 hours.
/// Today the browser supplies the zone and culture (<see cref="DisplayFormat.Initialize"/>); a settings page can supply all three later
/// through <see cref="DisplayFormat.Use"/>.
/// </summary>
/// <param name="Zone">Where times are shown; UTC until the browser says otherwise.</param>
/// <param name="Culture">Spells the numbers and the month names.</param>
/// <param name="Use24Hour">A 24 hour clock; false writes "2:03 PM".</param>
public sealed record DisplayPreferences(TimeZoneInfo Zone, CultureInfo Culture, bool Use24Hour = true)
{
    /// <summary>UTC, invariant culture, 24 hours: what the dashboard showed before there were preferences, and what a browser that says nothing gets.</summary>
    public static DisplayPreferences Default { get; } = new(TimeZoneInfo.Utc, CultureInfo.InvariantCulture);
}

/// <summary>
/// Every time, date, duration and number the dashboard shows to a person goes through here, so the zone, culture and clock they chose (or
/// their browser implies) apply everywhere and nothing is spelled twice. Exported data, API responses, log files and machine values
/// (IDs, fingerprints, what a form field holds) stay invariant and don't come through here.
/// </summary>
public sealed class DisplayFormat
{
    /// <summary>The cookie the page's script keeps the browser's IANA zone in (<c>Europe/Paris</c>), so the server renders local times from the first byte. Not a secret.</summary>
    public const string ZoneCookie = "paduraksa-tz";

    /// <summary>The cookie the page's script keeps the browser's language tag in (<c>en-GB</c>).</summary>
    public const string LocaleCookie = "paduraksa-locale";

    /// <summary>UTC and invariant, for code with no circuit to ask (the old static helpers delegate to it); it is never changed.</summary>
    public static DisplayFormat Invariant { get; } = new();

    private bool _initialized;

    /// <summary>Who is reading: their zone, culture and clock.</summary>
    public DisplayPreferences Preferences { get; private set; } = DisplayPreferences.Default;

    private CultureInfo Culture => Preferences.Culture;

    /// <summary>Raised after the preferences changed, so a page can draw again.</summary>
    public event Action? Changed;

    /// <summary>Takes what the request's cookies describe, once: the circuit keeps its own afterwards. A zone or language this server doesn't know is left as the default.</summary>
    public void Initialize(string? zoneId, string? locale)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        var zone = FindZone(zoneId);
        var culture = FindCulture(locale);
        Preferences = Preferences with { Zone = zone ?? Preferences.Zone, Culture = culture ?? Preferences.Culture };
    }

    /// <summary>Applies a person's own choice (a settings page's, later).</summary>
    public void Use(DisplayPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        _initialized = true;
        Preferences = preferences;
        Changed?.Invoke();
    }

    /// <summary>The zone an IANA id names; null when it is empty, malformed or unknown to this machine (a minimal image may carry no zone data).</summary>
    public static TimeZoneInfo? FindZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64)
        {
            return null;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The culture a language tag names; null when it is empty or malformed.</summary>
    public static CultureInfo? FindCulture(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag) || tag.Length > 35)
        {
            return null;
        }

        try
        {
            return CultureInfo.GetCultureInfo(tag);
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    // ---------- Times ----------

    /// <summary>The instant on the clock of the person's zone.</summary>
    public DateTimeOffset Local(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, Preferences.Zone);

    /// <summary>"UTC", "UTC+7", "UTC-3:30": the offset in force at <paramref name="at"/>, which says the same in every browser and needs no zone abbreviation table.</summary>
    public string ZoneLabel(DateTimeOffset at)
    {
        var offset = Preferences.Zone.GetUtcOffset(at);
        if (offset == TimeSpan.Zero)
        {
            return "UTC";
        }

        var sign = offset < TimeSpan.Zero ? '-' : '+';
        offset = offset.Duration();
        return offset.Minutes == 0
            ? string.Create(CultureInfo.InvariantCulture, $"UTC{sign}{offset.Hours}")
            : string.Create(CultureInfo.InvariantCulture, $"UTC{sign}{offset.Hours}:{offset.Minutes:00}");
    }

    /// <summary>A timestamp in full with its zone: "2026-11-12 21:03:20 UTC+7".</summary>
    public string Exact(DateTimeOffset at) => $"{Local(at).ToString(Clockwise("yyyy-MM-dd HH:mm:ss"), Culture)} {ZoneLabel(at)}";

    /// <summary>A timestamp to the millisecond with its zone, for a request: "2026-11-12 21:03:20.481 UTC+7".</summary>
    public string Stamp(DateTimeOffset at) => $"{Local(at).ToString(Clockwise("yyyy-MM-dd HH:mm:ss.fff"), Culture)} {ZoneLabel(at)}";

    /// <summary>The time of day with its zone: "21:03 UTC+7".</summary>
    public string Time(DateTimeOffset at) => $"{Local(at).ToString(Clockwise("HH:mm"), Culture)} {ZoneLabel(at)}";

    /// <summary>The time of day to the second: "21:03:20".</summary>
    public string Clock(DateTimeOffset at) => Local(at).ToString(Clockwise("HH:mm:ss"), Culture);

    /// <summary>The milliseconds of the instant, three digits: "481".</summary>
    public string Fraction(DateTimeOffset at) => Local(at).ToString("fff", CultureInfo.InvariantCulture);

    /// <summary>A date: "12 Nov 2026".</summary>
    public string Date(DateTimeOffset at) => Local(at).ToString("d MMM yyyy", Culture);

    /// <summary>A date and the time of day with its zone: "12 Nov 2026, 21:03 UTC+7".</summary>
    public string DateTime(DateTimeOffset at) => $"{Local(at).ToString(Clockwise("d MMM yyyy, HH:mm"), Culture)} {ZoneLabel(at)}";

    /// <summary>The instant in a .NET format of the caller's (a chart's axis label), on the person's clock.</summary>
    public string Axis(DateTimeOffset at, string format) => Local(at).ToString(Clockwise(format), Culture);

    /// <summary>"just now", "5 min ago", "3 h ago", "yesterday", else the date.</summary>
    public string Ago(DateTimeOffset at, DateTimeOffset now)
    {
        var age = now - at;
        if (age < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)age.TotalMinutes} min ago");
        }

        if (age < TimeSpan.FromDays(1))
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)age.TotalHours} h ago");
        }

        return age < TimeSpan.FromDays(2) ? "yesterday" : Date(at);
    }

    private static readonly string[] ClockFormats = ["HH:mm:ss.fff", "HH:mm:ss", "HH:mm"];

    // A 12 hour clock writes the hour as h and ends the time with the AM or PM: "HH:mm" becomes "h:mm tt".
    private string Clockwise(string format)
    {
        if (Preferences.Use24Hour)
        {
            return format;
        }

        foreach (var clock in ClockFormats)
        {
            if (format.Contains(clock, StringComparison.Ordinal))
            {
                return format.Replace(clock, clock.Replace("HH", "h", StringComparison.Ordinal) + " tt", StringComparison.Ordinal);
            }
        }

        return format;
    }

    // ---------- Durations ----------

    /// <summary>Milliseconds the way a person reads them: "0.42 ms", "38 ms", "1.2 s"; "-" for no time.</summary>
    public string Duration(double? milliseconds) => milliseconds switch
    {
        null => "-",
        < 1 => milliseconds.Value.ToString("0.##", Culture) + " ms",
        < 100 => milliseconds.Value.ToString("0.#", Culture) + " ms",
        < 1000 => milliseconds.Value.ToString("0", Culture) + " ms",
        _ => (milliseconds.Value / 1000).ToString("0.##", Culture) + " s",
    };

    /// <summary>The smallest step <see cref="Duration"/> tells apart at this size, in milliseconds: what a value is rounded to when it is shown.</summary>
    public static double DurationStep(double milliseconds) => milliseconds switch
    {
        < 1 => 0.01,
        < 100 => 0.1,
        < 1000 => 1,
        _ => 10,
    };

    /// <summary>A latency for a chart or a tile: <see cref="Duration"/>, and "-" when nothing was timed.</summary>
    public string Latency(double milliseconds) => milliseconds <= 0 ? "-" : Duration(milliseconds);

    /// <summary>A stretch of seconds: "45 s", "12.5 min", "3.2 h".</summary>
    public string Seconds(int seconds) => seconds switch
    {
        < 90 => seconds.ToString(Culture) + " s",
        < 5_400 => (seconds / 60.0).ToString("0.#", Culture) + " min",
        _ => (seconds / 3600.0).ToString("0.#", Culture) + " h",
    };

    /// <summary>A span in prose, in the largest unit that holds it whole: "90 days", "15 minutes", "1 hour", "1.5 seconds".</summary>
    public string Span(TimeSpan span)
    {
        string Unit(double count, string unit) => count.ToString("#,0.##", Culture) + " " + unit + (count == 1 ? string.Empty : "s");

        if (span == TimeSpan.Zero)
        {
            return "0 seconds";
        }

        if (span.Ticks % TimeSpan.TicksPerDay == 0)
        {
            return Unit(span.TotalDays, "day");
        }

        if (span.Ticks % TimeSpan.TicksPerHour == 0)
        {
            return Unit(span.TotalHours, "hour");
        }

        return span.Ticks % TimeSpan.TicksPerMinute == 0 ? Unit(span.TotalMinutes, "minute") : Unit(span.TotalSeconds, "second");
    }

    /// <summary>
    /// A server setting's value (the config's text form, <see cref="ServerSettingDefinition.TextOf"/>) the way its declaration's kind reads: a
    /// span in words, a size in binary units, a count grouped, a switch as on or off, an empty text as none.
    /// </summary>
    public string ServerSetting(ServerSettingDefinition setting, string text)
    {
        ArgumentNullException.ThrowIfNull(setting);
        ArgumentNullException.ThrowIfNull(text);
        return setting.Kind switch
        {
            ServerSettingKind.Duration when TimeSpan.TryParseExact(text, "c", CultureInfo.InvariantCulture, out var span) => Span(span),
            ServerSettingKind.Size when long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var bytes) => FileSize(bytes),
            ServerSettingKind.Number when long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) => Count(number),
            ServerSettingKind.Boolean => text == "true" ? "on" : "off",
            ServerSettingKind.Text when text.Length == 0 => "none",
            _ => text,
        };
    }

    // ---------- Numbers ----------

    /// <summary>A count with thousands separators: "1,204".</summary>
    public string Count(long value) => value.ToString("N0", Culture);

    /// <summary>"950", "1.2k", "34k", "5.6M": for an axis, where a full count would only crowd it.</summary>
    public string Compact(double value) => value switch
    {
        < 1_000 => value.ToString("0", Culture),
        < 10_000 => (value / 1_000).ToString("0.0", Culture) + "k",
        < 1_000_000 => (value / 1_000).ToString("0", Culture) + "k",
        < 10_000_000 => (value / 1_000_000).ToString("0.0", Culture) + "M",
        < 1_000_000_000 => (value / 1_000_000).ToString("0", Culture) + "M",
        _ => (value / 1_000_000_000).ToString("0.0", Culture) + "G",
    };

    /// <summary>A file's size the way the dashboard types sizes into settings (binary units): "0 B", "512 B", "1.5 KiB", "20 MiB".</summary>
    public string FileSize(long bytes) => bytes switch
    {
        < 1024 => bytes.ToString("0", Culture) + " B",
        < 1024 * 1024 => (bytes / 1024.0).ToString("0.#", Culture) + " KiB",
        < 1024L * 1024 * 1024 => (bytes / (1024.0 * 1024)).ToString("0.#", Culture) + " MiB",
        _ => (bytes / (1024.0 * 1024 * 1024)).ToString("0.#", Culture) + " GiB",
    };

    /// <summary>"0 B", "512 B", "1.5 kB", "34 MB", "2.1 GB": decimal units, as a network is measured.</summary>
    public string Bytes(double bytes) => bytes switch
    {
        <= 0 => "0 B",
        < 1_000 => bytes.ToString("0", Culture) + " B",
        < 10_000 => (bytes / 1_000).ToString("0.0", Culture) + " kB",
        < 1_000_000 => (bytes / 1_000).ToString("0", Culture) + " kB",
        < 10_000_000 => (bytes / 1_000_000).ToString("0.0", Culture) + " MB",
        < 1_000_000_000 => (bytes / 1_000_000).ToString("0", Culture) + " MB",
        < 10_000_000_000 => (bytes / 1_000_000_000).ToString("0.0", Culture) + " GB",
        < 1_000_000_000_000 => (bytes / 1_000_000_000).ToString("0", Culture) + " GB",
        _ => (bytes / 1_000_000_000_000).ToString("0.0", Culture) + " TB",
    };

    /// <summary>"0/s", "&lt;0.1/s", "4.2/s", "340/s", "12k/s".</summary>
    public string PerSecond(double rate) => rate switch
    {
        <= 0 => "0/s",
        < 0.1 => "<0.1/s",
        < 10 => rate.ToString("0.0", Culture) + "/s",
        < 1000 => rate.ToString("0", Culture) + "/s",
        < 10_000 => (rate / 1000).ToString("0.0", Culture) + "k/s",
        _ => (rate / 1000).ToString("0", Culture) + "k/s",
    };

    /// <summary>A rate that may not exist yet: "0.4/s", "12/s", "0/s"; blank before there is one, so a tile keeps its height.</summary>
    public string RateText(double? rate) => rate switch
    {
        null => string.Empty,
        0 => "0/s",
        < 0.1 => "<0.1/s",
        < 10 => rate.Value.ToString("0.#", Culture) + "/s",
        { } value => value.ToString("0", Culture) + "/s",
    };

    /// <summary>A share given as a fraction: "0%", "&lt;0.1%", "0.4%", "12%".</summary>
    public string Percent(double share) => share switch
    {
        <= 0 => "0%",
        < 0.001 => "<0.1%",
        < 0.1 => (share * 100).ToString("0.0", Culture) + "%",
        _ => (share * 100).ToString("0", Culture) + "%",
    };

    /// <summary>"0%", "&lt;1%", "12%": a share of a whole, whole percents.</summary>
    public string Share(long part, long whole) => whole <= 0 || part <= 0
        ? "0%"
        : part * 100.0 / whole < 1 ? "<1%" : (part * 100.0 / whole).ToString("0", Culture) + "%";

    /// <summary>"81%" of <paramref name="whole"/>; null when there is nothing to take a share of.</summary>
    public string? ShareOrNull(long part, long whole) =>
        whole <= 0 ? null : Math.Min(100, part * 100.0 / whole).ToString("0", Culture) + "%";

    /// <summary>A number with at most <paramref name="decimals"/> places, for a value the others don't cover (a limit, a size in seconds).</summary>
    public string Number(double value, int decimals = 0) => value.ToString("#,0" + (decimals > 0 ? "." + new string('#', decimals) : string.Empty), Culture);
}
