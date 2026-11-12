using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Paduraksa.Application.Certificates;
using Paduraksa.Application.Notifications;
using Paduraksa.Contracts.Certificates;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using static Paduraksa.Tests.Data.Config.TestConfigDatabase;
using static Paduraksa.Tests.Issuance.IssuanceHarness;

namespace Paduraksa.Tests.Issuance;

/// <summary>The expiry notices: every source, 30/14/7/1 days, once per threshold per chain, persisted, and nothing marked while nobody listens.</summary>
public sealed class CertificateExpiryMonitorTests
{
    private static CertificateInfo Info(string name, DateTimeOffset notAfter, string fingerprint = "f1") => new(
        $"CN={name}", [name], notAfter.AddDays(-90), notAfter, "CN=Test", "01", null, fingerprint, "ECDSA P-256", [], "-----BEGIN CERTIFICATE-----\nAAAA\n-----END CERTIFICATE-----\n");

    private static async Task<CertificateId> Register(IssuanceHarness h, CertificateSource source, string name, TimeSpan remaining, CertificateId? id = null, string fingerprint = "f1")
    {
        var cid = id ?? CertificateId.New();
        _ = await h.Certificates.RegisterAsync(new CertificateRegistration(cid, source, Info(name, h.Time.GetUtcNow() + remaining, fingerprint)), Admin, Ct);
        return cid;
    }

    private static CertificateExpiryMonitor Monitor(IssuanceHarness h, RecordingEvents events) => new(
        h.Config.Db.Factory, h.Certificates, events, new NotificationOptions(), h.Time, h.Loggers.CreateLogger<CertificateExpiryMonitor>());

    [Fact]
    public async Task EverySourceIsWatchedAndACertificateFarFromExpiryIsNot()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var events = new RecordingEvents();
        foreach (var (source, name) in new[]
        {
            (CertificateSource.Acme, "acme.example.com"), (CertificateSource.Uploaded, "uploaded.example.com"), (CertificateSource.SelfSigned, "self.example.com"),
            (CertificateSource.LocalCa, "leaf.example.com"), (CertificateSource.LocalCaRoot, "root.example.com"),
        })
        {
            _ = await Register(h, source, name, TimeSpan.FromDays(10));
        }

        _ = await Register(h, CertificateSource.Acme, "fine.example.com", TimeSpan.FromDays(60));

        var sent = await Monitor(h, events).RunOnceAsync(Ct);

        Assert.Equal(5, sent);
        Assert.Equal(5, events.Published.Count(e => e.Kind == NotificationEventKind.CertificateExpiring));
        Assert.DoesNotContain(events.Published, e => e.Title.Contains("fine.example.com", StringComparison.Ordinal));
        Assert.All(events.Published, e => Assert.Equal("Certificate " + e.Title.Split(' ')[1] + " expires in 10 days", e.Title));
    }

    [Fact]
    public async Task AThresholdIsSentOnceAndTheNextOneWhenItIsCrossed()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var events = new RecordingEvents();
        var monitor = Monitor(h, events);
        _ = await Register(h, CertificateSource.Uploaded, "app.example.com", TimeSpan.FromDays(29));

        Assert.Equal(1, await monitor.RunOnceAsync(Ct));
        Assert.Equal(0, await monitor.RunOnceAsync(Ct));
        h.Time.Now += TimeSpan.FromDays(10);
        Assert.Equal(0, await monitor.RunOnceAsync(Ct));
        h.Time.Now += TimeSpan.FromDays(6);
        Assert.Equal(1, await monitor.RunOnceAsync(Ct));
        h.Time.Now += TimeSpan.FromDays(7);
        Assert.Equal(1, await monitor.RunOnceAsync(Ct));
        h.Time.Now += TimeSpan.FromDays(5.5);
        Assert.Equal(1, await monitor.RunOnceAsync(Ct));
        Assert.Equal(0, await monitor.RunOnceAsync(Ct));

        Assert.Equal(
            ["Certificate app.example.com expires in 29 days", "Certificate app.example.com expires in 13 days", "Certificate app.example.com expires in 6 days", "Certificate app.example.com expires within a day"],
            events.Published.Select(e => e.Title));
    }

    [Fact]
    public async Task ARestartDoesNotSendAgainBecauseWhatWasSentIsStored()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var events = new RecordingEvents();
        _ = await Register(h, CertificateSource.Acme, "app.example.com", TimeSpan.FromDays(6));
        Assert.Equal(1, await Monitor(h, events).RunOnceAsync(Ct));

        var afterRestart = await Monitor(h, events).RunOnceAsync(Ct);

        Assert.Equal(0, afterRestart);
        await using var context = await h.Config.Db.Factory.CreateDbContextAsync(Ct);
        Assert.Equal([7, 14, 30], (await context.ExpiryNotices.AsNoTracking().ToListAsync(Ct)).Select(n => n.Days).Order());
    }

    [Fact]
    public async Task SeveralCrossedThresholdsAreOneMessageAndAllAreMarked()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var events = new RecordingEvents();
        _ = await Register(h, CertificateSource.Uploaded, "late.example.com", TimeSpan.FromHours(20));

        var sent = await Monitor(h, events).RunOnceAsync(Ct);

        Assert.Equal(1, sent);
        Assert.Equal("Certificate late.example.com expires within a day", Assert.Single(events.Published).Title);
        await using var context = await h.Config.Db.Factory.CreateDbContextAsync(Ct);
        Assert.Equal([1, 7, 14, 30], (await context.ExpiryNotices.AsNoTracking().ToListAsync(Ct)).Select(n => n.Days).Order());
    }

    [Fact]
    public async Task ARenewalStartsTheThresholdsOverAndDropsTheOldChainsNotices()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var events = new RecordingEvents();
        var monitor = Monitor(h, events);
        var id = await Register(h, CertificateSource.Acme, "app.example.com", TimeSpan.FromDays(6));
        Assert.Equal(1, await monitor.RunOnceAsync(Ct));

        _ = await Register(h, CertificateSource.Acme, "app.example.com", TimeSpan.FromDays(80), id, "f2");
        Assert.Equal(0, await monitor.RunOnceAsync(Ct));
        h.Time.Now += TimeSpan.FromDays(60);
        Assert.Equal(1, await monitor.RunOnceAsync(Ct));

        Assert.Equal(2, events.Published.Count);
        await using var context = await h.Config.Db.Factory.CreateDbContextAsync(Ct);
        Assert.Single((await context.ExpiryNotices.AsNoTracking().ToListAsync(Ct)).Select(n => n.NotAfter).Distinct());
    }

    [Fact]
    public async Task NothingIsMarkedWhileNoChannelIsSubscribed()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var events = new RecordingEvents { Subscribed = false };
        _ = await Register(h, CertificateSource.Uploaded, "app.example.com", TimeSpan.FromDays(5));

        Assert.Equal(0, await Monitor(h, events).RunOnceAsync(Ct));
        events.Subscribed = true;

        Assert.Equal(1, await Monitor(h, events).RunOnceAsync(Ct));
    }

    [Fact]
    public async Task ADeletedCertificateTakesItsNoticesWithIt()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var events = new RecordingEvents();
        var id = await Register(h, CertificateSource.Uploaded, "app.example.com", TimeSpan.FromDays(5));
        _ = await Monitor(h, events).RunOnceAsync(Ct);

        _ = await h.Certificates.DeleteAsync(id, Admin, Ct);

        await using var context = await h.Config.Db.Factory.CreateDbContextAsync(Ct);
        Assert.Empty(await context.ExpiryNotices.AsNoTracking().ToListAsync(Ct));
    }
}
