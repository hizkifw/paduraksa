using Paduraksa.Application.Certificates;
using Paduraksa.Contracts.Certificates;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Tests.Unit.Ids;

namespace Paduraksa.Tests.Unit.Certificates;

/// <summary>
/// The admin certificate's name set and renewal decision: pure arithmetic over the inventory's public
/// facts, driven here by a fake clock so the threshold is pinned without waiting months.
/// </summary>
public sealed class AdminCertificateTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("localhost", "127.0.0.1")]
    [InlineData("127.0.0.1", "localhost")]
    [InlineData("LOCALHOST", "127.0.0.1")]
    public void NamesInADifferentOrderOrCaseAreTheSameSet(string first, string second) =>
        Assert.False(AdminCertificatePolicy.ShouldReissue(Summary([first, second], Start, TimeSpan.FromDays(90)), ["localhost", "127.0.0.1"], Start));

    [Fact]
    public void AnAddedNameForcesAReissue()
    {
        var current = Summary(["admin.lan"], Start, TimeSpan.FromDays(90));
        Assert.True(AdminCertificatePolicy.ShouldReissue(current, ["admin.lan", "extra.lan"], Start));
    }

    [Fact]
    public void ARemovedNameForcesAReissue()
    {
        var current = Summary(["admin.lan", "extra.lan"], Start, TimeSpan.FromDays(90));
        Assert.True(AdminCertificatePolicy.ShouldReissue(current, ["admin.lan"], Start));
    }

    [Fact]
    public void AnUnchangedSetWithMostOfItsLifetimeLeftIsKept()
    {
        // 90 days of life, one day old: nowhere near the last third.
        ManualTimeProvider clock = new(Start.AddDays(1));
        Assert.False(AdminCertificatePolicy.ShouldReissue(Summary(["admin.lan"], Start, TimeSpan.FromDays(90)), ["admin.lan"], clock.GetUtcNow()));
    }

    [Fact]
    public void AThirdOfTheLifetimeLeftIsTheRenewalPoint()
    {
        // 90 days of life: the last third starts at day 60.
        var current = Summary(["admin.lan"], Start, TimeSpan.FromDays(90));
        Assert.False(AdminCertificatePolicy.ShouldReissue(current, ["admin.lan"], Start.AddDays(59)));
        Assert.True(AdminCertificatePolicy.ShouldReissue(current, ["admin.lan"], Start.AddDays(60)));
        Assert.True(AdminCertificatePolicy.ShouldReissue(current, ["admin.lan"], Start.AddDays(61)));
    }

    [Fact]
    public void AnExpiredCertificateIsAlwaysReissued() =>
        Assert.True(AdminCertificatePolicy.ShouldReissue(Summary(["admin.lan"], Start, TimeSpan.FromDays(90)), ["admin.lan"], Start.AddDays(120)));

    [Fact]
    public void ACertificateNotYetValidIsKept() =>
        Assert.False(AdminCertificatePolicy.ShouldReissue(Summary(["admin.lan"], Start.AddDays(10), TimeSpan.FromDays(90)), ["admin.lan"], Start));

    [Fact]
    public void TheDesiredNamesAreTheConfiguredOnesPlusEveryLocalWayIn()
    {
        var names = AdminCertificateNames.Compose(["Admin.Lan"], "box.lan", "192.0.2.7");

        Assert.Equal(["admin.lan", "localhost", "127.0.0.1", "::1", "box.lan", "192.0.2.7"], names);
    }

    [Fact]
    public void AWildcardBindAndUnusableEntriesAreLeftOut()
    {
        var names = AdminCertificateNames.Compose(["not a name!", "admin.lan", "admin.lan"], "box.lan", "0.0.0.0");

        Assert.Equal(["admin.lan", "localhost", "127.0.0.1", "::1", "box.lan"], names);
    }

    [Fact]
    public void TheUrlsBracketIPv6AndSkipWildcards() =>
        Assert.Equal(
            ["https://admin.lan:9443/", "https://[::1]:9443/"],
            AdminCertificateNames.Urls(["admin.lan", "*.lan", "::1"], 9443));

    [Fact]
    public void AZoneIdOnTheBindAddressIsDropped()
    {
        // A scope id belongs to the interface, not to a certificate; the address itself is still covered.
        var names = AdminCertificateNames.Compose([], null, "fe80::1%eth0");

        Assert.Contains("fe80::1", names);
        Assert.DoesNotContain(names, name => name.Contains('%'));
    }

    [Fact]
    public void TheCapLeavesRoomForTheAutomaticNamesInsideTheVaultsOwnLimit()
    {
        // The vault refuses a certificate with more names than its limit; the configured list must stay below it once the
        // process has added its own.
        Assert.Equal(CertificateLimits.MaxNames, AdminCertificateNames.MaxNames);
        Assert.Equal(AdminCertificateNames.MaxNames, AdminCertificateNames.MaxConfiguredNames + AdminCertificateNames.AutomaticNames);

        var full = AdminCertificateNames.Compose(
            [.. Enumerable.Range(0, AdminCertificateNames.MaxConfiguredNames).Select(i => $"admin{i}.lan")],
            "box.lan",
            "192.0.2.7");
        Assert.True(full.Count <= CertificateLimits.MaxNames, $"{full.Count} names");
    }

    [Theory]
    [InlineData(-10, false)] // longer before NotBefore than the clock-skew tolerance allows
    [InlineData(-6, false)]
    [InlineData(-5, true)]   // exactly the tolerance
    [InlineData(0, true)]    // inside its validity period
    [InlineData(5, true)]
    [InlineData(10, false)]  // at NotAfter
    public void IsServableCoversBothEndsOfTheValidityPeriod(int minutesFromNow, bool servable)
    {
        CertificateInfo info = new(
            "CN=admin.lan",
            ["admin.lan"],
            Start,
            Start.AddMinutes(10),
            "CN=Paduraksa Local CA",
            "01",
            null,
            "00",
            "ECDSA P-256",
            ["CN=Paduraksa Local CA"],
            "-----BEGIN CERTIFICATE-----\nAAAA\n-----END CERTIFICATE-----\n");

        Assert.Equal(servable, AdminCertificatePolicy.IsServable(info, Start.AddMinutes(minutesFromNow)));
    }

    private static CertificateSummary Summary(IReadOnlyList<string> names, DateTimeOffset notBefore, TimeSpan lifetime) => new(
        CertificateId.New(),
        CertificateSource.LocalCa,
        CertificateStatus.Active,
        CertificateValidity.Valid,
        names,
        notBefore,
        notBefore + lifetime,
        "CN=admin.lan",
        "CN=Paduraksa Local CA",
        "01",
        "00",
        "ECDSA P-256",
        CreatedAt: notBefore,
        UpdatedAt: notBefore,
        Label: AdminCertificate.Label);
}
