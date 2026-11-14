using System.Text;

namespace Paduraksa.Proxy.Logging;

/// <summary>
/// One request as the live tail holds and shows it: the fields of an <see cref="AccessLogEntry"/> after the same caps,
/// control-character escaping and query redaction the file line gets, so the tail never shows more than <c>access.log</c> would.
/// Immutable, and shared by every subscriber. Built by the drain, off the request path.
/// </summary>
/// <remarks>
/// Missing values are <see langword="null"/>. Text is the file's quoted text as the file writes it (<see cref="LogEscaping"/>: the
/// same escapes for quotes, backslashes, controls and the Unicode line separators), a field past its cap ends with <c>...</c>, and a
/// parameter named in the sensitive list reads <c>name=REDACTED</c>. Only a field
/// the rules change is copied; the rest keeps the string the request already held, which is why a capped entry costs little.
/// </remarks>
public sealed class AccessLogTailEntry
{
    private AccessLogTailEntry()
    {
    }

    /// <summary>Position in the stream of offered entries, from 1. A gap between two a subscriber sees is what it skipped.</summary>
    public long Sequence { get; private set; }

    /// <summary>The moment the request ended, as UTC <see cref="DateTime"/> ticks.</summary>
    public long TimestampUtcTicks { get; private init; }

    public string? ClientAddress { get; private init; }

    /// <summary>The <c>Host</c> as received, port included.</summary>
    public string? Host { get; private init; }

    /// <summary>
    /// Every name of the configured host that owned the request, or <see langword="null"/> when no host did (a scanner, a typo, an
    /// address). Resolved against the live snapshot when the entry was built.
    /// </summary>
    public IReadOnlyList<string>? MatchedHostNames { get; private init; }

    public string? Method { get; private init; }

    public string? Path { get; private init; }

    /// <summary>The query with its sensitive parameters redacted, <c>?</c> included; null when there is none or the file leaves it out.</summary>
    public string? Query { get; private init; }

    public string? Protocol { get; private init; }

    /// <summary>The status sent, or <see cref="AccessLogEntry.NoResponse"/> when the gate dropped the request.</summary>
    public int StatusCode { get; private init; }

    /// <summary>Response body bytes written to the client, or <see cref="AccessLogEntry.Unknown"/>.</summary>
    public long ResponseBytes { get; private init; }

    /// <summary>Request body bytes read from the client, or <see cref="AccessLogEntry.Unknown"/>.</summary>
    public long RequestBytes { get; private init; }

    /// <summary>In <see cref="TimeSpan"/> ticks.</summary>
    public long TotalTicks { get; private init; }

    /// <summary>In <see cref="TimeSpan"/> ticks, or <see cref="AccessLogEntry.Unknown"/>.</summary>
    public long UpstreamTicks { get; private init; }

    public string? UpstreamAddress { get; private init; }

    public string? RequestId { get; private init; }

    public string? CloudflareRay { get; private init; }

    public string? Referer { get; private init; }

    public string? UserAgent { get; private init; }

    /// <summary>The stages, as on <see cref="AccessLogEntry"/> (<see cref="TimeSpan"/> ticks, <see cref="AccessLogEntry.Unknown"/> when not known).</summary>
    public long TlsHandshakeTicks { get; private init; }

    public int ConnectionRequest { get; private init; }

    public long RequestBodyTicks { get; private init; }

    public long GateTicks { get; private init; }

    public long UpstreamConnectTicks { get; private init; }

    public long ResponseSendTicks { get; private init; }

    internal void Number(long sequence) => Sequence = sequence;

