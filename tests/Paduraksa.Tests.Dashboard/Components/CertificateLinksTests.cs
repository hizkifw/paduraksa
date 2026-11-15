using Bunit;

using Paduraksa.Contracts.Certificates;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard;
using Paduraksa.Dashboard.Certificates;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>Where a certificate is used, as links: each host to its drawer, the default certificate to the global defaults, and a host that is gone stays plain.</summary>
public sealed class CertificateLinksTests
{
    [Fact]
    public void AHostUse_IsALinkChipToTheHostsDrawer()
    {
        using DashboardTestContext context = new();
        var host = HostId.New();

        var chip = context.Render<CertificateUseLink>(p => p.Add(x => x.Use, new CertificateUse(CertificateUseKind.Specific, host, "app.home.test")));

        var link = chip.Find("a.pdk-tag.pdk-tag-link");
        Assert.Equal(DashboardRoutes.Host(host), link.GetAttribute("href"));
        Assert.Equal("app.home.test", link.TextContent);
    }

    [Fact]
    public void TheDefaultCertificate_LinksToTheGlobalDefaults_AndAUseWithoutAHostIsPlain()
    {
        using DashboardTestContext context = new();

        var defaults = context.Render<CertificateUseLink>(p => p.Add(x => x.Use, new CertificateUse(CertificateUseKind.Default)));
        var gone = context.Render<CertificateUseLink>(p => p.Add(x => x.Use, new CertificateUse(CertificateUseKind.Auto, null, "old.home.test")));

        Assert.Equal(SettingsRoutes.Defaults, defaults.Find("a.pdk-tag-link").GetAttribute("href"));
        Assert.Empty(gone.FindAll("a"));
        Assert.Equal("old.home.test", gone.Find(".pdk-tag").TextContent);
    }
}
