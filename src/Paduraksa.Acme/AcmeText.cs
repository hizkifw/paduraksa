using System.Text;

namespace Paduraksa.Acme;

/// <summary>Helpers for putting CA-supplied text into messages and logs.</summary>
internal static class AcmeText
{
    /// <summary>
    /// <paramref name="text"/> with control characters removed (no log or console injection through a CA's problem detail)
    /// and cut to <paramref name="maxLength"/> characters, marked with an ellipsis when cut.
    /// </summary>
    public static string Sanitize(string? text, int maxLength = 512)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(text.Length, maxLength + 1));
        foreach (var c in text)
        {
            if (char.IsControl(c))
            {
                continue;
            }

            if (builder.Length >= maxLength)
            {
                builder.Append("...");
                break;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>
    /// A URL as it is shown in a message or log: its original text without query or fragment. Safe for a relative URI, which
    /// has no <see cref="Uri.GetLeftPart"/>.
    /// </summary>
    public static string Url(Uri url)
    {
        var text = url.OriginalString;
        var cut = text.IndexOfAny(['?', '#']);
        return Sanitize(cut < 0 ? text : text[..cut], 256);
    }

    /// <summary>Whether <paramref name="url"/> is an absolute http or https URL: the only kind kept for display-only links.</summary>
    public static bool IsDisplayUrl(Uri? url) =>
        url is { IsAbsoluteUri: true } && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp);
}
