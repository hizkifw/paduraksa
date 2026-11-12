using Bunit;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Issuance;
using Paduraksa.Contracts.Notifications;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.NotificationsArea;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>The notification channel form: fields per kind, secrets that are password fields emptied as the form is sent, and what a replacement sends.</summary>
public sealed class NotificationFormTests
{
    private static NotificationChannelSummary Existing(NotificationChannelKind kind = NotificationChannelKind.Ntfy, bool hasSecret = true) => new(
        NotificationChannelId.New(), kind, "ops", Enabled: true, [NotificationEventKind.UpstreamDown], [], hasSecret, HasSigningSecret: kind == NotificationChannelKind.Webhook,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, ServerUrl: "https://ntfy.sh", Topic: "home");

    private static NotificationChannelResult Saved(NotificationChannelSummary channel) => new(IssuanceChangeStatus.Committed, channel);

    [Fact]
    public async Task EachKindAsksForItsOwnFieldsAndANewChannelStartsWithTheUsualEvents()
    {
        await using DashboardTestContext context = new();
        var form = context.Render<NotificationForm>(p => p.Add(f => f.Submit, _ => Task.FromResult<NotificationChannelResult?>(null)));

        Assert.NotEmpty(form.FindAll("[data-field=topic]"));
        Assert.Empty(form.FindAll("[data-field=smtp-host]"));
        form.Find(".pdk-notify-kind-email").Click();
        Assert.NotEmpty(form.FindAll("[data-field=smtp-host]"));
        Assert.Empty(form.FindAll("[data-field=topic]"));
        form.Find(".pdk-notify-kind-webhook").Click();
        Assert.NotEmpty(form.FindAll("[data-field=signing-secret]"));
        form.Find(".pdk-notify-kind-discord").Click();
        Assert.Empty(form.FindAll("[data-field=signing-secret]"));

        Assert.Equal(9, form.FindAll("[data-event]").Count);
        Assert.Equal(7, form.FindAll("input[data-event][checked]").Count);
        Assert.Empty(form.FindAll("input[data-event='destination.down'][checked]"));
    }

