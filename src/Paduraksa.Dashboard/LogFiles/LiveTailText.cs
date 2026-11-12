using System.Globalization;

using Paduraksa.Contracts.Logging;
using Paduraksa.Dashboard.Config;

namespace Paduraksa.Dashboard.LogFiles;

/// <summary>How the live tail words and colours what it shows, and how it reads the status field.</summary>
public static class LiveTailText
{
    /// <summary>Colours a status by its class; a request the gate dropped without an answer has none.</summary>
    public static Tone ToneOf(int? status) => status switch
    {
        >= 500 => Tone.Error,
        >= 400 => Tone.Warning,
        >= 300 => Tone.Info,
        >= 200 => Tone.Success,
        _ => Tone.Default,
    };

    public static string StatusText(int? status) => status is { } code ? code.ToString(CultureInfo.InvariantCulture) : "-";

    /// <summary>Milliseconds the way a person reads them: <c>0.42 ms</c>, <c>38 ms</c>, <c>1.2 s</c>.</summary>
    public static string Duration(double? milliseconds) => milliseconds switch
    {
        null => "-",
        < 1 => (milliseconds.Value).ToString("0.##", CultureInfo.InvariantCulture) + " ms",
        < 100 => milliseconds.Value.ToString("0.#", CultureInfo.InvariantCulture) + " ms",
        < 1000 => milliseconds.Value.ToString("0", CultureInfo.InvariantCulture) + " ms",
        _ => (milliseconds.Value / 1000).ToString("0.##", CultureInfo.InvariantCulture) + " s",
    };

    /// <summary>The upstream's address without its scheme and trailing slash, which every line would repeat.</summary>
    public static string UpstreamText(string? upstream)
    {
        if (string.IsNullOrEmpty(upstream))
        {
            return "-";
        }

        var text = upstream.AsSpan();
        var scheme = text.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            text = text[(scheme + 3)..];
        }

        return text.TrimEnd('/').ToString();
    }

    /// <summary>The path and query as one string, the way the request line has them.</summary>
    public static string Target(LogTailEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return (entry.Path ?? "-") + entry.Query;
    }

    /// <summary>
    /// The status field: a status (<c>404</c>) or a class (<c>5xx</c>, or just <c>5</c>). Empty is no filter; anything else is
    /// <see langword="false"/>.
    /// </summary>
    public static bool TryParseStatus(string? text, out int? statusClass, out int? status)
    {
        statusClass = null;
        status = null;
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return true;
        }

        if (trimmed.Length is 1 or 3 && char.IsAsciiDigit(trimmed[0]) && (trimmed.Length == 1 || trimmed.EndsWith("xx", StringComparison.OrdinalIgnoreCase)))
        {
            var digit = trimmed[0] - '0';
            if (digit is >= 1 and <= 5)
            {
                statusClass = digit;
                return true;
            }

            return false;
        }

        if (int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var exact) && exact is >= 100 and <= 599)
        {
            status = exact;
            return true;
        }

        return false;
    }

    /// <summary>The link to the simulator, prefilled with what this request was (host, method, path, client IP).</summary>
    public static string SimulatorLink(LogTailEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var url = "https://" + (string.IsNullOrEmpty(entry.Host) ? "localhost" : entry.Host) + (entry.Path is { Length: > 0 } path ? path : "/") + entry.Query;
        var link = DashboardRoutes.Simulator + "?url=" + Uri.EscapeDataString(url);
        if (!string.IsNullOrEmpty(entry.ClientAddress))
        {
            link += "&clientIp=" + Uri.EscapeDataString(entry.ClientAddress);
        }

        if (!string.IsNullOrEmpty(entry.Method))
        {
            link += "&method=" + Uri.EscapeDataString(entry.Method);
        }

        return link;
    }
}