    /// <summary>
    /// What the buffer keeps of an entry: the struct, with any string longer than a bit past its cap cut there. Only the memory
    /// is bounded here (a header can run to kilobytes); the visible caps, the escaping and the redaction are applied by
    /// <see cref="From"/> when the entry is read, and one character more than the cap is kept so that a field that was cut still
    /// ends with the marker. No allocation unless a string is cut.
    /// </summary>
    internal static AccessLogEntry Bound(in AccessLogEntry entry) =>
        Fits(entry.Path, AccessLogFormatter.MaxPathLength) && Fits(entry.Query, MaxRawQueryLength) && Fits(entry.Host, AccessLogFormatter.MaxHostLength)
            && Fits(entry.UserAgent, AccessLogFormatter.MaxHeaderValueLength) && Fits(entry.Referer, AccessLogFormatter.MaxHeaderValueLength)
            && Fits(entry.UpstreamAddress, AccessLogFormatter.MaxUpstreamLength) && Fits(entry.RequestId, RequestIds.MaxLength)
            && Fits(entry.Method, AccessLogFormatter.MaxMethodLength) && Fits(entry.ClientAddress, AccessLogFormatter.MaxClientAddressLength)
            && Fits(entry.Protocol, AccessLogFormatter.MaxProtocolLength) && Fits(entry.CloudflareRay, AccessLogFormatter.MaxCloudflareRayLength)
            ? entry
            : entry with
            {
                Path = Cut(entry.Path, AccessLogFormatter.MaxPathLength),
                Query = Cut(entry.Query, MaxRawQueryLength),
                Host = Cut(entry.Host, AccessLogFormatter.MaxHostLength),
                UserAgent = Cut(entry.UserAgent, AccessLogFormatter.MaxHeaderValueLength),
                Referer = Cut(entry.Referer, AccessLogFormatter.MaxHeaderValueLength),
                UpstreamAddress = Cut(entry.UpstreamAddress, AccessLogFormatter.MaxUpstreamLength),
                RequestId = Cut(entry.RequestId, RequestIds.MaxLength),
                Method = Cut(entry.Method, AccessLogFormatter.MaxMethodLength),
                ClientAddress = Cut(entry.ClientAddress, AccessLogFormatter.MaxClientAddressLength),
                Protocol = Cut(entry.Protocol, AccessLogFormatter.MaxProtocolLength),
                CloudflareRay = Cut(entry.CloudflareRay, AccessLogFormatter.MaxCloudflareRayLength),
            };

    /// <summary>
    /// The raw query kept before redaction: the file's own query cap, so the tail never holds more of a query than it could show.
    /// Redaction after the cut stays safe: a cut only ever removes the end of the last parameter, so a sensitive parameter that
    /// straddles it is a name with a partial value, which reads <c>name=REDACTED</c> like any other, or a partial name, which is no
    /// value at all.
    /// </summary>
    private const int MaxRawQueryLength = AccessLogFormatter.MaxQueryLength;

    private static bool Fits(string? value, int cap) => value is null || value.Length <= cap + 1;

    private static string? Cut(string? value, int cap) => value is not null && value.Length > cap + 1 ? value[..(cap + 1)] : value;

