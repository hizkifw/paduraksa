using Microsoft.Extensions.Logging;

using Paduraksa.Server.Configuration;
using Paduraksa.Server.Hosting;

namespace Paduraksa.Tests.Integration.Hosting;

public sealed class ConfigurationListBindingTests
{
    [Fact]
    public void AScalarListIsSplitTrimmedAndEmptied()
    {
        PaduraksaOptions options = Load(
            ("Paduraksa:Listen:AdminAllowedSources", " 192.168.1.0/24, 10.0.0.0/8 ,, "),
            ("Paduraksa:Admin:CertificateNames", "admin.example.com, paduraksa.lan"),
            ("Paduraksa:Listen:HttpProxyProtocol:TrustedSources", "192.0.2.10, 198.51.100.0/24"),
            ("Paduraksa:Listen:HttpsProxyProtocol:TrustedSources", "203.0.113.1"),
            ("Paduraksa:Acme:DnsPropagation:Resolvers", "1.1.1.1, 9.9.9.9:53"),
            ("Paduraksa:AccessLog:SensitiveQueryKeys", "pin, otp"));

        Assert.Equal(["192.168.1.0/24", "10.0.0.0/8"], options.Listen.AdminAllowedSources);
        Assert.Equal(["admin.example.com", "paduraksa.lan"], options.Admin.CertificateNames);
        Assert.Equal(["192.0.2.10", "198.51.100.0/24"], options.Listen.HttpProxyProtocol.TrustedSources);
        Assert.Equal(["203.0.113.1"], options.Listen.HttpsProxyProtocol.TrustedSources);
        Assert.Equal(["1.1.1.1", "9.9.9.9:53"], options.Acme.DnsPropagation.Resolvers);
        // The access log's redaction list: the scalar replaces the built-in list.
        Assert.Equal(["pin", "otp"], options.AccessLog.SensitiveQueryKeys);
        Assert.Equal(["pin", "otp"], options.AccessLog.ToFormatOptions().SensitiveQueryKeys);
        Assert.Empty(options.ListBindingWarnings);
    }

    [Fact]
    public void TheAccessLogDefaultsToTheWellKnownSensitiveNames()
    {
        PaduraksaOptions options = Load();

        Assert.Equal(Paduraksa.Proxy.Logging.AccessLogRing.DefaultCapacity, options.AccessLog.RingSize);
        Assert.True(options.AccessLog.LogQueryString);
        Assert.Empty(options.AccessLog.SensitiveQueryKeys);
        Assert.Equal(
            [.. Paduraksa.Proxy.Logging.AccessLogFormatOptions.DefaultSensitiveQueryKeys],
            options.AccessLog.ToFormatOptions().SensitiveQueryKeys);
        Assert.Contains("token", options.AccessLog.ToFormatOptions().SensitiveQueryKeys);
    }

    [Fact]
    public void IndexedKeysStillBind()
    {
        PaduraksaOptions options = Load(
            ("Paduraksa:Listen:AdminAllowedSources:0", "192.168.1.0/24"),
            ("Paduraksa:Listen:AdminAllowedSources:1", "10.0.0.0/8"),
            ("Paduraksa:Admin:CertificateNames:0", "admin.example.com"));

        Assert.Equal(["192.168.1.0/24", "10.0.0.0/8"], options.Listen.AdminAllowedSources);
        Assert.Equal(["admin.example.com"], options.Admin.CertificateNames);
        Assert.Empty(options.ListBindingWarnings);
    }

    [Fact]
    public void AScalarBesideIndexedKeysWinsAndSaysSo()
    {
        PaduraksaOptions options = Load(
            ("Paduraksa:Listen:AdminAllowedSources", "10.0.0.0/8"),
            ("Paduraksa:Listen:AdminAllowedSources:0", "192.168.1.0/24"));

        Assert.Equal(["10.0.0.0/8"], options.Listen.AdminAllowedSources);
        Assert.Contains(options.ListBindingWarnings, w => w.Contains("both as a scalar", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnIgnoredFormIsLoggedAtStartup()
    {
        LogCapture logs = new();
        await using PaduraksaHost host = TestHost.Create(
            settings: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Paduraksa:Listen:AdminAllowedSources"] = "10.0.0.0/8",
                ["Paduraksa:Listen:AdminAllowedSources:0"] = "192.168.1.0/24",
            },
            logs: logs);

        LogEntry entry = Assert.Single(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("both as a scalar", StringComparison.Ordinal));
        Assert.Contains("AdminAllowedSources", entry.Message, StringComparison.Ordinal);
    }

    private static PaduraksaOptions Load(params (string Key, string Value)[] settings)
    {
        Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string value) in settings)
        {
            values[key] = value;
        }

        return PaduraksaHost.LoadOptions(TestHost.CreateOptions(settings: values));
    }
}
