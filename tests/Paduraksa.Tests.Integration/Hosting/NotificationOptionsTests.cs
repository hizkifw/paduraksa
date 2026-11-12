using Microsoft.Extensions.Options;
using Paduraksa.Server.Hosting;

namespace Paduraksa.Tests.Integration.Hosting;

/// <summary>The notification settings (<c>Paduraksa:Notifications</c>): validated before anything binds.</summary>
public sealed class NotificationOptionsTests
{
    [Theory]
    [InlineData("Paduraksa:Notifications:AdminUrl", "ftp://admin.example.com", "AdminUrl")]
    [InlineData("Paduraksa:Notifications:AdminUrl", "admin.example.com", "AdminUrl")]
    [InlineData("Paduraksa:Notifications:AdminUrl", "https://user:pw@admin.example.com", "AdminUrl")]
    [InlineData("Paduraksa:Notifications:AdminUrl", "https://admin.example.com/?x=1", "AdminUrl")]
    [InlineData("Paduraksa:Notifications:HealthHold", "00:00:00", "HealthHold")]
    [InlineData("Paduraksa:Notifications:HealthHold", "01:00:00", "HealthHold")]
    [InlineData("Paduraksa:Notifications:CoalesceWindow", "00:05:00", "CoalesceWindow")]
    public void ABadSettingIsRefusedAtStartup(string key, string value, string named)
    {
        var exception = Assert.Throws<OptionsValidationException>(() =>
            PaduraksaHost.LoadOptions(TestHost.CreateOptions(settings: new Dictionary<string, string?> { [key] = value })));

        Assert.Contains(named, string.Join(" ", exception.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void NoLinkIsMadeWithoutAnAdminUrlAndTheDebounceIsTenSecondsByDefault()
    {
        var options = PaduraksaHost.LoadOptions(TestHost.CreateOptions()).Notifications.ToOptions();

        Assert.Null(options.AdminUrl);
        Assert.Equal(TimeSpan.FromSeconds(10), options.HealthHold);
        Assert.Equal(TimeSpan.FromSeconds(3), options.CoalesceWindow);
    }

    [Fact]
    public void TheAdminUrlIsBoundAndLinksJoinOntoIt()
    {
        var options = PaduraksaHost.LoadOptions(TestHost.CreateOptions(settings: new Dictionary<string, string?>
        {
            ["Paduraksa:Notifications:AdminUrl"] = "https://admin.example.com:9443",
            ["Paduraksa:Notifications:HealthHold"] = "00:00:30",
        })).Notifications.ToOptions();

        Assert.Equal("https://admin.example.com:9443/upstreams", new Uri(options.AdminUrl!, "upstreams").ToString());
        Assert.Equal(TimeSpan.FromSeconds(30), options.HealthHold);
    }
}
