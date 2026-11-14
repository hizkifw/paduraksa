using Microsoft.Extensions.Options;

using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Hosting;

namespace Paduraksa.Tests.Integration.Metrics;

/// <summary>The analytics settings (<c>Paduraksa:Analytics</c>): validated before anything binds, like the metrics and log settings.</summary>
public sealed class AnalyticsOptionsTests
{
    [Theory]
    [InlineData("Paduraksa:Analytics:Retention", "00:30:00", "Retention")]
    [InlineData("Paduraksa:Analytics:Retention", "00:00:00", "Retention")]
    [InlineData("Paduraksa:Analytics:Retention", "2000.00:00:00", "Retention")]
    [InlineData("Paduraksa:Analytics:WriteInterval", "00:00:00", "WriteInterval")]
    [InlineData("Paduraksa:Analytics:WriteInterval", "00:00:00.5", "WriteInterval")]
    [InlineData("Paduraksa:Analytics:WriteInterval", "00:11:00", "WriteInterval")]
    public void ABadAnalyticsSettingIsRefusedAtStartup(string key, string value, string named)
    {
        var exception = Assert.Throws<OptionsValidationException>(() =>
            PaduraksaHost.LoadOptions(TestHost.CreateOptions(settings: new Dictionary<string, string?> { [key] = value })));

        Assert.Contains(named, string.Join(" ", exception.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void NinetyDaysAreKeptAndBucketsAreWrittenEveryFifteenSecondsByDefault()
    {
        var options = PaduraksaHost.LoadOptions(TestHost.CreateOptions());

        Assert.Equal(TimeSpan.FromDays(90), options.Analytics.Retention);
        Assert.Equal(TimeSpan.FromSeconds(15), options.Analytics.WriteInterval);
    }

    [Fact]
    public void TheSettingsAreBoundFromConfiguration()
    {
        var options = PaduraksaHost.LoadOptions(TestHost.CreateOptions(settings: new Dictionary<string, string?>
        {
            ["Paduraksa:Analytics:Retention"] = "7.00:00:00",
            ["Paduraksa:Analytics:WriteInterval"] = "00:01:00",
        }));

        var rollup = options.Analytics.ToOptions();
        Assert.Equal(TimeSpan.FromDays(7), rollup.Retention);
        Assert.Equal(TimeSpan.FromMinutes(1), rollup.WriteInterval);
    }
}
