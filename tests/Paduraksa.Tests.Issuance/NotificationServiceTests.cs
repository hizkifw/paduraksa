using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Notifications;
using Paduraksa.Contracts.Issuance;
using Paduraksa.Contracts.Notifications;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Notifications;
using Paduraksa.Tests.Shared;
using static Paduraksa.Tests.Data.Config.TestConfigDatabase;
using static Paduraksa.Tests.Issuance.IssuanceHarness;

namespace Paduraksa.Tests.Issuance;

/// <summary>Channel CRUD and the test button on a real config database and writer: validation per kind, secrets only in the vault, audit, permissions.</summary>
public sealed class NotificationServiceTests
{
    private static readonly CommandOrigin Stranger = new(Actor.ForUser(UserId.New()), Channel.Api, IPAddress.Loopback);
    private static readonly NotificationEventKind[] Events = [NotificationEventKind.UpstreamDown, NotificationEventKind.CertificateExpiring];

    private static NotificationService Service(IssuanceHarness h, INotificationSenders? senders = null) => new(
        h.Config.Db.Factory, h.Config.Service, h.Secrets, senders ?? new NoSenders(), new NotificationOptions { AdminUrl = new Uri("https://admin.example.com") },
        h.Time, h.Loggers.CreateLogger<NotificationService>(), h.Permissions);

    private static NotificationChannelBody Discord(string? secret = "https://discord.com/api/webhooks/1/abc-token") =>
        new(NotificationChannelKind.Discord, "  alerts  ", Events, Secret: secret);

    private static NotificationChannelBody Email() => new(
        NotificationChannelKind.Email, "mail", Events, SmtpHost: "smtp.example.com", SmtpPort: 587, SmtpUsername: "mailer", MailFrom: "paduraksa@example.com", MailTo: ["ops@example.com"], Secret: "smtp-password");

    private static async Task<(SecretId? Secret, SecretId? Signing)> SecretsOf(IssuanceHarness h, NotificationChannelId id)
    {
        await using var context = await h.Config.Db.Factory.CreateDbContextAsync(Ct);
        var row = await context.NotificationChannels.AsNoTracking().SingleAsync(c => c.Id == id, Ct);
        return (row.SecretId, row.SigningSecretId);
    }

    [Fact]
    public async Task AChannelKeepsOnlyTheVaultIdOfItsSecretAndIsAudited()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var service = Service(h);

        var result = await service.CreateAsync(Discord(), Admin, Ct);

        Assert.Equal(IssuanceChangeStatus.Committed, result.Status);
        var channel = result.Channel!;
        Assert.Equal(("alerts", true, true), (channel.Label, channel.HasSecret, channel.Enabled));
        Assert.Equal(Events, channel.Events);
        var (secret, signing) = await SecretsOf(h, channel.Id);
        Assert.Null(signing);
        Assert.Equal("https://discord.com/api/webhooks/1/abc-token", Encoding.UTF8.GetString(h.Secrets.ValueOf(secret!.Value)!));
        Assert.Equal(SecretKind.NotificationSecret, (await h.Secrets.DescribeAsync(secret.Value, Ct))!.Kind);
        var audit = Assert.Single(await h.AuditAsync("notification.channel.create"));
        Assert.Equal((AuditOutcome.Success, channel.Id.ToString()), (audit.Outcome, audit.Target));
        Assert.DoesNotContain("abc-token", audit.Details ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("abc-token", channel.ToString(), StringComparison.Ordinal);
        await h.Config.AssertChainIntactAsync();
    }

    [Fact]
    public async Task NoSecretReachesTheDatabaseTheAuditOrAnyLog()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var service = Service(h);
        var body = new NotificationChannelBody(NotificationChannelKind.Webhook, "hook", Events, Secret: "https://hooks.example.com/in/very-secret-path", SigningSecret: "very-secret-signing-key");

        var created = await service.CreateAsync(body, Admin, Ct);
        _ = await service.UpdateAsync(created.Channel!.Id, body with { Label = "hook 2", Secret = "https://hooks.example.com/in/another-secret" }, Admin, Ct);
        _ = await service.DeleteAsync(created.Channel.Id, Admin, Ct);

        var everything = string.Join('\n', (await h.Config.AuditAsync()).Select(a => a.Details + a.Target + a.Action)) + string.Join('\n', h.Loggers.Entries.Select(e => e.Message));
        foreach (var needle in new[] { "very-secret-path", "very-secret-signing-key", "another-secret" })
        {
            Assert.DoesNotContain(needle, everything, StringComparison.Ordinal);
        }