    /// <summary>Copies <paramref name="entry"/> (the ring's slot is reused) under the file's rules.</summary>
    internal static AccessLogTailEntry From(in AccessLogEntry entry, AccessLogFormatOptions options, IReadOnlyList<string>? matchedHostNames) => new()
    {
        TimestampUtcTicks = entry.TimestampUtcTicks,
        ClientAddress = Visible(entry.ClientAddress, AccessLogFormatter.MaxClientAddressLength),
        Host = Visible(entry.Host, AccessLogFormatter.MaxHostLength),
        MatchedHostNames = matchedHostNames,
        Method = Visible(entry.Method, AccessLogFormatter.MaxMethodLength),
        Path = Visible(entry.Path, AccessLogFormatter.MaxPathLength),
        Query = options.LogQueryString ? VisibleQuery(entry.Query, options) : null,
        Protocol = Visible(entry.Protocol, AccessLogFormatter.MaxProtocolLength),
        StatusCode = entry.StatusCode,
        ResponseBytes = entry.ResponseBytes,
        RequestBytes = entry.RequestBytes,
        TotalTicks = entry.TotalTicks,
        UpstreamTicks = entry.UpstreamTicks,
        UpstreamAddress = Visible(entry.UpstreamAddress, AccessLogFormatter.MaxUpstreamLength),
        RequestId = Visible(entry.RequestId, RequestIds.MaxLength),
        CloudflareRay = Visible(entry.CloudflareRay, AccessLogFormatter.MaxCloudflareRayLength),
        Referer = Visible(entry.Referer, AccessLogFormatter.MaxHeaderValueLength),
        UserAgent = Visible(entry.UserAgent, AccessLogFormatter.MaxHeaderValueLength),
        TlsHandshakeTicks = entry.TlsHandshakeTicks,
        ConnectionRequest = entry.ConnectionRequest,
        RequestBodyTicks = entry.RequestBodyTicks,
        GateTicks = entry.GateTicks,
        UpstreamConnectTicks = entry.UpstreamConnectTicks,
        ResponseSendTicks = entry.ResponseSendTicks,
    };

    private static string? VisibleQuery(string? query, AccessLogFormatOptions options)
    {
        if (query is not { Length: > 0 })
        {
            return null;
        }

        Span<char> redacted = stackalloc char[AccessLogFormatter.MaxQueryLength + AccessLogFormatter.TruncationMarker.Length];
        var length = 0;
        var truncated = false;
        AccessLogFormatter.AppendQuery(query, options, redacted, ref length, ref truncated);
        // Bound kept one character more than the cap, so a longer query than that was cut before it got here (or is that long).
        truncated |= query.Length > AccessLogFormatter.MaxQueryLength;
        if (truncated)
        {
            length = Math.Min(length, redacted.Length - AccessLogFormatter.TruncationMarker.Length);
            AccessLogFormatter.TruncationMarker.CopyTo(redacted[length..]);
            length += AccessLogFormatter.TruncationMarker.Length;
        }

        var visible = redacted[..length];
        // Nothing to redact, cap or escape: the request's own string is what the file shows too.
        return visible.SequenceEqual(query) && !NeedsCopy(visible, AccessLogFormatter.MaxQueryLength + AccessLogFormatter.TruncationMarker.Length)
            ? query
            : Visible(visible, AccessLogFormatter.MaxQueryLength + AccessLogFormatter.TruncationMarker.Length);
    }

    private static string? Visible(string? value, int budget)
    {
        if (value is not { Length: > 0 })
        {
            return null;
        }

        return NeedsCopy(value, budget) ? Visible(value.AsSpan(), budget) : value;
    }

    /// <summary>Whether the text is already what the file would show: short enough and without a character that is escaped.</summary>
    private static bool NeedsCopy(ReadOnlySpan<char> value, int budget)
    {
        if (value.Length > budget)
        {
            return true;
        }

        foreach (var c in value)
        {
            if (LogEscaping.EscapedLength(c) != 1)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The escaping and cap of the file's quoted fields (<c>\xNN</c>, then <c>...</c> past the budget), without the quoting.</summary>
    private static string Visible(ReadOnlySpan<char> value, int budget)
    {
        if (!NeedsCopy(value, budget))
        {
            return value.ToString();
        }

        StringBuilder text = new(Math.Min(value.Length, budget) + 3);
        var written = 0;
        Span<char> escaped = stackalloc char[LogEscaping.MaxEscapedLength];
        foreach (var c in value)
        {
            var cost = LogEscaping.EscapedLength(c);
            if (written + cost > budget)
            {
                text.Append(AccessLogFormatter.TruncationMarker);
                break;
            }

            if (cost == 1)
            {
                text.Append(c);
            }
            else
            {
                text.Append(escaped[..LogEscaping.Escape(c, escaped)]);
            }

            written += cost;
        }

        return text.ToString();
    }
}
