using Microsoft.Extensions.Options;

using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Hosting;

namespace Paduraksa.Tests.Integration.Mcp;

/// <summary>The MCP settings (<c>Paduraksa:Mcp</c>): off by default, and the idle time of a sandbox child is validated before anything binds.</summary>
public sealed class McpOptionsTests
{
    private static Paduraksa.Server.Configuration.PaduraksaOptions Load(params (string Key, string Value)[] settings) =>
        PaduraksaHost.LoadOptions(TestHost.CreateOptions(settings: settings.ToDictionary(s => s.Key, s => (string?)s.Value)));

    [Theory]
    [InlineData("00:00:09")]
    [InlineData("00:00:00")]
    [InlineData("1.00:00:01")]
    [InlineData("-00:01:00")]
    public void ABadIdleTimeoutIsRefusedAtStartup(string value)
    {
        var exception = Assert.Throws<OptionsValidationException>(() => Load(("Paduraksa:Mcp:IdleTimeout", value)));
        Assert.Contains("Mcp:IdleTimeout", string.Join(" ", exception.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void McpIsOffAndTheIdleTimeIsTenMinutesByDefaultAndBothAreBound()
    {
        Paduraksa.Server.Configuration.PaduraksaMcpOptions defaults = new();
        Assert.False(defaults.Enabled);
        Assert.Equal(TimeSpan.FromMinutes(10), defaults.IdleTimeout);

        var set = Load(("Paduraksa:Mcp:Enabled", "true"), ("Paduraksa:Mcp:IdleTimeout", "00:02:00"));
        Assert.True(set.Mcp.Enabled);
        Assert.Equal(TimeSpan.FromMinutes(2), set.Mcp.IdleTimeout);
    }
}
