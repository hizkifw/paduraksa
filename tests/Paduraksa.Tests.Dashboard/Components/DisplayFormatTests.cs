using System.Globalization;

using Paduraksa.Dashboard;

namespace Paduraksa.Tests.Dashboard.Components;

public sealed class DisplayFormatTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 12, 12, 3, 20, 481, TimeSpan.Zero);

    private static DisplayFormat In(TimeSpan offset, bool use24Hour = true)
    {
        DisplayFormat format = new();
        format.Use(new DisplayPreferences(TimeZoneInfo.CreateCustomTimeZone("test", offset, "test", "test"), CultureInfo.InvariantCulture, use24Hour));
        return format;
    }

    [Fact]
    public void WithNoPreferencesTimesAreUtcAndSaySo()
    {
        DisplayFormat format = new();

        Assert.Equal("2026-11-12 12:03:20 UTC", format.Exact(Noon));
        Assert.Equal("2026-11-12 12:03:20.481 UTC", format.Stamp(Noon));
        Assert.Equal("12:03 UTC", format.Time(Noon));
        Assert.Equal("12 Nov 2026, 12:03 UTC", format.DateTime(Noon));
    }

    [Fact]
    public void TimesAreWrittenOnTheClockOfTheZoneWithItsOffset()
    {
        var format = In(TimeSpan.FromHours(7));

        Assert.Equal("2026-11-12 19:03:20 UTC+7", format.Exact(Noon));
        Assert.Equal("19:03:20", format.Clock(Noon));
        Assert.Equal("481", format.Fraction(Noon));
        Assert.Equal("Nov 12 19:03", format.Axis(Noon, "MMM d HH:mm"));
    }

    [Fact]
    public void AnOffsetWithMinutesAndANegativeOffsetAreWrittenOut()
    {
        Assert.Equal("2026-11-12 08:33:20 UTC-3:30", In(TimeSpan.FromMinutes(-210)).Exact(Noon));
        Assert.Equal("2026-11-12 17:33:20 UTC+5:30", In(TimeSpan.FromMinutes(330)).Exact(Noon));
    }

    [Fact]
    public void ALocalDayThatIsAnotherDayInUtcShowsTheLocalDate()
    {
        var late = new DateTimeOffset(2026, 11, 12, 20, 0, 0, TimeSpan.Zero);

        Assert.Equal("13 Nov 2026", In(TimeSpan.FromHours(7)).Date(late));
        Assert.Equal("12 Nov 2026", new DisplayFormat().Date(late));
    }

    [Fact]
    public void ATwelveHourClockWritesAmAndPm()
    {
        var format = In(TimeSpan.FromHours(2), use24Hour: false);

        Assert.Equal("2026-11-12 2:03:20 PM UTC+2", format.Exact(Noon));
        Assert.Equal("2:03 PM UTC+2", format.Time(Noon));
        Assert.Equal("Nov 12 2:03 PM", format.Axis(Noon, "MMM d HH:mm"));
    }

    [Theory]
    [InlineData(20, "just now")]
    [InlineData(300, "5 min ago")]
    [InlineData(3 * 3600, "3 h ago")]
    [InlineData(30 * 3600, "yesterday")]
    [InlineData(5 * 86400, "7 Nov 2026")]
    public void AgeIsWrittenAsHowLongAgoThenAsTheDate(int seconds, string expected) =>
        Assert.Equal(expected, new DisplayFormat().Ago(Noon.AddSeconds(-seconds), Noon));

    [Theory]
    [InlineData(null, "-")]
    [InlineData(0.42, "0.42 ms")]
    [InlineData(38.0, "38 ms")]
    [InlineData(412.0, "412 ms")]
    [InlineData(1234.0, "1.23 s")]
    public void DurationsAreWordedForReading(double? milliseconds, string expected) => Assert.Equal(expected, new DisplayFormat().Duration(milliseconds));

    [Theory]
    [InlineData(45, "45 s")]
    [InlineData(600, "10 min")]
    [InlineData(7200, "2 h")]
    public void SpansOfSecondsUseTheUnitThatReadsBest(int seconds, string expected) => Assert.Equal(expected, new DisplayFormat().Seconds(seconds));

    [Theory]
    [InlineData(0, "0")]
    [InlineData(1204, "1,204")]
    [InlineData(1234567, "1,234,567")]
    public void CountsHaveThousandsSeparators(long count, string expected) => Assert.Equal(expected, new DisplayFormat().Count(count));

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1500, "1.5 kB")]
    [InlineData(34_000_000, "34 MB")]
    [InlineData(2_100_000_000, "2.1 GB")]
    public void BytesUseDecimalUnits(double bytes, string expected) => Assert.Equal(expected, new DisplayFormat().Bytes(bytes));

    [Theory]
    [InlineData(950, "950")]
    [InlineData(1200, "1.2k")]
    [InlineData(34_000, "34k")]
    [InlineData(5_600_000, "5.6M")]
    public void AnAxisCountIsCompact(double value, string expected) => Assert.Equal(expected, new DisplayFormat().Compact(value));

    [Theory]
    [InlineData(0, "0/s")]
    [InlineData(0.05, "<0.1/s")]
    [InlineData(4.2, "4.2/s")]
    [InlineData(340, "340/s")]
    [InlineData(12_000, "12k/s")]
    public void RatesAreShortWithTheirUnit(double rate, string expected) => Assert.Equal(expected, new DisplayFormat().PerSecond(rate));

    [Theory]
    [InlineData(0, "0%")]
    [InlineData(0.0004, "<0.1%")]
    [InlineData(0.004, "0.4%")]
    [InlineData(0.12, "12%")]
    public void SharesAreWrittenAsPercentages(double share, string expected) => Assert.Equal(expected, new DisplayFormat().Percent(share));

    [Fact]
    public void SharesOfAWholeAreWholePercents()
    {
        DisplayFormat format = new();

        Assert.Equal("<1%", format.Share(1, 1000));
        Assert.Equal("50%", format.Share(1, 2));
        Assert.Equal("0%", format.Share(0, 10));
        Assert.Null(format.ShareOrNull(0, 0));
        Assert.Equal("81%", format.ShareOrNull(81, 100));
    }

    [Fact]
    public void ANumberKeepsAtMostTheDecimalsAsked()
    {
        DisplayFormat format = new();

        Assert.Equal("1,234.5", format.Number(1234.5, 2));
        Assert.Equal("1,235", format.Number(1234.5));
        Assert.Equal("0.33", format.Number(1 / 3.0, 2));
    }

    [Fact]
    public void TheBrowsersZoneAndLanguageAreTakenOnceAndNotWhenUnknown()
    {
        DisplayFormat format = new();
        format.Initialize("Etc/GMT-7", "en-GB");
        var zone = format.Preferences.Zone;

        format.Initialize("Etc/GMT+3", "fr-FR");

        Assert.Equal(TimeSpan.FromHours(7), zone.BaseUtcOffset);
        Assert.Same(zone, format.Preferences.Zone);

        DisplayFormat unknown = new();
        unknown.Initialize("Mars/Olympus", "not a language");
        Assert.Same(TimeZoneInfo.Utc, unknown.Preferences.Zone);
        // The build runs with invariant globalization, so a language tag can only be known or malformed, and a malformed one changes nothing.
        Assert.Equal(string.Empty, unknown.Preferences.Culture.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("../../etc/passwd")]
    [InlineData("Mars/Olympus")]
    public void ANonsenseZoneIsNoZone(string? id) => Assert.Null(DisplayFormat.FindZone(id));

    [Fact]
    public void ChoosingPreferencesRaisesChanged()
    {
        DisplayFormat format = new();
        var raised = 0;
        format.Changed += () => raised++;

        format.Use(DisplayPreferences.Default with { Use24Hour = false });

        Assert.Equal(1, raised);
        Assert.False(format.Preferences.Use24Hour);
    }
}
