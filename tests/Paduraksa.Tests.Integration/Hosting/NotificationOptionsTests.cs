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
    [InlineData("Paduraksa:Notifications:MaxDeliveryAge", "00:00:30", "MaxDeliveryAge")]
    [InlineData("Paduraksa:Notifications:MaxDeliveryAge", "8.00:00:00", "MaxDeliveryAge")]
    [InlineData("Paduraksa:Notifications:HistoryRetention", "00:30:00", "HistoryRetention")]
    [InlineData("Paduraksa:Notifications:HistoryRetention", "1900.00:00:00", "HistoryRetention")]
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
        Assert.Equal(TimeSpan.FromHours(1), options.MaxDeliveryAge);
        Assert.Equal(TimeSpan.FromDays(14), options.HistoryRetention);
    }

    [Fact]
    public void TheDeliveryAgeAndTheHistoryRetentionAreBound()
    {
        var options = PaduraksaHost.LoadOptions(TestHost.CreateOptions(settings: new Dictionary<string, string?>
        {
            ["Paduraksa:Notifications:MaxDeliveryAge"] = "00:20:00",
            ["Paduraksa:Notifications:HistoryRetention"] = "30.00:00:00",
        })).Notifications.ToOptions();

        Assert.Equal(TimeSpan.FromMinutes(20), options.MaxDeliveryAge);
        Assert.Equal(TimeSpan.FromDays(30), options.HistoryRetention);
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
