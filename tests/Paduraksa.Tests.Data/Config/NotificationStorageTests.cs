using Microsoft.EntityFrameworkCore;
using Paduraksa.Core.Model;
using Paduraksa.Data.Certificates;
using Paduraksa.Data.Notifications;
using static Paduraksa.Tests.Data.Config.TestConfigDatabase;

namespace Paduraksa.Tests.Data.Config;

public sealed class NotificationStorageTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static NotificationChannelRow Channel() => new()
    {
        Id = NotificationChannelId.New(),
        Kind = NotificationChannelKind.Email,
        Label = "ops mailbox",
        Events = ["upstream.down", "certificate.expiring"],
        SmtpHost = "smtp.example.com",
        SmtpPort = 587,
        SmtpSecurity = SmtpSecurity.StartTls,
        MailFrom = "paduraksa@example.com",
        MailTo = ["ops@example.com", "me@example.com"],
        SecretId = SecretId.New(),
        CreatedAt = T0,
        UpdatedAt = T0,
    };

    [Fact]
    public async Task AChannelRoundTripsWithStableNamesAndNoSecretColumn()
    {
        await using var db = await CreateAsync();
        var channel = Channel();
        await using (var write = db.NewContext())
        {
            write.NotificationChannels.Add(channel);
            await write.SaveChangesAsync(Ct);
        }

        Assert.Equal(["email", "starttls"], (await db.QueryAsync("SELECT Kind, SmtpSecurity FROM NotificationChannels")).Single());
        await using var read = db.NewContext();
        var back = await read.NotificationChannels.SingleAsync(Ct);
        Assert.Equal(channel.Events, back.Events);
        Assert.Equal(channel.MailTo, back.MailTo);
        Assert.Equal(channel.SecretId, back.SecretId);

        var columns = (await db.QueryAsync("SELECT name FROM pragma_table_info('NotificationChannels')")).Select(r => (string)r[0]!).ToList();
        Assert.DoesNotContain(columns, c => c.Contains("Token", StringComparison.OrdinalIgnoreCase) || c.Contains("Password", StringComparison.OrdinalIgnoreCase) || c.Contains("Url", StringComparison.OrdinalIgnoreCase) && c != "ServerUrl");
    }

    [Fact]
    public async Task UnknownNamesAreRefused()
    {
        await using var db = await CreateAsync();
        await using (var write = db.NewContext())
        {
            write.NotificationChannels.Add(Channel());
            await write.SaveChangesAsync(Ct);
        }

        var kind = await Assert.ThrowsAnyAsync<Exception>(() => db.ExecuteAsync("UPDATE NotificationChannels SET Kind = 'sms'"));
        Assert.Contains("CK_NotificationChannels_Kind", kind.Message, StringComparison.Ordinal);
        var security = await Assert.ThrowsAnyAsync<Exception>(() => db.ExecuteAsync("UPDATE NotificationChannels SET SmtpSecurity = 'ssl'"));
        Assert.Contains("CK_NotificationChannels_SmtpSecurity", security.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpiryNoticesAreOnePerChainAndThresholdAndGoWithTheCertificate()
    {
        await using var db = await CreateAsync();
        CertificateRow certificate = new()
        {
            Id = CertificateId.New(),
            Source = CertificateSource.Uploaded,
            Status = CertificateStatus.Active,
            NotBefore = T0,
            NotAfter = T0.AddDays(90),
            CreatedAt = T0,
            UpdatedAt = T0,
        };
        await using (var write = db.NewContext())
        {
            write.Certificates.Add(certificate);
            write.ExpiryNotices.Add(new ExpiryNoticeRow { CertificateId = certificate.Id, NotAfter = 100, Days = 30, SentAt = T0 });
            await write.SaveChangesAsync(Ct);
        }

        await using (var again = db.NewContext())
        {
            again.ExpiryNotices.Add(new ExpiryNoticeRow { CertificateId = certificate.Id, NotAfter = 100, Days = 30, SentAt = T0 });
            await Assert.ThrowsAsync<DbUpdateException>(() => again.SaveChangesAsync(Ct));
        }

        await using (var renewed = db.NewContext())
        {
            renewed.ExpiryNotices.Add(new ExpiryNoticeRow { CertificateId = certificate.Id, NotAfter = 200, Days = 30, SentAt = T0 });
            await renewed.SaveChangesAsync(Ct);
        }

        await db.ExecuteAsync("DELETE FROM Certificates");
        Assert.Equal(0L, await db.ScalarAsync("SELECT count(*) FROM ExpiryNotices"));
    }
}
