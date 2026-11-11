using Microsoft.Extensions.Options;

using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Hosting;

namespace Paduraksa.Tests.Integration.Metrics;

/// <summary>The metrics settings (<c>Paduraksa:Metrics</c>): validated before anything binds, like the log settings.</summary>
public sealed class MetricsOptionsTests
{
    [Theory]
    [InlineData("Paduraksa:Metrics:Retention", "00:30:00", "Retention")]
    [InlineData("Paduraksa:Metrics:Retention", "00:00:00", "Retention")]
    [InlineData("Paduraksa:Metrics:Retention", "2000.00:00:00", "Retention")]
    [InlineData("Paduraksa:Metrics:WriteInterval", "00:00:00", "WriteInterval")]
    [InlineData("Paduraksa:Metrics:WriteInterval", "00:00:00.5", "WriteInterval")]
    [InlineData("Paduraksa:Metrics:WriteInterval", "00:11:00", "WriteInterval")]
    public void ABadMetricsSettingIsRefusedAtStartup(string key, string value, string named)
    {
        var exception = Assert.Throws<OptionsValidationException>(() =>
            PaduraksaHost.LoadOptions(TestHost.CreateOptions(settings: new Dictionary<string, string?> { [key] = value })));

        Assert.Contains(named, string.Join(" ", exception.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void TheMinutesAreKeptTwoWeeksAndWrittenEveryFifteenSecondsByDefault()
    {
        var options = PaduraksaHost.LoadOptions(TestHost.CreateOptions());

        Assert.Equal(TimeSpan.FromDays(14), options.Metrics.Retention);
        Assert.Equal(TimeSpan.FromSeconds(15), options.Metrics.WriteInterval);
    }

    [Fact]
    public void TheSettingsAreBoundFromConfiguration()
    {
        var options = PaduraksaHost.LoadOptions(TestHost.CreateOptions(settings: new Dictionary<string, string?>
        {
            ["Paduraksa:Metrics:Retention"] = "90.00:00:00",
            ["Paduraksa:Metrics:WriteInterval"] = "00:01:00",
        }));

        Assert.Equal(TimeSpan.FromDays(90), options.Metrics.Retention);
        Assert.Equal(TimeSpan.FromMinutes(1), options.Metrics.WriteInterval);
        var rollup = options.Metrics.ToOptions();
        Assert.Equal(TimeSpan.FromDays(90), rollup.Retention);
        Assert.Equal(TimeSpan.FromMinutes(1), rollup.WriteInterval);
    }
}
