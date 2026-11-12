using Paduraksa.Core.Model;
using Paduraksa.Notifications;
using Paduraksa.Tests.Notifications.Fakes;

namespace Paduraksa.Tests.Notifications;

/// <summary>The email sender against an in-process SMTP server.</summary>
public sealed class EmailSenderTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 12, 14, 2, 30, TimeSpan.Zero);
    private static readonly NotificationMessage Message = new(
        NotificationEventKind.CertificateExpiring, "Certificate for app.example.com expires in 7 days", "Renew it before 2026-11-19.", NotificationSeverity.Warning, At, "https://admin.example.com/certificates");

    private readonly FakeSecrets _secrets = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Senders that trust exactly <paramref name="trusted"/>'s certificate, or the system roots when null.</summary>
    private NotificationSenders Senders(FakeSmtpServer? trusted) =>
        new(_secrets, new FakeMacKeys(), TimeProvider.System, NotificationHttp.CreateHandler(), trusted is null ? null : (_, certificate, _, _) => certificate?.GetCertHashString() == trusted.Thumbprint);

    private ChannelTarget Channel(FakeSmtpServer server, SmtpSecurity security, string? username = null, string? password = null, string[]? to = null) => new(
        NotificationChannelId.New(),
        NotificationChannelKind.Email,
        null,
        null,
        new SmtpTarget("127.0.0.1", server.Port, security, username, "paduraksa@example.com", to ?? ["ops@example.com", "me@example.com"]),
        password is null ? null : _secrets.Add(password),
        null);

    private static Task Send(NotificationSenders senders, ChannelTarget channel) => senders.Find(channel.Kind)!.SendAsync(channel, Message, Ct);

    [Fact]
    public async Task SendsAPlainTextMailToEveryRecipient()
    {
        await using FakeSmtpServer server = new();
        using var senders = Senders(null);

        await Send(senders, Channel(server, SmtpSecurity.None));

        var mail = Assert.Single(server.Mail);
        Assert.Equal("paduraksa@example.com", mail.From);
        Assert.Equal(["ops@example.com", "me@example.com"], mail.To);
        Assert.Contains("Subject: [Paduraksa] Certificate for app.example.com expires in 7 days", mail.Data, StringComparison.Ordinal);
        Assert.Contains("Auto-Submitted: auto-generated", mail.Data, StringComparison.Ordinal);
        Assert.Contains("Content-Type: text/plain", mail.Data, StringComparison.Ordinal);
        Assert.Contains("Renew it before 2026-11-19.", mail.Data, StringComparison.Ordinal);
        Assert.Contains("https://admin.example.com/certificates", mail.Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartTlsUpgradesBeforeTheLoginAndTheLoginUsesTheVaultPassword()
    {
        await using FakeSmtpServer server = new(offerStartTls: true, requireAuth: true);
        using var senders = Senders(server);

        await Send(senders, Channel(server, SmtpSecurity.StartTls, "mailer", "p@ss word"));

        Assert.Equal("mailer:p@ss word", Assert.Single(server.Mail).Login);
    }

    [Fact]
    public async Task ImplicitTlsConnectsSecureFromTheFirstByte()
    {
        await using FakeSmtpServer server = new(implicitTls: true);
        using var senders = Senders(server);

        await Send(senders, Channel(server, SmtpSecurity.ImplicitTls));

        Assert.Single(server.Mail);
    }

    [Fact]
    public async Task AServerCertificateTheSystemDoesNotTrustIsRefusedAndNothingIsSent()
    {
        await using FakeSmtpServer server = new(implicitTls: true);
        using var senders = Senders(null);

        var failure = await Assert.ThrowsAsync<NotificationDeliveryException>(() => Send(senders, Channel(server, SmtpSecurity.ImplicitTls, "mailer", "secret-password")));

        Assert.False(failure.Transient);
        Assert.Equal("The TLS handshake with the mail server failed.", failure.Message);
        Assert.Empty(server.Mail);
    }

    [Fact]
    public async Task StartTlsIsRequiredNotOpportunistic()
    {
        await using FakeSmtpServer server = new(offerStartTls: false);
        using var senders = Senders(server);

        var failure = await Assert.ThrowsAsync<NotificationDeliveryException>(() => Send(senders, Channel(server, SmtpSecurity.StartTls, "mailer", "secret-password")));

        Assert.False(failure.Transient);
        Assert.Contains("STARTTLS", failure.Message, StringComparison.Ordinal);
        Assert.Empty(server.Mail);
    }

    [Theory]
    [InlineData("550 no such user", false, "refused the message (550)")]
    [InlineData("451 try later", true, "refused the message (451); will retry")]
    public async Task ARefusalIsPermanentFor5xxAndTransientFor4xx(string reply, bool transient, string expected)
    {
        await using FakeSmtpServer server = new() { RejectRecipientWith = reply };
        using var senders = Senders(null);

        var failure = await Assert.ThrowsAsync<NotificationDeliveryException>(() => Send(senders, Channel(server, SmtpSecurity.None)));

        Assert.Equal(transient, failure.Transient);
        Assert.Contains(expected, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ops@example.com", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusedLoginIsPermanentAndNeverEchoesThePassword()
    {
        await using FakeSmtpServer server = new() { RejectAuthWith = "535 5.7.8 bad credentials" };
        using var senders = Senders(null);

        var failure = await Assert.ThrowsAsync<NotificationDeliveryException>(() => Send(senders, Channel(server, SmtpSecurity.None, "mailer", "secret-password")));

        Assert.False(failure.Transient);
        Assert.Equal("The mail server refused the login.", failure.Message);
    }

    [Fact]
    public async Task ABadAddressOrAMissingPasswordFailsBeforeAnyConnection()
    {
        await using FakeSmtpServer server = new();
        using var senders = Senders(null);

        var address = await Assert.ThrowsAsync<NotificationDeliveryException>(() => Send(senders, Channel(server, SmtpSecurity.None, to: ["not an address"])));
        var password = await Assert.ThrowsAsync<NotificationDeliveryException>(() => Send(senders, Channel(server, SmtpSecurity.None, "mailer")));

        Assert.False(address.Transient);
        Assert.False(password.Transient);
        Assert.Empty(server.Mail);
    }

    [Fact]
    public async Task AServerThatIsNotThereIsATransientFailure()
    {
        FakeSmtpServer gone = new();
        var channel = Channel(gone, SmtpSecurity.None);
        await gone.DisposeAsync();
        using var senders = Senders(null);

        var failure = await Assert.ThrowsAsync<NotificationDeliveryException>(() => Send(senders, channel));

        Assert.True(failure.Transient);
    }
}
