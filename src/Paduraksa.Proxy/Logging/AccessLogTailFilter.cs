namespace Paduraksa.Proxy.Logging;

/// <summary>
/// Which entries a tail subscriber wants. Every part that is set must match; the hub applies it before an entry reaches the
/// subscriber's channel, so a narrow filter keeps the channel from filling with lines the person will never see.
/// </summary>
public sealed record AccessLogTailFilter
{
    /// <summary>The longest text any part may have: wider input is the caller's mistake, not a search.</summary>
    public const int MaxTextLength = 256;

    /// <summary>Everything.</summary>
    public static AccessLogTailFilter All { get; } = new();

    /// <summary>A name of the configured host that owned the request (any of its names, ignoring case).</summary>
    public string? Host { get; init; }

    /// <summary>Only requests no configured host owned; with <see cref="Host"/> nothing can match, so a caller picks one.</summary>
    public bool UnmatchedHost { get; init; }

    /// <summary>The status's class, 1 to 5 (a gate drop without a response has none).</summary>
    public int? StatusClass { get; init; }

    /// <summary>One status; wins over <see cref="StatusClass"/>.</summary>
    public int? Status { get; init; }

    /// <summary>The client address, whole or as a leading part (<c>192.168.1.</c>, <c>2001:db8:</c>), ignoring case.</summary>
    public string? ClientAddressPrefix { get; init; }

    /// <summary>The method, ignoring case.</summary>
    public string? Method { get; init; }

    /// <summary>A part of the path (not of the query), ignoring case.</summary>
    public string? PathContains { get; init; }

    public bool Matches(AccessLogTailEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (UnmatchedHost && entry.MatchedHostNames is not null)
        {
            return false;
        }

        if (Host is { Length: > 0 } host && !Owns(entry.MatchedHostNames, host))
        {
            return false;
        }

        if (Status is { } status)
        {
            if (entry.StatusCode != status)
            {
                return false;
            }
        }
        else if (StatusClass is { } statusClass && (entry.StatusCode <= 0 || entry.StatusCode / 100 != statusClass))
        {
            return false;
        }

        if (ClientAddressPrefix is { Length: > 0 } prefix
            && entry.ClientAddress?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) != true)
        {
            return false;
        }

        if (Method is { Length: > 0 } method && !string.Equals(entry.Method, method, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return PathContains is not { Length: > 0 } part || entry.Path?.Contains(part, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool Owns(IReadOnlyList<string>? names, string host)
    {
        if (names is null)
        {
            return false;
        }

        for (var i = 0; i < names.Count; i++)
        {
            if (string.Equals(names[i], host, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