    [Fact]
    public async Task ATokenIsAPasswordFieldAndLeavesTheFormWithTheBodyAndNothingStaysOnThePage()
    {
        await using DashboardTestContext context = new();
        NotificationChannelBody? sent = null;
        var form = context.Render<NotificationForm>(p => p
            .Add(f => f.Submit, body =>
            {
                sent = body;
                return Task.FromResult<NotificationChannelResult?>(Saved(Existing(NotificationChannelKind.Gotify)));
            }));

        form.Find(".pdk-notify-kind-gotify").Click();
        form.Find("[data-field=label]").Change("Gotify - lab");
        form.Find("[data-field=server-url]").Change(" https://gotify.lan ");
        Assert.Equal("password", form.Find("[data-field=secret]").GetAttribute("type"));
        form.Find("[data-field=secret]").Change("  app-token-123  ");
        await form.InvokeAsync(() => form.Find(".pdk-notify-submit").Click());

        Assert.Equal((NotificationChannelKind.Gotify, "Gotify - lab", "https://gotify.lan", "app-token-123"), (sent!.Kind, sent.Label, sent.ServerUrl, sent.Secret));
        Assert.Null(sent.Topic);
        Assert.Null(sent.SmtpHost);
        Assert.True(string.IsNullOrEmpty(form.Find("[data-field=secret]").GetAttribute("value")));
        Assert.DoesNotContain("app-token-123", form.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("app-token-123", sent.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmailChannelSendsItsAddressesSplitAndItsSecurityAndPort()
    {
        await using DashboardTestContext context = new();
        NotificationChannelBody? sent = null;
        var form = context.Render<NotificationForm>(p => p.Add(f => f.Submit, body =>
        {
            sent = body;
            return Task.FromResult<NotificationChannelResult?>(Saved(Existing(NotificationChannelKind.Email)));
        }));

        form.Find(".pdk-notify-kind-email").Click();
        form.Find("[data-field=smtp-host]").Change("smtp.example.com");
        form.Find("[data-field=mail-from]").Change("paduraksa@example.com");
        form.Find("[data-field=mail-to]").Change("ops@example.com, me@example.com;\n you@example.com");
        form.Find("[data-field=smtp-username]").Change("mailer");
        form.Find("[data-field=secret]").Change("smtp-pass");
        await form.InvokeAsync(() => form.Find(".pdk-notify-submit").Click());

        Assert.Equal(["ops@example.com", "me@example.com", "you@example.com"], sent!.MailTo);
        Assert.Equal(("smtp.example.com", 587, SmtpSecurity.StartTls, "mailer", "smtp-pass"), (sent.SmtpHost, sent.SmtpPort, sent.SmtpSecurity, sent.SmtpUsername, sent.Secret));
        Assert.Null(sent.ServerUrl);
    }

    [Fact]
    public async Task AReplacementKeepsTheStoredSecretUnlessOneIsTypedOrRemovalIsAsked()
    {
        await using DashboardTestContext context = new();
        var channel = Existing();
        List<NotificationChannelBody> sent = [];
        var form = context.Render<NotificationForm>(p => p
            .Add(f => f.Channel, channel)
            .Add(f => f.Submit, body =>
            {
                sent.Add(body);
                return Task.FromResult<NotificationChannelResult?>(Saved(channel));
            }));

        Assert.Empty(form.FindAll(".pdk-notify-kind-email"));
        Assert.Equal("ops", form.Find("[data-field=label]").GetAttribute("value"));
        await form.InvokeAsync(() => form.Find(".pdk-notify-submit").Click());
        Assert.Null(sent[0].Secret);

        form.Find("[data-field=secret]").Change("tk_new");
        await form.InvokeAsync(() => form.Find(".pdk-notify-submit").Click());
        Assert.Equal("tk_new", sent[1].Secret);

        form.Find("[data-field=remove-secret]").Change(true);
        await form.InvokeAsync(() => form.Find(".pdk-notify-submit").Click());
        Assert.Equal(string.Empty, sent[2].Secret);
    }

    [Fact]
    public async Task ARefusalMarksTheFieldAndSaysToEnterTheSecretAgain()
    {
        await using DashboardTestContext context = new();
        var form = context.Render<NotificationForm>(p => p.Add(f => f.Submit, _ => Task.FromResult<NotificationChannelResult?>(new NotificationChannelResult(
            IssuanceChangeStatus.Invalid, Errors: [new ConfigError("/secret", NotificationErrorCodes.UrlInvalid, "the secret must be a Discord webhook URL")]))));

        form.Find(".pdk-notify-kind-discord").Click();
        form.Find("[data-field=secret]").Change("https://example.com/x");
        await form.InvokeAsync(() => form.Find(".pdk-notify-submit").Click());

        Assert.Contains("The secret must be a Discord webhook URL.", form.Markup, StringComparison.Ordinal);
        Assert.Contains("Enter the secret again.", form.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("https://example.com/x", form.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ChannelsAreDescribedByWhatTheyAre()
    {
        var ntfy = Existing();
        var email = new NotificationChannelSummary(
            NotificationChannelId.New(), NotificationChannelKind.Email, "mail", true, [NotificationEventKind.UpstreamDown, NotificationEventKind.UpstreamUp],
            ["a@example.com", "b@example.com", "c@example.com"], false, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, SmtpHost: "smtp.example.com");

        Assert.Equal("ntfy.sh/home", NotificationDisplay.Target(ntfy));
        Assert.Equal("a@example.com, b@example.com +1 via smtp.example.com", NotificationDisplay.Target(email));
        Assert.Equal("Signed JSON webhook", NotificationDisplay.Target(Existing(NotificationChannelKind.Webhook)));
        Assert.Equal("2 events", NotificationDisplay.EventCount(2));
        Assert.Equal("1 event", NotificationDisplay.EventCount(1));
        Assert.All(Enum.GetValues<NotificationEventKind>().Where(k => k != NotificationEventKind.Test), kind =>
            Assert.Contains(NotificationDisplay.Groups, g => g.Events.Contains(kind)));
    }
}
