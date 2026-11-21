using Microsoft.Extensions.Options;

using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Hosting;

namespace Paduraksa.Tests.Integration.Proxy;

/// <summary>The honeypot's server option (<c>Paduraksa:Honeypot:MaxConcurrent</c>): sixteen by default, bound from the configuration, and validated before anything binds.</summary>
public sealed class HoneypotOptionsTests
{
    private static Paduraksa.Server.Configuration.PaduraksaOptions Load(params (string Key, string Value)[] settings) =>
        PaduraksaHost.LoadOptions(TestHost.CreateOptions(settings: settings.ToDictionary(s => s.Key, s => (string?)s.Value)));

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1025")]
    public void ACapOutOfRangeIsRefusedAtStartup(string value)
    {
        var exception = Assert.Throws<OptionsValidationException>(() => Load(("Paduraksa:Honeypot:MaxConcurrent", value)));

        Assert.Contains("Honeypot:MaxConcurrent", string.Join(" ", exception.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void TheCapIsSixteenByDefaultAndBound()
    {
        Assert.Equal(16, new Paduraksa.Server.Configuration.PaduraksaHoneypotOptions().MaxConcurrent);
        Assert.Equal(16, Load().Honeypot.MaxConcurrent);
        Assert.Equal(1024, Load(("Paduraksa:Honeypot:MaxConcurrent", "1024")).Honeypot.MaxConcurrent);
        Assert.Equal(1, Load(("Paduraksa:Honeypot:MaxConcurrent", "1")).Honeypot.MaxConcurrent);
    }
}
