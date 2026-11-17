using Bunit;

using Paduraksa.Dashboard.Components;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>One meter draws all three password hints (setup, My account, backup); the callers score.</summary>
public sealed class StrengthMeterTests
{
    [Theory]
    [InlineData(0, "Too short: 3 more characters")]
    [InlineData(2, "Fair")]
    [InlineData(4, "Strong")]
    public async Task TheLevelAndTheWordAreWhatTheCallerSays(int level, string label)
    {
        await using DashboardTestContext context = new();

        var meter = context.Render<StrengthMeter>(p => p.Add(c => c.Level, level).Add(c => c.Label, label));

        Assert.Equal(level.ToString(System.Globalization.CultureInfo.InvariantCulture), meter.Find("[data-strength]").GetAttribute("data-strength"));
        Assert.Equal(label, meter.Find("[data-strength-label]").TextContent);
        Assert.Equal(4, meter.FindAll(".pdk-strength-bar > span").Count);
    }

    [Fact]
    public async Task NothingTyped_ShowsAnEmptyBarAndNoWord()
    {
        await using DashboardTestContext context = new();

        var meter = context.Render<StrengthMeter>(p => p.Add(c => c.Level, -1).Add(c => c.Label, "Weak"));

        Assert.Equal("-1", meter.Find("[data-strength]").GetAttribute("data-strength"));
        Assert.Equal(string.Empty, meter.Find("[data-strength-label]").TextContent);
    }

    [Fact]
    public async Task TheBarIsHiddenFromScreenReaders_AndTheWordIsAnnouncedAsItChanges()
    {
        await using DashboardTestContext context = new();

        var meter = context.Render<StrengthMeter>(p => p.Add(c => c.Level, 3).Add(c => c.Label, "Good"));

        Assert.Equal("polite", meter.Find("[data-strength]").GetAttribute("aria-live"));
        Assert.Equal("true", meter.Find(".pdk-strength-bar").GetAttribute("aria-hidden"));
    }
}
