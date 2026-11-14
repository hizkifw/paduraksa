using System.Globalization;
using System.Reflection;

using Paduraksa.Contracts.Config;
using Paduraksa.Server.Hosting;

namespace Paduraksa.Tests.Integration.Hosting;

/// <summary>
/// Each server setting's configuration key names a bound option of <c>Paduraksa</c>, whose default is the declaration's, and a key the
/// configuration sets is the environment's (docs/design/config.md "Server settings", precedence).
/// </summary>
public sealed class ServerSettingOptionsTests
{
    private static object? Bound(object options, string configurationKey)
    {
        object? value = options;
        foreach (var name in configurationKey.Split(':'))
        {
            var property = value!.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
                ?? throw new InvalidOperationException($"{configurationKey}: no option {name} on {value.GetType().Name}");
            value = property.GetValue(value);
        }

        return value;
    }

    private static string Text(object? value) => value switch
    {
        null => string.Empty,
        TimeSpan span => span.ToString("c", CultureInfo.InvariantCulture),
        bool flag => flag ? "true" : "false",
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()!,
    };

    [Fact]
    public void EveryKeyIsABoundOptionWhoseDefaultIsTheDeclarations()
    {
        var options = PaduraksaHost.LoadOptions(TestHost.CreateOptions(settings: new Dictionary<string, string?> { ["Paduraksa:Mcp:Enabled"] = null }));

        Assert.All(ServerSettingCatalog.All, setting => Assert.Equal(setting.DefaultText, Text(Bound(options, setting.ConfigurationKey))));
    }

    [Fact]
    public void AKeyTheConfigurationSetsIsTheEnvironmentsEvenAtItsDefault()
    {
        var options = PaduraksaHost.LoadOptions(TestHost.CreateOptions(settings: new Dictionary<string, string?>
        {
            ["Paduraksa:Metrics:Retention"] = "90.00:00:00",
            ["Paduraksa:Admin:RateLimit:PermitsPerIp"] = "7",
            ["Paduraksa:Mcp:Enabled"] = null,
        }));

        Assert.Equal("90.00:00:00", options.ServerSettingEnvironment["metricsRetention"]);
        Assert.Equal("7", options.ServerSettingEnvironment["adminRateLimitPermitsPerIp"]);
        Assert.False(options.ServerSettingEnvironment.ContainsKey("mcpEnabled"));
        Assert.False(options.ServerSettingEnvironment.ContainsKey("analyticsRetention"));
    }
}
