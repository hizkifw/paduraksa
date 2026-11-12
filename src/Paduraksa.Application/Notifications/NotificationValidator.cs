using System.Text.RegularExpressions;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Notifications;
using Paduraksa.Core.Model;

namespace Paduraksa.Application.Notifications;

/// <summary>What a channel request may say, per kind. Secrets are checked as text before they reach the vault.</summary>
internal static partial class NotificationValidator
{
    public const int MaxLabelLength = 256;
    public const int MaxSecretBytes = 4096;
    public const int MaxRecipients = 10;

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex TopicPattern();

    [GeneratedRegex(@"^[^@\s<>"",;:()\[\]\\]+@[A-Za-z0-9.-]+\.[A-Za-z0-9-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex AddressPattern();

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex HostPattern();

    /// <summary>What is wrong with a channel request. A new channel must carry its required secrets; a replacement may leave them out to keep the stored ones.</summary>
    public static List<ConfigError> Check(NotificationChannelBody body, bool isNew)
    {
        List<ConfigError> errors = [];
        if (!Enum.IsDefined(body.Kind))
        {
            errors.Add(new("/kind", NotificationErrorCodes.KindUndefined, $"unknown channel kind {body.Kind}"));
            return errors;
        }

        var label = (body.Label ?? string.Empty).Trim();
        if (label.Length == 0)
        {
            errors.Add(new("/label", NotificationErrorCodes.LabelRequired, "a channel needs a label"));
        }
        else if (label.Length > MaxLabelLength)
        {
            errors.Add(new("/label", NotificationErrorCodes.TextTooLong, $"the label is limited to {MaxLabelLength} characters"));
        }

        CheckEvents(body, errors);
        switch (body.Kind)
        {
            case NotificationChannelKind.Ntfy:
                CheckServer(body, errors);
                if (body.Topic is not { } topic || !TopicPattern().IsMatch(topic))
                {
                    errors.Add(new("/topic", NotificationErrorCodes.TopicInvalid, "the topic is 1 to 64 letters, digits, - or _"));
                }

                RejectSmtp(body, errors);
                RejectSigning(body, errors);
                break;
            case NotificationChannelKind.Gotify:
                CheckServer(body, errors);
                RequireSecret(body, isNew, errors, "the Gotify application token");
                RejectTopic(body, errors);
                RejectSmtp(body, errors);
                RejectSigning(body, errors);
                break;
            case NotificationChannelKind.Discord:
                RequireSecret(body, isNew, errors, "the Discord webhook URL");
                if (!string.IsNullOrEmpty(body.Secret) && !IsDiscordWebhook(body.Secret))
                {
                    errors.Add(new("/secret", NotificationErrorCodes.UrlInvalid, "the secret must be a Discord webhook URL (https://discord.com/api/webhooks/...)"));
                }

                RejectServer(body, errors);
                RejectSmtp(body, errors);
                RejectSigning(body, errors);
                break;
            case NotificationChannelKind.Webhook:
                RequireSecret(body, isNew, errors, "the webhook URL");
                if (!string.IsNullOrEmpty(body.Secret) && !IsHttpUrl(body.Secret, allowQuery: true))
                {
                    errors.Add(new("/secret", NotificationErrorCodes.UrlInvalid, "the secret must be an http or https URL"));
                }

                RejectServer(body, errors);
                RejectSmtp(body, errors);
                break;
            case NotificationChannelKind.Email:
                CheckEmail(body, isNew, errors);
                RejectServer(body, errors);
                RejectSigning(body, errors);
                break;
        }

        if (body.Secret is { } secret && System.Text.Encoding.UTF8.GetByteCount(secret) > MaxSecretBytes)
        {
            errors.Add(new("/secret", NotificationErrorCodes.SecretTooLong, $"the secret is limited to {MaxSecretBytes} bytes"));
        }

        if (body.SigningSecret is { } signing && System.Text.Encoding.UTF8.GetByteCount(signing) > MaxSecretBytes)
        {
            errors.Add(new("/signingSecret", NotificationErrorCodes.SecretTooLong, $"the signing secret is limited to {MaxSecretBytes} bytes"));
        }

        return errors;
    }

    private static void CheckEvents(NotificationChannelBody body, List<ConfigError> errors)
    {
        if (body.Events is not { Count: > 0 })
        {
            errors.Add(new("/events", NotificationErrorCodes.EventsRequired, "a channel needs at least one event"));
            return;
        }

        foreach (var kind in body.Events)
        {
            if (!Enum.IsDefined(kind) || kind == NotificationEventKind.Test)
            {
                errors.Add(new("/events", NotificationErrorCodes.EventUndefined, $"{kind} is not an event a channel can subscribe to"));
            }
        }
    }

    private static void CheckServer(NotificationChannelBody body, List<ConfigError> errors)
    {
        if (!IsHttpUrl(body.ServerUrl, allowQuery: false))
        {
            errors.Add(new("/serverUrl", NotificationErrorCodes.UrlInvalid, "the server URL must be an http or https URL without credentials, a query or a fragment"));
        }
    }

    private static void CheckEmail(NotificationChannelBody body, bool isNew, List<ConfigError> errors)
    {
        if (body.SmtpHost is not { } host || !HostPattern().IsMatch(host))
        {
            errors.Add(new("/smtpHost", NotificationErrorCodes.SmtpInvalid, "the SMTP server must be a host name or an address"));
        }

        if (body.SmtpPort is not (>= 1 and <= 65535))
        {
            errors.Add(new("/smtpPort", NotificationErrorCodes.SmtpInvalid, "the SMTP port is 1 to 65535"));
        }

        if (body.SmtpSecurity is { } security && !Enum.IsDefined(security))
        {
            errors.Add(new("/smtpSecurity", NotificationErrorCodes.SmtpInvalid, "unknown SMTP security"));
        }

        if (body.MailFrom is not { } from || !IsAddress(from))
        {
            errors.Add(new("/mailFrom", NotificationErrorCodes.AddressInvalid, "the sender must be an email address"));
        }

        if (body.MailTo is not { Count: > 0 })
        {
            errors.Add(new("/mailTo", NotificationErrorCodes.AddressInvalid, "at least one recipient is needed"));
        }
        else if (body.MailTo.Count > MaxRecipients || body.MailTo.Any(a => !IsAddress(a)))
        {
            errors.Add(new("/mailTo", NotificationErrorCodes.AddressInvalid, $"recipients must be email addresses, at most {MaxRecipients}"));
        }

        if (body.SmtpUsername is { } user && (user.Length is 0 or > 256 || user.Any(char.IsControl)))
        {
            errors.Add(new("/smtpUsername", NotificationErrorCodes.SmtpInvalid, "the login is 1 to 256 characters"));
        }

        // No TLS and a login would send the password in clear; a relay on a trusted network takes no login, so the pair is refused.
        if (body.SmtpSecurity == SmtpSecurity.None && (!string.IsNullOrEmpty(body.SmtpUsername) || !string.IsNullOrEmpty(body.Secret)))
        {
            errors.Add(new("/smtpSecurity", NotificationErrorCodes.SmtpInsecureLogin, "a login and password are not sent without TLS: choose STARTTLS or TLS, or remove the login and password"));
        }

        if (isNew && !string.IsNullOrEmpty(body.SmtpUsername) && string.IsNullOrEmpty(body.Secret))
        {
            errors.Add(new("/secret", NotificationErrorCodes.SecretRequired, "a login needs its password as the secret"));
        }

        RejectTopic(body, errors);
    }

    private static void RequireSecret(NotificationChannelBody body, bool isNew, List<ConfigError> errors, string what)
    {
        if ((isNew && string.IsNullOrWhiteSpace(body.Secret)) || (!isNew && body.Secret is { } given && string.IsNullOrWhiteSpace(given)))
        {
            errors.Add(new("/secret", NotificationErrorCodes.SecretRequired, $"the secret is {what}"));
        }
    }

    private static void RejectServer(NotificationChannelBody body, List<ConfigError> errors)
    {
        if (body.ServerUrl is not null)
        {
            errors.Add(new("/serverUrl", NotificationErrorCodes.FieldNotForKind, $"a {body.Kind} channel has no server URL"));
        }

        RejectTopic(body, errors);
    }

    private static void RejectTopic(NotificationChannelBody body, List<ConfigError> errors)
    {
        if (body.Topic is not null && body.Kind != NotificationChannelKind.Ntfy)
        {
            errors.Add(new("/topic", NotificationErrorCodes.FieldNotForKind, $"a {body.Kind} channel has no topic"));
        }
    }

    private static void RejectSmtp(NotificationChannelBody body, List<ConfigError> errors)
    {
        if (body.SmtpHost is not null || body.SmtpPort is not null || body.SmtpSecurity is not null || body.SmtpUsername is not null || body.MailFrom is not null || body.MailTo is { Count: > 0 })
        {
            errors.Add(new("/smtpHost", NotificationErrorCodes.FieldNotForKind, $"a {body.Kind} channel has no mail settings"));
        }
    }

    private static void RejectSigning(NotificationChannelBody body, List<ConfigError> errors)
    {
        if (body.SigningSecret is not null)
        {
            errors.Add(new("/signingSecret", NotificationErrorCodes.FieldNotForKind, $"a {body.Kind} channel has no signing key"));
        }
    }

    private static bool IsAddress(string? text) =>
        text is { Length: > 0 and <= 254 } && !text.Any(char.IsControl) && AddressPattern().IsMatch(text);

    private static bool IsHttpUrl(string? text, bool allowQuery) =>
        text is { Length: > 0 and <= 2048 } && !text.Any(char.IsControl) && Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && (allowQuery || uri.Query.Length == 0) && uri.Host.Length > 0;

    private static bool IsDiscordWebhook(string text) =>
        IsHttpUrl(text, allowQuery: true) && Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == "https"
        && uri.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.Ordinal);
}
