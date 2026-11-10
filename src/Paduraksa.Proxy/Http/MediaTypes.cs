using System.Globalization;

using Microsoft.Extensions.Primitives;

namespace Paduraksa.Proxy.Http;

/// <summary>How the proxy reads a request's <c>Accept</c> header for its own answers.</summary>
internal static class MediaTypes
{
    /// <summary>
    /// Whether the header names <c>text/html</c>, as a browser's navigation does. Parsed from the header lines without
    /// allocating. Parameters don't change the media type, except the qvalue: <c>text/html;q=0</c> withdraws that entry,
    /// so a client that clipped the range (or an entry) gets no page. A wildcard alone is not <c>text/html</c>: curl's
    /// <c>*/*</c> keeps the plain answer.
    /// </summary>
    public static bool AcceptsHtml(StringValues accept)
    {
        for (var i = 0; i < accept.Count; i++)
        {
            if (accept[i] is not { } line)
            {
                continue;
            }

            var rest = line.AsSpan();
            while (!rest.IsEmpty)
            {
                var comma = rest.IndexOf(',');
                var entry = comma < 0 ? rest : rest[..comma];
                rest = comma < 0 ? default : rest[(comma + 1)..];
                if (IsHtml(entry))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsHtml(ReadOnlySpan<char> entry)
    {
        var semicolon = entry.IndexOf(';');
        var media = (semicolon < 0 ? entry : entry[..semicolon]).Trim();
        if (!media.Equals("text/html", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (semicolon < 0)
        {
            return true;
        }

        // Only the qvalue matters here; other parameters (level, charset) don't select the media type. A qvalue that
        // doesn't parse is treated as present, as browsers never send one and the entry is still text/html.
        var parameters = entry[(semicolon + 1)..];
        while (!parameters.IsEmpty)
        {
            var next = parameters.IndexOf(';');
            var parameter = next < 0 ? parameters : parameters[..next];
            parameters = next < 0 ? default : parameters[(next + 1)..];
            var equals = parameter.IndexOf('=');
            if (equals < 0 || !parameter[..equals].Trim().Equals("q", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (double.TryParse(parameter[(equals + 1)..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var weight)
                && weight == 0)
            {
                return false;
            }
        }

        return true;
    }
}
