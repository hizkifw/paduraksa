using System.Globalization;

using Paduraksa.Contracts.Config;

namespace Paduraksa.Dashboard.Config;

/// <summary>The edge-range list in one line, as the client-address settings show it.</summary>
public static class CloudflareRangeText
{
    /// <summary>"Cloudflare ranges: updated 3 days ago (22 ranges)", or the built-in list and the day it is from.</summary>
    public static string Line(CloudflareRangeStatus status, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(status);
        var ranges = string.Create(CultureInfo.InvariantCulture, $"{status.Count} ranges");
        if (status.Source == CloudflareRangeSource.BuiltIn)
        {
            return $"Cloudflare ranges: built-in list from {status.FetchedAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} ({ranges})";
        }

        var days = Math.Max(0, (int)(now - status.FetchedAt).TotalDays);
        var age = days switch
        {
            0 => "today",
            1 => "1 day ago",
            _ => string.Create(CultureInfo.InvariantCulture, $"{days} days ago"),
        };
        return $"Cloudflare ranges: updated {age} ({ranges})";
    }
}
