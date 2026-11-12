using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;

namespace Paduraksa.Notifications;

/// <summary>
/// Email over SMTP with MailKit (Microsoft discourages <c>System.Net.Mail.SmtpClient</c> and it has no modern TLS or SASL story). With STARTTLS
/// it is required, not opportunistic: a server that doesn't offer it is refused rather than sent the password in clear. The certificate is
/// checked against the system roots like any other connection. <see cref="SmtpSecurity.None"/> sends in clear by definition, which is why the
/// channel validator refuses it together with a login or password (a trusted-network relay takes neither); this sender does not enforce that itself.
/// </summary>
internal sealed class EmailSender(INotificationSecretSource secrets, RemoteCertificateValidationCallback? certificateValidation = null) : INotificationSender
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public NotificationChannelKind Kind => NotificationChannelKind.Email;

    public async Task SendAsync(ChannelTarget channel, NotificationMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(message);
        var smtp = channel.Smtp ?? throw new NotificationDeliveryException("The channel has no mail settings.", transient: false);
        MimeMessage mail;
        try
        {
            mail = Compose(smtp, message);
        }
        catch (ParseException)
        {
            throw new NotificationDeliveryException("A sender or recipient address is not valid.", transient: false);
        }

        if (smtp.Username is not null && channel.Secret is null)
        {
            throw new NotificationDeliveryException("The channel has a login but no password.", transient: false);
        }

        try
        {
            if (smtp.Username is { } username && channel.Secret is { } secret)
            {
                var found = await secrets.UseAsync(
                    secret,
                    async (value, token) => await DeliverAsync(smtp, mail, username, Encoding.UTF8.GetString(value.Span), token).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false);
                if (!found)
                {
                    throw new NotificationDeliveryException("The SMTP password is missing from the vault.", transient: false);
                }
            }
            else
            {
                await DeliverAsync(smtp, mail, null, null, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (NotificationDeliveryException)
        {
            throw;
        }
        catch (NotSupportedException)
        {
            throw new NotificationDeliveryException("The mail server does not offer STARTTLS; choose another security setting or fix the server.", transient: false);
        }
        catch (Exception ex) when (ex is System.Security.Authentication.AuthenticationException or SslHandshakeException)
        {
            throw new NotificationDeliveryException("The TLS handshake with the mail server failed.", transient: false);
        }
        catch (SmtpCommandException ex)
        {
            var permanent = (int)ex.StatusCode >= 500;
            throw new NotificationDeliveryException(
                $"The mail server refused the message ({(int)ex.StatusCode}){(permanent ? string.Empty : "; will retry")}.", transient: !permanent);
        }
        catch (Exception ex) when (ex is AuthenticationException or SaslException)
        {
            throw new NotificationDeliveryException("The mail server refused the login.", transient: false);
        }
        catch (Exception ex) when (ex is ProtocolException or ServiceNotConnectedException or ServiceNotAuthenticatedException)
        {
            throw new NotificationDeliveryException("The mail server did not accept the connection.", transient: true);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or TimeoutException)
        {
            throw new NotificationDeliveryException("Could not reach the mail server.", transient: true);
        }
    }

    private static MimeMessage Compose(SmtpTarget smtp, NotificationMessage message)
    {
        MimeMessage mail = new();
        mail.From.Add(MailboxAddress.Parse(smtp.From));
        foreach (var to in smtp.To)
        {
            mail.To.Add(MailboxAddress.Parse(to));
        }

        mail.Subject = NotificationFormats.Subject(message);
        mail.Date = message.At;
        mail.Headers.Add("Auto-Submitted", "auto-generated");
        mail.Body = new TextPart("plain") { Text = NotificationFormats.MailBody(message) };
        return mail;
    }

    private async Task DeliverAsync(SmtpTarget smtp, MimeMessage mail, string? username, string? password, CancellationToken cancellationToken)
    {
        using SmtpClient client = new() { Timeout = (int)Timeout.TotalMilliseconds };
        if (certificateValidation is not null)
        {
            client.ServerCertificateValidationCallback = certificateValidation;
        }

        var options = smtp.Security switch
        {
            SmtpSecurity.ImplicitTls => SecureSocketOptions.SslOnConnect,
            SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
            _ => SecureSocketOptions.None,
        };
        await client.ConnectAsync(smtp.Host, smtp.Port, options, cancellationToken).ConfigureAwait(false);
        if (username is not null && password is not null)
        {
            await client.AuthenticateAsync(username, password, cancellationToken).ConfigureAwait(false);
        }

        _ = await client.SendAsync(mail, cancellationToken).ConfigureAwait(false);
        await client.DisconnectAsync(quit: true, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>The senders of a build: ntfy, Gotify, Discord, webhook and email.</summary>
public sealed class NotificationSenders : INotificationSenders, IDisposable
{
    private readonly Dictionary<NotificationChannelKind, INotificationSender> _senders;
    private readonly HttpClient _http;

    public NotificationSenders(INotificationSecretSource secrets, IMacKeySource macKeys, TimeProvider time)
        : this(secrets, macKeys, time, NotificationHttp.CreateHandler(), certificateValidation: null)
    {
    }

    internal NotificationSenders(
        INotificationSecretSource secrets, IMacKeySource macKeys, TimeProvider time, HttpMessageHandler handler, RemoteCertificateValidationCallback? certificateValidation)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(macKeys);
        ArgumentNullException.ThrowIfNull(time);
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        List<INotificationSender> all =
        [
            new NtfySender(secrets, _http),
            new GotifySender(secrets, _http),
            new DiscordSender(secrets, _http),
            new WebhookSender(secrets, macKeys, _http, time),
            new EmailSender(secrets, certificateValidation),
        ];
        _senders = all.ToDictionary(s => s.Kind);
    }

    public INotificationSender? Find(NotificationChannelKind kind) => _senders.GetValueOrDefault(kind);

    public void Dispose() => _http.Dispose();
}
