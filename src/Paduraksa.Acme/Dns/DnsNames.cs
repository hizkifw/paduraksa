using System.Text;

namespace Paduraksa.Acme.Dns;

/// <summary>Name and TXT-text helpers the providers share.</summary>
internal static class DnsNames
{
    /// <summary>The longest TXT character-string (RFC 1035): ACME's values are 43 characters, so a longer one is a caller's bug.</summary>
    public const int MaxTxtStringLength = 255;

    /// <summary>
    /// The record name in comparison form: lowercase, no trailing dot, labels of letters, digits, hyphens and underscores.
    /// </summary>
    /// <exception cref="ArgumentException">The name is not a plain DNS name.</exception>
    public static string Normalize(string recordName)
    {
        ArgumentNullException.ThrowIfNull(recordName);
        var name = recordName.Trim().TrimEnd('.').ToLowerInvariant();
        if (name.Length is 0 or > 253)
        {
            throw new ArgumentException("A record name is 1 to 253 characters.", nameof(recordName));
        }

        foreach (var label in name.Split('.'))
        {
            if (label.Length is 0 or > 63 || !label.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                throw new ArgumentException("A record name is dot-separated labels of letters, digits, hyphens and underscores.", nameof(recordName));
            }
        }

        return name;
    }

    /// <summary>The names a zone for <paramref name="name"/> may have, longest first, down to two labels (<c>_acme-challenge.a.example.com</c> gives itself, <c>a.example.com</c>, <c>example.com</c>).</summary>
    public static IEnumerable<string> ZoneCandidates(string name)
    {
        var labels = name.Split('.');
        for (var i = 0; i <= labels.Length - 2; i++)
        {
            yield return string.Join('.', labels[i..]);
        }
    }

    /// <summary>A value that can be written as one TXT character-string without escaping beyond quotes: printable ASCII, 1 to 255 characters.</summary>
    /// <exception cref="ArgumentException">The value cannot be a challenge value.</exception>
    public static string RequireTxtValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length is 0 or > MaxTxtStringLength || !value.All(c => c is >= ' ' and <= '~'))
        {
            throw new ArgumentException("A TXT value is 1 to 255 printable ASCII characters.", nameof(value));
        }

        return value;
    }

    /// <summary>The presentation form of a TXT value, <c>"value"</c>, with <c>\</c> and <c>"</c> escaped.</summary>
    public static string QuoteTxt(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    /// <summary>
    /// The text of a TXT record as a provider hands it back: either bare, or one or more quoted strings (<c>"a" "b"</c>, joined
    /// as DNS does) with <c>\"</c> and <c>\\</c> unescaped. Anything that is not that shape is returned as it is.
    /// </summary>
    public static string UnquoteTxt(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var text = content.Trim();
        if (text.Length < 2 || text[0] != '"')
        {
            return text;
        }

        StringBuilder joined = new(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == ' ')
            {
                i++;
                continue;
            }

            if (text[i] != '"')
            {
                return text;
            }

            i++;
            var closed = false;
            while (i < text.Length)
            {
                var c = text[i++];
                if (c == '\\' && i < text.Length)
                {
                    joined.Append(text[i++]);
                }
                else if (c == '"')
                {
                    closed = true;
                    break;
                }
                else
                {
                    joined.Append(c);
                }
            }

            if (!closed)
            {
                return text;
            }
        }

        return joined.ToString();
    }
}
