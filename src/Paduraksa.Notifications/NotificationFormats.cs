using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Paduraksa.Core.Model;

namespace Paduraksa.Notifications;

/// <summary>
/// The wire formats, as pure functions so each can be pinned by a golden test. Names inside a message are a user's, so each medium's own
/// escaping is applied here and not by the caller: JSON strings are escaped by the writer, Discord's markdown is escaped for its renderer,
/// and a mail subject is one clean line.
/// </summary>
public static class NotificationFormats
{
    private const int NtfyTitleMax = 250;
    private const int DiscordTitleMax = 256;
    private const int DiscordDescriptionMax = 4000;

    /// <summary>The ntfy publish body (<c>POST /</c> with JSON, which keeps non-ASCII titles out of HTTP headers).</summary>
    public static byte[] Ntfy(string topic, NotificationMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Json(w =>
        {
            w.WriteString("topic", topic);
            w.WriteString("title", Clip(OneLine(message.Title), NtfyTitleMax));
            w.WriteString("message", Clean(message.Body));
            w.WriteNumber("priority", Priority(message.Severity));
            w.WriteStartArray("tags");
            w.WriteStringValue(Tag(message.Severity));
            w.WriteEndArray();
            if (message.Link is not null)
            {
                w.WriteString("click", message.Link);
            }
        });
    }

    /// <summary>The Gotify <c>POST /message</c> body.</summary>
    public static byte[] Gotify(NotificationMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Json(w =>
        {
            w.WriteString("title", Clip(OneLine(message.Title), NtfyTitleMax));
            w.WriteString("message", Clean(message.Body));
            w.WriteNumber("priority", GotifyPriority(message.Severity));
            if (message.Link is not null)
            {
                w.WriteStartObject("extras");
                w.WriteStartObject("client::notification");
                w.WriteStartObject("click");
                w.WriteString("url", message.Link);
                w.WriteEndObject();
                w.WriteEndObject();
                w.WriteEndObject();
            }
        });
    }

    /// <summary>
    /// The Discord webhook body: one embed, no mentions of anyone (<c>allowed_mentions.parse</c> is empty, so a host named
    /// <c>@everyone</c> pings nobody), markdown in names escaped.
    /// </summary>
    public static byte[] Discord(NotificationMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Json(w =>
        {
            w.WriteString("username", "Paduraksa");
            w.WriteStartObject("allowed_mentions");
            w.WriteStartArray("parse");
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteStartArray("embeds");
            w.WriteStartObject();
            w.WriteString("title", Clip(DiscordMarkdown(OneLine(message.Title)), DiscordTitleMax));
            w.WriteString("description", Clip(DiscordMarkdown(Clean(message.Body)), DiscordDescriptionMax));
            w.WriteNumber("color", Color(message.Severity));
            w.WriteString("timestamp", message.At.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            if (message.Link is not null)
            {
                w.WriteString("url", message.Link);
            }

            w.WriteEndObject();
            w.WriteEndArray();
        });
    }

    /// <summary>The generic webhook body.</summary>
    public static byte[] Webhook(NotificationMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Json(w =>
        {
            w.WriteString("event", message.Kind.ToName());
            w.WriteString("severity", SeverityName(message.Severity));
            w.WriteString("title", OneLine(message.Title));
            w.WriteString("message", Clean(message.Body));
            w.WriteString("time", message.At.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            if (message.Link is not null)
            {
                w.WriteString("link", message.Link);
            }

            w.WriteStartObject("data");
            foreach (var (key, value) in message.Data ?? [])
            {
                w.WriteString(key, value);
            }

            w.WriteEndObject();
        });
    }

    /// <summary>A mail's subject: the title on one line with the product in front, so a rule can file it.</summary>
    public static string Subject(NotificationMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return "[Paduraksa] " + Clip(OneLine(message.Title), 200);
    }

    /// <summary>A mail's text/plain body.</summary>
    public static string MailBody(NotificationMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        StringBuilder text = new();
        text.Append(Clean(message.Body)).Append('\n');
        if (message.Link is not null)
        {
            text.Append('\n').Append(message.Link).Append('\n');
        }

        text.Append("\nSent by Paduraksa at ").Append(message.At.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)).Append('\n');
        return text.ToString().Replace("\n", "\r\n", StringComparison.Ordinal);
    }

    /// <summary>One printable line: control characters (a newline in a host name) become spaces, runs of spaces collapse.</summary>
    public static string OneLine(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        StringBuilder line = new(text.Length);
        var space = false;
        foreach (var c in text)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c))
            {
                space = line.Length > 0;
                continue;
            }

            if (space)
            {
                line.Append(' ');
                space = false;
            }

            line.Append(c);
        }

        return line.ToString();
    }

    /// <summary>Text that may span lines: line breaks kept (as <c>\n</c>), tabs become spaces, every other control character is dropped.</summary>
    public static string Clean(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        StringBuilder clean = new(text.Length);
        foreach (var c in text)
        {
            if (c == '\n')
            {
                clean.Append(c);
            }
            else if (c == '\t')
            {
                clean.Append(' ');
            }
            else if (!char.IsControl(c) && c is not ('\u2028' or '\u2029'))
            {
                clean.Append(c);
            }
        }

        return clean.ToString();
    }

    /// <summary>Backslash-escapes the characters Discord renders: emphasis, code, spoilers, links, headings, quotes and mentions.</summary>
    public static string DiscordMarkdown(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        StringBuilder escaped = new(text.Length + 8);
        foreach (var c in text)
        {
            if (c is '\\' or '*' or '_' or '~' or '`' or '|' or '>' or '#' or '[' or ']' or '(' or ')' or '@' or '<')
            {
                escaped.Append('\\');
            }

            escaped.Append(c);
        }

        return escaped.ToString();
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : string.Concat(text.AsSpan(0, max - 1), "\u2026");

    private static int Priority(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Critical => 5,
        NotificationSeverity.Warning => 4,
        _ => 3,
    };

    private static int GotifyPriority(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Critical => 8,
        NotificationSeverity.Warning => 6,
        _ => 4,
    };

    private static string Tag(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Critical => "rotating_light",
        NotificationSeverity.Warning => "warning",
        NotificationSeverity.Good => "white_check_mark",
        _ => "information_source",
    };

    private static int Color(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Critical => 0xD93F3F,
        NotificationSeverity.Warning => 0xE0A030,
        NotificationSeverity.Good => 0x3FA35B,
        _ => 0x4A7DFF,
    };

    private static string SeverityName(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Critical => "critical",
        NotificationSeverity.Warning => "warning",
        NotificationSeverity.Good => "good",
        _ => "info",
    };

    private static byte[] Json(Action<Utf8JsonWriter> write)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }
}
