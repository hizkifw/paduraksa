using Paduraksa.Application.Certificates;
using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

namespace Paduraksa.Tests.Unit.Certificates;

/// <summary>Which hosts use which certificate, as the dashboard lists it: the compiler's selection over the inventory's metadata.</summary>
public sealed class CertificateUsageTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SpecificAutoAndDefaultUsesAreListedPerCertificate()
    {
        CertificateSummary wildcard = Certificate("*.example.com");
        CertificateSummary exact = Certificate("example.com", "www.example.com");
        CertificateSummary unused = Certificate("other.test");
        CertificateSummary named = Certificate("named.test");
        HostConfig auto = Host("www.example.com", "app.example.com", "example.com");
        HostConfig specific = Host("named.test") with { Certificate = new CertificateBinding { Mode = CertificateBindingMode.Specific, CertificateId = named.Id } };
        ConfigDocument document = new(Settings: new GlobalSettings(DefaultCertificateId: wildcard.Id), Hosts: [auto, specific]);

        IReadOnlyDictionary<CertificateId, IReadOnlyList<CertificateUse>> uses = CertificateUsage.Of(document, [wildcard, exact, unused, named], Now);

        // An exact name beats the wildcard; the wildcard keeps the name only it covers, and is the default too.
        CertificateUse exactUse = Assert.Single(uses[exact.Id]);
        Assert.Equal((CertificateUseKind.Auto, auto.Id, "www.example.com"), (exactUse.Kind, exactUse.HostId, exactUse.HostName));
        Assert.Equal(["www.example.com", "example.com"], exactUse.ServerNames);
        Assert.Equal([CertificateUseKind.Default, CertificateUseKind.Auto], uses[wildcard.Id].Select(u => u.Kind));
        Assert.Equal(["app.example.com"], uses[wildcard.Id][1].ServerNames);
        Assert.Equal((CertificateUseKind.Specific, specific.Id), (Assert.Single(uses[named.Id]).Kind, uses[named.Id][0].HostId));
        Assert.False(uses.ContainsKey(unused.Id));
    }

    [Fact]
    public void ExpiredPendingAndDisabledHostsChooseNothing()
    {
        CertificateSummary expired = Certificate("example.com") with { NotAfter = Now.AddDays(-1) };
        CertificateSummary pending = Certificate("www.example.com") with { Status = CertificateStatus.Pending };
        CertificateSummary valid = Certificate("off.example.com");
        ConfigDocument document = new(Hosts: [Host("example.com", "www.example.com"), Host("off.example.com") with { Enabled = false }]);

        Assert.Empty(CertificateUsage.Of(document, [expired, pending, valid], Now));
    }

    private static CertificateSummary Certificate(params string[] names) => new(
        CertificateId.New(), CertificateSource.Uploaded, CertificateStatus.Active, CertificateValidity.Valid, names, Now.AddDays(-10), Now.AddDays(80),
        $"CN={names[0]}", "CN=Test CA", "01", new string('a', 64), "ECDSA P-256", Now, Now);

    private static HostConfig Host(params string[] names) => new(HostId.New(), names);
}
