using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Pages;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>The overview's "Expiring soon": certificates that end within the horizon, only those in use when the config can be read.</summary>
public sealed class HomeSnapshotTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WithTheConfig_OnlyTheCertificatesInUseAreListed_WithTheirSourceInWords()
    {
        var used = Certificate("app.example.com", CertificateSource.LocalCa, Now.AddDays(5));
        var unused = Certificate("old.example.com", CertificateSource.Uploaded, Now.AddDays(3));
        ConfigDocument document = new(Hosts: [new HostConfig(HostId.New(), ["app.example.com"])]);

        var expiring = HomeSnapshot.ExpiringOf([used, unused], document, Now);

        var only = Assert.Single(expiring);
        Assert.Equal("app.example.com", only.Name);
        Assert.Equal("Local CA", only.Source);
    }

    [Fact]
    public void WithoutTheConfig_EveryActiveCertificateThatEndsSoonIsListed()
    {
        var soon = Certificate("a.example.com", CertificateSource.Uploaded, Now.AddDays(3));
        var sooner = Certificate("b.example.com", CertificateSource.Acme, Now.AddDays(1));
        var later = Certificate("c.example.com", CertificateSource.Acme, Now.AddDays(90));

        var expiring = HomeSnapshot.ExpiringOf([soon, sooner, later], null, Now);

        Assert.Equal(["b.example.com", "a.example.com"], expiring.Select(e => e.Name));
        Assert.Equal(["ACME", "Uploaded"], expiring.Select(e => e.Source));
    }

    [Fact]
    public void AStaleCloudflareListNeedsAttentionOnlyWhereTheEdgeIsTrusted()
    {
        CloudflareRangeStatus stale = new(CloudflareRangeSource.BuiltIn, Now.AddDays(-40), 22, 0, "Cloudflare's edge ranges are the built-in list, 40 days old.");
        CloudflareRangeStatus fine = stale with { Warning = null };
        static ConfigDocument Mode(TrustedProxyMode mode) => new(Settings: new GlobalSettings(TrustedProxies: new TrustedProxySettings(mode, mode == TrustedProxyMode.CloudflareTunnel ? ["127.0.0.1"] : [])));

        var item = Assert.Single(HomeSnapshot.AttentionOf(Mode(TrustedProxyMode.CloudflareProxy), [], [], [], Now, stale));
        Assert.Equal(Paduraksa.Dashboard.Config.Tone.Warning, item.Tone);
        Assert.Contains("40 days old", item.Detail, StringComparison.Ordinal);
        Assert.Equal(Paduraksa.Dashboard.SettingsRoutes.Defaults, item.Href);

        Assert.Empty(HomeSnapshot.AttentionOf(Mode(TrustedProxyMode.CloudflareProxy), [], [], [], Now, fine));
        Assert.Empty(HomeSnapshot.AttentionOf(Mode(TrustedProxyMode.CloudflareProxy), [], [], [], Now));
        foreach (var mode in new[] { TrustedProxyMode.Off, TrustedProxyMode.Generic, TrustedProxyMode.CloudflareTunnel })
        {
            Assert.Empty(HomeSnapshot.AttentionOf(Mode(mode), [], [], [], Now, stale));
        }
    }

    [Theory]
    [InlineData(CloudflareRangeSource.Fetched, 3, "Cloudflare ranges: updated 3 days ago (22 ranges)")]
    [InlineData(CloudflareRangeSource.Fetched, 1, "Cloudflare ranges: updated 1 day ago (22 ranges)")]
    [InlineData(CloudflareRangeSource.Fetched, 0, "Cloudflare ranges: updated today (22 ranges)")]
    [InlineData(CloudflareRangeSource.BuiltIn, 5, "Cloudflare ranges: built-in list from 4 Nov 2026 (22 ranges)")]
    public void TheRangeLineSaysHowFreshTheListIs(CloudflareRangeSource source, int days, string expected)
    {
        CloudflareRangeStatus status = new(source, Now.AddDays(-days), 22, 0, null);

        Assert.Equal(expected, Paduraksa.Dashboard.Config.CloudflareRangeText.Line(status, Now));
    }

    private static CertificateSummary Certificate(string name, CertificateSource source, DateTimeOffset notAfter) =>
        new(
            CertificateId.New(), source, CertificateStatus.Active, CertificateValidity.Valid, [name], Now.AddDays(-30), notAfter,
            $"CN={name}", "CN=Test CA", "01", new string('a', 64), "ECDSA P-256", Now.AddDays(-30), Now.AddDays(-30));
}
