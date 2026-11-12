using Paduraksa.Core.Model;
using Paduraksa.Notifications;

namespace Paduraksa.Tests.Notifications;

/// <summary>The wire formats, pinned as text. A host named to break out of a string, a mention or a header must come out inert.</summary>
public sealed class FormatTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 12, 14, 2, 30, TimeSpan.Zero);

    private static NotificationMessage Down(string upstream = "media", string? link = "https://admin.example.com/upstreams") => new(
        NotificationEventKind.UpstreamDown,
        $"Upstream {upstream} is down",
        $"All 2 destinations of {upstream} are unhealthy since 14:02 UTC: 10.0.0.5:8096 (HTTP 503), 10.0.0.6:8096 (timed out).\nCheck the service, then watch the Upstreams page.",
        NotificationSeverity.Critical,
        At,
        link,
        [new("upstreamId", "ups_1"), new("destinations", "2")]);

    [Fact]
    public void NtfyIsAJsonPublishWithPriorityTagAndClickLink() =>
        Assert.Equal(
            """{"topic":"homelab","title":"Upstream media is down","message":"All 2 destinations of media are unhealthy since 14:02 UTC: 10.0.0.5:8096 (HTTP 503), 10.0.0.6:8096 (timed out).\nCheck the service, then watch the Upstreams page.","priority":5,"tags":["rotating_light"],"click":"https://admin.example.com/upstreams"}""",
            System.Text.Encoding.UTF8.GetString(NotificationFormats.Ntfy("homelab", Down())));

    [Fact]
    public void GotifyCarriesTheLinkAsAClickAction() =>
        Assert.Equal(
            """{"title":"Upstream media is down","message":"All 2 destinations of media are unhealthy since 14:02 UTC: 10.0.0.5:8096 (HTTP 503), 10.0.0.6:8096 (timed out).\nCheck the service, then watch the Upstreams page.","priority":8,"extras":{"client::notification":{"click":{"url":"https://admin.example.com/upstreams"}}}}""",
            System.Text.Encoding.UTF8.GetString(NotificationFormats.Gotify(Down())));

    [Fact]
    public void DiscordIsOneEmbedThatMentionsNobodyWithItsMarkdownEscaped() =>
        Assert.Equal(
            """{"username":"Paduraksa","allowed_mentions":{"parse":[]},"embeds":[{"title":"Upstream media is down","description":"All 2 destinations of media are unhealthy since 14:02 UTC: 10.0.0.5:8096 \\(HTTP 503\\), 10.0.0.6:8096 \\(timed out\\).\nCheck the service, then watch the Upstreams page.","color":14237503,"timestamp":"2026-11-12T14:02:30Z","url":"https://admin.example.com/upstreams"}]}""",
            System.Text.Encoding.UTF8.GetString(NotificationFormats.Discord(Down())));

    [Fact]
    public void TheWebhookBodyHasTheEventSeverityTimeAndData() =>
        Assert.Equal(
            """{"event":"upstream.down","severity":"critical","title":"Upstream media is down","message":"All 2 destinations of media are unhealthy since 14:02 UTC: 10.0.0.5:8096 (HTTP 503), 10.0.0.6:8096 (timed out).\nCheck the service, then watch the Upstreams page.","time":"2026-11-12T14:02:30Z","link":"https://admin.example.com/upstreams","data":{"upstreamId":"ups_1","destinations":"2"}}""",
            System.Text.Encoding.UTF8.GetString(NotificationFormats.Webhook(Down())));

    [Fact]
    public void TheWebhookBodyCarriesTheEventIdWhenThereIsOneAndOtherFormatsDoNot()
    {
        var message = Down() with { EventId = "nev_example" };

        Assert.StartsWith("""{"event":"upstream.down","eventId":"nev_example","severity":""", System.Text.Encoding.UTF8.GetString(NotificationFormats.Webhook(message)), StringComparison.Ordinal);
        Assert.DoesNotContain("nev_example", System.Text.Encoding.UTF8.GetString(NotificationFormats.Discord(message)), StringComparison.Ordinal);
        Assert.DoesNotContain("nev_example", System.Text.Encoding.UTF8.GetString(NotificationFormats.Ntfy("t", message)), StringComparison.Ordinal);
        Assert.DoesNotContain("nev_example", NotificationFormats.MailBody(message), StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAKnownAdminUrlThereIsNoLinkAnywhere()
    {
        var message = Down(link: null);
        Assert.DoesNotContain("click", System.Text.Encoding.UTF8.GetString(NotificationFormats.Ntfy("t", message)), StringComparison.Ordinal);
        Assert.DoesNotContain("extras", System.Text.Encoding.UTF8.GetString(NotificationFormats.Gotify(message)), StringComparison.Ordinal);
        Assert.DoesNotContain("\"url\"", System.Text.Encoding.UTF8.GetString(NotificationFormats.Discord(message)), StringComparison.Ordinal);
        Assert.DoesNotContain("\"link\"", System.Text.Encoding.UTF8.GetString(NotificationFormats.Webhook(message)), StringComparison.Ordinal);
        Assert.DoesNotContain("https://", NotificationFormats.MailBody(message), StringComparison.Ordinal);
    }

    [Fact]
    public void AHostNameThatTriesToBreakOutStaysInsideItsString()
    {
        const string Hostile = "evil\"},\"priority\":1,\"x\":{\"a\":\"\r\nBcc: attacker@example.com\n@everyone [click](https://evil.example) *bold* `code`";
        var message = Down(Hostile);

        foreach (var body in new[] { NotificationFormats.Ntfy("t", message), NotificationFormats.Gotify(message), NotificationFormats.Discord(message), NotificationFormats.Webhook(message) })
        {
            using var json = System.Text.Json.JsonDocument.Parse(body);
            Assert.False(json.RootElement.TryGetProperty("x", out _));
        }

        using var ntfy = System.Text.Json.JsonDocument.Parse(NotificationFormats.Ntfy("t", message));
        Assert.Equal(5, ntfy.RootElement.GetProperty("priority").GetInt32());
        Assert.DoesNotContain('\n', ntfy.RootElement.GetProperty("title").GetString()!);

        using var discord = System.Text.Json.JsonDocument.Parse(NotificationFormats.Discord(message));
        var embed = discord.RootElement.GetProperty("embeds")[0];
        var title = embed.GetProperty("title").GetString()!;
        Assert.Contains("\\@everyone", title, StringComparison.Ordinal);
        Assert.Contains("\\[click\\]\\(https", title, StringComparison.Ordinal);
        Assert.Contains("\\*bold\\*", title, StringComparison.Ordinal);
        Assert.Empty(discord.RootElement.GetProperty("allowed_mentions").GetProperty("parse").EnumerateArray());
    }

    [Fact]
    public void AMailSubjectIsOneCleanLineAndTheBodyIsPlainTextWithCrlf()
    {
        var message = Down("a\r\nBcc: attacker@example.com\tb");

        Assert.Equal("[Paduraksa] Upstream a Bcc: attacker@example.com b is down", NotificationFormats.Subject(message));
        Assert.Equal(
            "All 2 destinations of a\r\nBcc: attacker@example.com b are unhealthy since 14:02 UTC: 10.0.0.5:8096 (HTTP 503), 10.0.0.6:8096 (timed out).\r\n"
            + "Check the service, then watch the Upstreams page.\r\n\r\nhttps://admin.example.com/upstreams\r\n\r\nSent by Paduraksa at 2026-11-12 14:02:30 UTC\r\n",
            NotificationFormats.MailBody(message));
    }

    [Fact]
    public void LongTextIsCutToWhatEachServiceAccepts()
    {
        var message = Down(new string('x', 5000));

        using var discord = System.Text.Json.JsonDocument.Parse(NotificationFormats.Discord(message with { Body = new string('y', 9000) }));
        var embed = discord.RootElement.GetProperty("embeds")[0];
        Assert.Equal(256, embed.GetProperty("title").GetString()!.Length);
        Assert.Equal(4000, embed.GetProperty("description").GetString()!.Length);
        Assert.True(NotificationFormats.Subject(message).Length <= 212);
    }
}
