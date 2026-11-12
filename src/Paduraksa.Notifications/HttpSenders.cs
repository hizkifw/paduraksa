using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;

namespace Paduraksa.Notifications;

/// <summary>The HTTP client every webhook-style sender shares: no redirects (a secret URL must not be followed elsewhere), bounded waits.</summary>
public static class NotificationHttp
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    public static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    };
}

/// <summary>
/// What the HTTP senders share: reading the channel's secret through the vault's one capability, sending, and turning every failure
/// into a fixed line that never carries a URL, a token or the server's answer.
/// </summary>
internal abstract class HttpSender(INotificationSecretSource secrets, HttpClient http) : INotificationSender
{
    public abstract NotificationChannelKind Kind { get; }

    public abstract Task SendAsync(ChannelTarget channel, NotificationMessage message, CancellationToken cancellationToken);

    /// <summary>Runs <paramref name="use"/> with the secret's text; the vault zeroes its copy when this returns.</summary>
    protected async Task WithSecretAsync(SecretId? id, string what, Func<string, Task> use, CancellationToken cancellationToken)
    {
        if (id is not { } secret)
        {
            throw new NotificationDeliveryException($"The channel has no {what}.", transient: false);
        }

        var found = await secrets.UseAsync(secret, async (value, token) => await use(Encoding.UTF8.GetString(value.Span)).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        if (!found)
        {
            throw new NotificationDeliveryException($"The {what} is missing from the vault.", transient: false);
        }
    }

    protected static ByteArrayContent Json(byte[] body)
    {
        ByteArrayContent content = new(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return content;
    }

    protected async Task SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Paduraksa", "1"));
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(NotificationHttp.RequestTimeout);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var code = (int)response.StatusCode;
            if (code is >= 200 and < 300)
            {
                return;
            }

            throw code switch
            {
                401 or 403 => new NotificationDeliveryException($"The server refused the credentials (HTTP {code.ToString(CultureInfo.InvariantCulture)}).", transient: false),
                408 or 425 or 429 or >= 500 => new NotificationDeliveryException($"The server answered HTTP {code.ToString(CultureInfo.InvariantCulture)}; will retry.", transient: true),
                _ => new NotificationDeliveryException($"The server answered HTTP {code.ToString(CultureInfo.InvariantCulture)}.", transient: false),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new NotificationDeliveryException("The server did not answer in time.", transient: true);
        }
        catch (HttpRequestException ex)
        {
            throw new NotificationDeliveryException(
                ex.HttpRequestError == HttpRequestError.SecureConnectionError ? "The TLS handshake with the server failed." : "Could not connect to the server.", transient: true);
        }
    }

    protected static Uri HttpUrl(string? text, string what)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new NotificationDeliveryException($"The {what} is not a valid http(s) URL.", transient: false);
        }

        return uri;
    }
}

internal sealed class NtfySender(INotificationSecretSource secrets, HttpClient http) : HttpSender(secrets, http)
{
    public override NotificationChannelKind Kind => NotificationChannelKind.Ntfy;

    public override async Task SendAsync(ChannelTarget channel, NotificationMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(message);
        var url = HttpUrl(channel.ServerUrl?.TrimEnd('/') + "/", "ntfy server URL");
        var body = NotificationFormats.Ntfy(channel.Topic ?? string.Empty, message);

        async Task Send(string? token)
        {
            using HttpRequestMessage request = new(HttpMethod.Post, url) { Content = Json(body) };
            if (token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            await SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (channel.Secret is null)
        {
            await Send(null).ConfigureAwait(false);
            return;
        }

        await WithSecretAsync(channel.Secret, "access token", token => Send(token.Trim()), cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class GotifySender(INotificationSecretSource secrets, HttpClient http) : HttpSender(secrets, http)
{
    public override NotificationChannelKind Kind => NotificationChannelKind.Gotify;

    public override Task SendAsync(ChannelTarget channel, NotificationMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(message);
        var url = HttpUrl(channel.ServerUrl?.TrimEnd('/') + "/message", "Gotify server URL");
        var body = NotificationFormats.Gotify(message);
        return WithSecretAsync(channel.Secret, "application token", async token =>
        {
            using HttpRequestMessage request = new(HttpMethod.Post, url) { Content = Json(body) };
            request.Headers.TryAddWithoutValidation("X-Gotify-Key", token.Trim());
            await SendAsync(request, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }
}

internal sealed class DiscordSender(INotificationSecretSource secrets, HttpClient http) : HttpSender(secrets, http)
{
    public override NotificationChannelKind Kind => NotificationChannelKind.Discord;

    public override Task SendAsync(ChannelTarget channel, NotificationMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(message);
        var body = NotificationFormats.Discord(message);
        return WithSecretAsync(channel.Secret, "webhook URL", async text =>
        {
            using HttpRequestMessage request = new(HttpMethod.Post, HttpUrl(text.Trim(), "webhook URL")) { Content = Json(body) };
            await SendAsync(request, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }
}

/// <summary>
/// The generic JSON webhook. With a signing key each body carries <c>X-Paduraksa-Timestamp</c> and
/// <c>X-Paduraksa-Signature: sha256=&lt;hex&gt;</c>, the HMAC-SHA-256 of <c>"&lt;timestamp&gt;.&lt;body&gt;"</c>, so a receiver can check who sent it and refuse a replay.
/// A message from the outbox also carries <c>X-Paduraksa-Event-Id</c> (and <c>eventId</c> in the body), the same on every resend: delivery is at-least-once.
/// </summary>
internal sealed class WebhookSender(INotificationSecretSource secrets, IMacKeySource macKeys, HttpClient http, TimeProvider time) : HttpSender(secrets, http)
{
    public override NotificationChannelKind Kind => NotificationChannelKind.Webhook;

    public override async Task SendAsync(ChannelTarget channel, NotificationMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(message);
        var body = NotificationFormats.Webhook(message);
        var stamp = time.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        string? signature = null;
        if (channel.SigningSecret is { } signing)
        {
            var key = await macKeys.OpenAsync(signing, SecretKind.HmacSigningKey, cancellationToken).ConfigureAwait(false)
                ?? throw new NotificationDeliveryException("The signing key is missing from the vault.", transient: false);
            var signed = Encoding.UTF8.GetBytes(stamp + ".").Concat(body).ToArray();
            var tag = new byte[key.TagLength];
            var written = key.Sign(signed, tag);
            signature = "sha256=" + Convert.ToHexStringLower(tag.AsSpan(0, written));
        }

        await WithSecretAsync(channel.Secret, "webhook URL", async text =>
        {
            using HttpRequestMessage request = new(HttpMethod.Post, HttpUrl(text.Trim(), "webhook URL")) { Content = Json(body) };
            request.Headers.TryAddWithoutValidation("X-Paduraksa-Event", message.Kind.ToName());
            if (message.EventId is not null)
            {
                request.Headers.TryAddWithoutValidation("X-Paduraksa-Event-Id", message.EventId);
            }

            request.Headers.TryAddWithoutValidation("X-Paduraksa-Timestamp", stamp);
            if (signature is not null)
            {
                request.Headers.TryAddWithoutValidation("X-Paduraksa-Signature", signature);
            }

            await SendAsync(request, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }
}