        Assert.Equal(0, h.Secrets.Count);
    }

    [Fact]
    public async Task AWebhookSigningKeyIsAnHmacKeyInTheVault()
    {
        await using var h = await IssuanceHarness.CreateAsync();

        var result = await Service(h).CreateAsync(new(NotificationChannelKind.Webhook, "hook", Events, Secret: "https://hooks.example.com/in", SigningSecret: "k"), Admin, Ct);

        Assert.True(result.Channel!.HasSigningSecret);
        var (_, signing) = await SecretsOf(h, result.Channel.Id);
        var metadata = (await h.Secrets.DescribeAsync(signing!.Value, Ct))!;
        Assert.Equal((SecretKind.HmacSigningKey, MacAlgorithm.HmacSha256), (metadata.Kind, metadata.MacAlgorithm));
    }

    [Theory]
    [InlineData("kind", "/kind", NotificationErrorCodes.KindUndefined)]
    [InlineData("label", "/label", NotificationErrorCodes.LabelRequired)]
    [InlineData("events", "/events", NotificationErrorCodes.EventsRequired)]
    [InlineData("testEvent", "/events", NotificationErrorCodes.EventUndefined)]
    [InlineData("secret", "/secret", NotificationErrorCodes.SecretRequired)]
    [InlineData("discordUrl", "/secret", NotificationErrorCodes.UrlInvalid)]
    [InlineData("topicOnDiscord", "/topic", NotificationErrorCodes.FieldNotForKind)]
    [InlineData("signingOnDiscord", "/signingSecret", NotificationErrorCodes.FieldNotForKind)]
    [InlineData("smtpOnDiscord", "/smtpHost", NotificationErrorCodes.FieldNotForKind)]
    public async Task ARequestThatCannotWorkIsRefusedAndStoresNothing(string problem, string path, string code)
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var body = problem switch
        {
            "kind" => Discord() with { Kind = (NotificationChannelKind)99 },
            "label" => Discord() with { Label = "  " },
            "events" => Discord() with { Events = [] },
            "testEvent" => Discord() with { Events = [NotificationEventKind.Test] },
            "secret" => Discord(secret: null),
            "discordUrl" => Discord("https://example.com/not-discord"),
            "topicOnDiscord" => Discord() with { Topic = "x" },
            "signingOnDiscord" => Discord() with { SigningSecret = "x" },
            _ => Discord() with { SmtpHost = "smtp.example.com" },
        };

        var result = await Service(h).CreateAsync(body, Admin, Ct);

        Assert.Equal(IssuanceChangeStatus.Invalid, result.Status);
        Assert.Contains(result.Errors, e => e.Path == path && e.Code == code);
        Assert.Equal(0, h.Secrets.Puts);
        Assert.Empty(await Service(h).ListAsync(Ct));
    }

    [Fact]
    public async Task EachKindsFieldsAreChecked()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var service = Service(h);
        (NotificationChannelBody Body, string Path)[] bad =
        [
            (new(NotificationChannelKind.Ntfy, "n", Events, ServerUrl: "ftp://x", Topic: "t"), "/serverUrl"),
            (new(NotificationChannelKind.Ntfy, "n", Events, ServerUrl: "https://ntfy.sh", Topic: "has space"), "/topic"),
            (new(NotificationChannelKind.Ntfy, "n", Events, ServerUrl: "https://user:pw@ntfy.sh", Topic: "t"), "/serverUrl"),
            (new(NotificationChannelKind.Gotify, "g", Events, ServerUrl: "https://gotify.lan"), "/secret"),
            (new(NotificationChannelKind.Webhook, "w", Events, Secret: "not a url"), "/secret"),
            (Email() with { SmtpHost = "bad host" }, "/smtpHost"),
            (Email() with { SmtpPort = 70000 }, "/smtpPort"),
            (Email() with { MailFrom = "nobody" }, "/mailFrom"),
            (Email() with { MailTo = [] }, "/mailTo"),
            (Email() with { MailTo = ["a@example.com\r\nBcc: x@example.com"] }, "/mailTo"),
            (Email() with { Secret = null }, "/secret"),
        ];

        foreach (var (body, path) in bad)
        {
            var result = await service.CreateAsync(body, Admin, Ct);
            Assert.Equal(IssuanceChangeStatus.Invalid, result.Status);
            Assert.Contains(result.Errors, e => e.Path == path);
        }

        var ok = await service.CreateAsync(new(NotificationChannelKind.Ntfy, "n", Events, ServerUrl: "https://ntfy.sh/", Topic: "home-lab"), Admin, Ct);
        Assert.Equal("https://ntfy.sh", ok.Channel!.ServerUrl);
        Assert.False(ok.Channel.HasSecret);
        Assert.Equal(SmtpSecurity.StartTls, (await service.CreateAsync(Email(), Admin, Ct)).Channel!.SmtpSecurity);
    }

    [Fact]
    public async Task AMailChannelWithoutTlsCannotCarryALoginOrPassword()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var service = Service(h);

        var login = await service.CreateAsync(Email() with { SmtpSecurity = SmtpSecurity.None }, Admin, Ct);
        var password = await service.CreateAsync(Email() with { SmtpSecurity = SmtpSecurity.None, SmtpUsername = null }, Admin, Ct);
        var relay = await service.CreateAsync(Email() with { SmtpSecurity = SmtpSecurity.None, SmtpUsername = null, Secret = null }, Admin, Ct);
        var tls = await service.CreateAsync(Email() with { SmtpSecurity = SmtpSecurity.ImplicitTls }, Admin, Ct);

        Assert.Equal(IssuanceChangeStatus.Invalid, login.Status);
        Assert.Contains(login.Errors, e => e.Path == "/smtpSecurity" && e.Code == NotificationErrorCodes.SmtpInsecureLogin);
        Assert.Equal(IssuanceChangeStatus.Invalid, password.Status);
        Assert.Equal(IssuanceChangeStatus.Committed, relay.Status);
        Assert.Equal(IssuanceChangeStatus.Committed, tls.Status);
        Assert.Equal(1, h.Secrets.Count);
        var update = await service.UpdateAsync(tls.Channel!.Id, Email() with { SmtpSecurity = SmtpSecurity.None, Secret = null }, Admin, Ct);
        Assert.Equal(IssuanceChangeStatus.Invalid, update.Status);
    }

    [Fact]
    public async Task AReplacementKeepsTheStoredSecretReplacesItInPlaceOrRemovesIt()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var service = Service(h);
        var channel = (await service.CreateAsync(new(NotificationChannelKind.Ntfy, "n", Events, ServerUrl: "https://ntfy.sh", Topic: "t", Secret: "tk_one"), Admin, Ct)).Channel!;
        var (first, _) = await SecretsOf(h, channel.Id);
        var replacement = new NotificationChannelBody(NotificationChannelKind.Ntfy, "n2", [NotificationEventKind.UpstreamUp], false, "https://ntfy.sh", "t2");

        var kept = await service.UpdateAsync(channel.Id, replacement, Admin, Ct);
        Assert.Equal(("n2", false, true, "t2"), (kept.Channel!.Label, kept.Channel.Enabled, kept.Channel.HasSecret, kept.Channel.Topic));
        Assert.Equal([NotificationEventKind.UpstreamUp], kept.Channel.Events);
        Assert.Equal("tk_one", Encoding.UTF8.GetString(h.Secrets.ValueOf(first!.Value)!));

        _ = await service.UpdateAsync(channel.Id, replacement with { Secret = "tk_two" }, Admin, Ct);
        Assert.Equal((first, "tk_two"), ((await SecretsOf(h, channel.Id)).Secret, Encoding.UTF8.GetString(h.Secrets.ValueOf(first.Value)!)));

        var removed = await service.UpdateAsync(channel.Id, replacement with { Secret = string.Empty }, Admin, Ct);
        Assert.False(removed.Channel!.HasSecret);
        Assert.Null(await h.Secrets.DescribeAsync(first.Value, Ct));
        Assert.Equal(3, (await h.AuditAsync("notification.channel.update")).Count);
    }

    [Fact]
    public async Task ARequiredSecretCannotBeRemovedAndAKindCannotChange()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var service = Service(h);
        var channel = (await service.CreateAsync(Discord(), Admin, Ct)).Channel!;

        var empty = await service.UpdateAsync(channel.Id, Discord(secret: string.Empty), Admin, Ct);
        var kind = await service.UpdateAsync(channel.Id, new(NotificationChannelKind.Webhook, "x", Events, Secret: "https://hooks.example.com/in"), Admin, Ct);

        Assert.Equal(IssuanceChangeStatus.Invalid, empty.Status);
        Assert.Equal(IssuanceChangeStatus.Invalid, kind.Status);
        Assert.Contains(kind.Errors, e => e.Code == NotificationErrorCodes.KindFixed);
        Assert.True((await service.GetAsync(channel.Id, Ct))!.HasSecret);
        Assert.Equal(IssuanceChangeStatus.NotFound, (await service.UpdateAsync(NotificationChannelId.New(), Discord(), Admin, Ct)).Status);
    }

    [Fact]
    public async Task DeletingAChannelTakesItsSecretsOutOfTheVault()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var service = Service(h);
        var channel = (await service.CreateAsync(new(NotificationChannelKind.Webhook, "hook", Events, Secret: "https://hooks.example.com/in", SigningSecret: "k"), Admin, Ct)).Channel!;
        Assert.Equal(2, h.Secrets.Count);

        var result = await service.DeleteAsync(channel.Id, Admin, Ct);

        Assert.Equal(IssuanceChangeStatus.Committed, result.Status);
        Assert.Equal(0, h.Secrets.Count);
        Assert.Null(await service.GetAsync(channel.Id, Ct));
        Assert.Equal(IssuanceChangeStatus.NotFound, (await service.DeleteAsync(channel.Id, Admin, Ct)).Status);
        Assert.Single(await h.AuditAsync("notification.channel.delete"));
    }

    [Fact]
    public async Task EveryChangeAndTheTestNeedProvidersWriteAndAreAuditedAsDeniedOtherwise()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var channel = (await Service(h).CreateAsync(Discord(), Admin, Ct)).Channel!;
        h.Permissions = TestPermissions.Holding(Permission.ProvidersRead);
        var service = Service(h);

        Assert.Equal(IssuanceChangeStatus.Denied, (await service.CreateAsync(Discord(), Stranger, Ct)).Status);
        Assert.Equal(IssuanceChangeStatus.Denied, (await service.UpdateAsync(channel.Id, Discord(), Stranger, Ct)).Status);
        Assert.Equal(IssuanceChangeStatus.Denied, (await service.DeleteAsync(channel.Id, Stranger, Ct)).Status);
        Assert.True((await service.TestAsync(channel.Id, Stranger, Ct)).Denied);

        Assert.Equal(1, h.Secrets.Count);
        foreach (var action in new[] { "create", "update", "delete", "test" })
        {
            Assert.Single(await h.AuditAsync($"notification.channel.{action}"), a => a.Outcome == AuditOutcome.Denied);
        }
    }

    [Fact]
    public async Task TheTestSendsOneMessageWithoutRetriesAndRecordsTheOutcomeOnTheChannel()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        RecordingSender sender = new();
        var service = Service(h, new Senders(sender));
        var channel = (await service.CreateAsync(Discord(), Admin, Ct)).Channel!;

        var worked = await service.TestAsync(channel.Id, Admin, Ct);
        sender.Failure = new NotificationDeliveryException("The server answered HTTP 404.", transient: false);
        var failed = await service.TestAsync(channel.Id, Admin, Ct);

        Assert.True(worked.Delivered);
        Assert.Equal((false, "The server answered HTTP 404."), (failed.Delivered, failed.Error));
        var message = sender.Sent[0];
        Assert.Equal((NotificationEventKind.Test, "Test from Paduraksa", "https://admin.example.com/settings/notifications"), (message.Kind, message.Title, message.Link));
        var summary = (await service.GetAsync(channel.Id, Ct))!;
        Assert.Equal(("The server answered HTTP 404.", true), (summary.LastError, summary.LastSentAt is not null));
        Assert.Equal([AuditOutcome.Success, AuditOutcome.Failed], (await h.AuditAsync("notification.channel.test")).Select(a => a.Outcome));
        Assert.False((await service.TestAsync(NotificationChannelId.New(), Admin, Ct)).Found);
    }

    private sealed class NoSenders : INotificationSenders
    {
        public INotificationSender? Find(NotificationChannelKind kind) => null;
    }

    private sealed class Senders(INotificationSender sender) : INotificationSenders
    {
        public INotificationSender? Find(NotificationChannelKind kind) => sender;
    }

    private sealed class RecordingSender : INotificationSender
    {
        public NotificationChannelKind Kind => NotificationChannelKind.Discord;

        public List<NotificationMessage> Sent { get; } = [];

        public Exception? Failure { get; set; }

        public Task SendAsync(ChannelTarget channel, NotificationMessage message, CancellationToken cancellationToken)
        {
            Sent.Add(message);
            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }
    }
}
