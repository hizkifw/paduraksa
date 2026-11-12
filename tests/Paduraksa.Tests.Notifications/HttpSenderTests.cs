using System.Security.Cryptography;
using System.Text;

using Paduraksa.Core.Model;
using Paduraksa.Notifications;
using Paduraksa.Tests.Notifications.Fakes;

namespace Paduraksa.Tests.Notifications;

/// <summary>Each HTTP sender against a fake server of its service: what it posts, where the secret goes, and how failures read.</summary>
public sealed class HttpSenderTests : IAsyncLifetime
{
    private static readonly DateTimeOffset At = new(2026, 11, 12, 14, 2, 30, TimeSpan.Zero);
    private static readonly NotificationMessage Message = new(
        NotificationEventKind.UpstreamUp, "Upstream media is back", "media has a healthy destination again.", NotificationSeverity.Good, At, "https://admin.example.com/upstreams", [new("upstreamId", "ups_1")]);

    private readonly FakeSecrets _secrets = new();
    private readonly FakeMacKeys _macKeys = new();
    private FakeEndpoint _server = null!;
    private NotificationSenders _senders = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _server = await FakeEndpoint.StartAsync();
        _senders = new NotificationSenders(_secrets, _macKeys, new FixedTime(At));
    }

    public async ValueTask DisposeAsync()
    {
        _senders.Dispose();
        await _server.DisposeAsync();
    }

    private Task Send(ChannelTarget channel) => _senders.Find(channel.Kind)!.SendAsync(channel, Message, Ct);

    private static ChannelTarget Channel(NotificationChannelKind kind, string? serverUrl = null, string? topic = null, SecretId? secret = null, SecretId? signing = null) =>
        new(NotificationChannelId.New(), kind, serverUrl, topic, null, secret, signing);

    [Fact]
    public async Task NtfyPostsJsonToTheServerRootWithTheTokenAsABearer()
    {
        var token = _secrets.Add("tk_secret_token");

        await Send(Channel(NotificationChannelKind.Ntfy, _server.Address + "ntfy/", "homelab", token));

        var request = Assert.Single(_server.Requests);
        Assert.Equal("/ntfy/", request.Path);
        Assert.Equal("Bearer tk_secret_token", request.Headers["Authorization"]);
        Assert.StartsWith("application/json", request.Headers["Content-Type"], StringComparison.Ordinal);
        Assert.Equal(Encoding.UTF8.GetString(NotificationFormats.Ntfy("homelab", Message)), request.Text);
        Assert.DoesNotContain("tk_secret_token", request.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NtfyWithoutATokenSendsNoAuthorization()
    {
        await Send(Channel(NotificationChannelKind.Ntfy, _server.Address.ToString(), "homelab"));

        Assert.False(Assert.Single(_server.Requests).Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task GotifyPostsToMessageWithTheApplicationTokenInItsHeaderNotTheUrl()
    {
        var token = _secrets.Add("app-token-123");

        await Send(Channel(NotificationChannelKind.Gotify, _server.Address.ToString(), secret: token));

        var request = Assert.Single(_server.Requests);
        Assert.Equal("/message", request.Path);
        Assert.Equal("app-token-123", request.Headers["X-Gotify-Key"]);
        Assert.Equal(Encoding.UTF8.GetString(NotificationFormats.Gotify(Message)), request.Text);
    }

    [Fact]
    public async Task DiscordPostsToTheSecretWebhookUrl()
    {
        var url = _secrets.Add(_server.Address + "api/webhooks/123/the-token");

        await Send(Channel(NotificationChannelKind.Discord, secret: url));

        var request = Assert.Single(_server.Requests);
        Assert.Equal("/api/webhooks/123/the-token", request.Path);
        Assert.Equal(Encoding.UTF8.GetString(NotificationFormats.Discord(Message)), request.Text);
    }

    [Fact]
    public async Task AWebhookIsSignedOverTheTimestampAndTheBodyWhenItHasAKey()
    {
        var url = _secrets.Add(_server.Address + "hook?x=1");
        var key = _macKeys.Add("shared-signing-key");

        await Send(Channel(NotificationChannelKind.Webhook, secret: url, signing: key));

        var request = Assert.Single(_server.Requests);
        Assert.Equal("/hook?x=1", request.Path);
        Assert.Equal("upstream.up", request.Headers["X-Paduraksa-Event"]);
        var stamp = request.Headers["X-Paduraksa-Timestamp"];
        Assert.Equal(At.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), stamp);
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes("shared-signing-key"), Encoding.UTF8.GetBytes(stamp + ".").Concat(request.Body).ToArray());
        Assert.Equal("sha256=" + Convert.ToHexStringLower(expected), request.Headers["X-Paduraksa-Signature"]);
        Assert.Equal(Encoding.UTF8.GetString(NotificationFormats.Webhook(Message)), request.Text);
    }

    [Fact]
    public async Task AWebhookWithoutAKeyCarriesNoSignature()
    {
        await Send(Channel(NotificationChannelKind.Webhook, secret: _secrets.Add(_server.Address + "hook")));

        var request = Assert.Single(_server.Requests);
        Assert.False(request.Headers.ContainsKey("X-Paduraksa-Signature"));
        Assert.Equal("upstream.up", request.Headers["X-Paduraksa-Event"]);
    }

    [Theory]
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(429, true)]
    [InlineData(408, true)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(404, false)]
    [InlineData(400, false)]
    public async Task AFailedAnswerIsAShortLineAndSaysWhetherARetryCanHelp(int status, bool transient)
    {
        _server.Answer(status);
        var url = _secrets.Add(_server.Address + "api/webhooks/1/leaky-token");

        var failure = await Assert.ThrowsAsync<NotificationDeliveryException>(() => Send(Channel(NotificationChannelKind.Discord, secret: url)));

        Assert.Equal(transient, failure.Transient);
        Assert.Contains(status.ToString(System.Globalization.CultureInfo.InvariantCulture), failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("leaky-token", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARedirectIsNotFollowedSoASecretUrlCannotBeSentElsewhere()
    {
        _server.Answer(302, "https://elsewhere.example/");
        var url = _secrets.Add(_server.Address + "hook");

        var failure = await Assert.ThrowsAsync<NotificationDeliveryException>(() => Send(Channel(NotificationChannelKind.Webhook, secret: url)));

        Assert.False(failure.Transient);
        Assert.Single(_server.Requests);
    }

    [Fact]
    public async Task ANoServerIsAConnectionFailureThatNeverNamesTheAddress()
    {
        var port = ((System.Net.IPEndPoint)DeadEndpoint()).Port;
        var url = _secrets.Add($"http://127.0.0.1:{port}/api/webhooks/1/leaky-token");

        var failure = await Assert.ThrowsAsync<NotificationDeliveryException>(() => Send(Channel(NotificationChannelKind.Discord, secret: url)));

        Assert.True(failure.Transient);
        Assert.Equal("Could not connect to the server.", failure.Message);
    }

    [Fact]
    public async Task AMissingOrInvalidSecretIsAPermanentFailureWithoutARequest()
    {
        var invalid = _secrets.Add("not a url");

        var none = await Assert.ThrowsAsync<NotificationDeliveryException>(() => Send(Channel(NotificationChannelKind.Discord)));
        var gone = await Assert.ThrowsAsync<NotificationDeliveryException>(() => Send(Channel(NotificationChannelKind.Discord, secret: SecretId.New())));
        var bad = await Assert.ThrowsAsync<NotificationDeliveryException>(() => Send(Channel(NotificationChannelKind.Webhook, secret: invalid)));
        var noKey = await Assert.ThrowsAsync<NotificationDeliveryException>(() =>
            Send(Channel(NotificationChannelKind.Webhook, secret: _secrets.Add(_server.Address + "h"), signing: SecretId.New())));

        Assert.All(new[] { none, gone, bad, noKey }, f => Assert.False(f.Transient));
        Assert.DoesNotContain("not a url", bad.Message, StringComparison.Ordinal);
        Assert.Empty(_server.Requests);
    }

    private static System.Net.EndPoint DeadEndpoint()
    {
        using System.Net.Sockets.TcpListener listener = new(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return listener.LocalEndpoint;
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
