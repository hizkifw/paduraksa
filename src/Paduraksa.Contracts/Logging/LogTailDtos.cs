using System.Globalization;

namespace Paduraksa.Contracts.Logging;

/// <summary>
/// One request in the live access-log tail, as <c>access.log</c> would show it: the same caps, the same escaping of control
/// characters (<c>\xNN</c>) and the same redaction of sensitive query parameters. A request of a host whose access log is off is
/// never here.
/// </summary>
/// <param name="Sequence">Position in the stream since the process started. A jump between two is what the reader skipped.</param>
/// <param name="Time">When the request ended, UTC.</param>
/// <param name="ClientAddress">The resolved client address (behind a trusted proxy, the one its header carried).</param>
/// <param name="Host">The <c>Host</c> as received, port included.</param>
/// <param name="MatchedHost">The configured host's first name, or absent when no host owned the request (a scanner, a typo, an address).</param>
/// <param name="Method">The request method.</param>
/// <param name="Path">The decoded path.</param>
/// <param name="Query">The query, <c>?</c> included, with sensitive parameters as <c>name=REDACTED</c>; absent when there is none or the file leaves it out.</param>
/// <param name="Protocol"><c>HTTP/1.1</c>, <c>HTTP/2</c> or <c>HTTP/3</c>.</param>
/// <param name="Status">The status sent, or absent when the gate dropped the connection without answering.</param>
/// <param name="ResponseBytes">Response body bytes written to the client.</param>
/// <param name="RequestBytes">Request body bytes read from the client.</param>
/// <param name="TotalMs">Request start to the end of the response, in milliseconds.</param>
/// <param name="UpstreamMs">The forward to the upstream, in milliseconds; absent when nothing was forwarded.</param>
/// <param name="Upstream">The destination the forwarder chose.</param>
/// <param name="RequestIdentifier">The <c>X-Request-Id</c> of the request: the one a trusted proxy sent, or Paduraksa's own.</param>
/// <param name="CloudflareRay"><c>CF-Ray</c>, only when the request came through a trusted Cloudflare edge or tunnel.</param>
/// <param name="Referer">The <c>Referer</c> header.</param>
/// <param name="UserAgent">The <c>User-Agent</c> header.</param>
/// <param name="Timing">Where the request's time went, stage by stage.</param>
public sealed record LogTailEntry(
    long Sequence,
    DateTimeOffset Time,
    double TotalMs,
    string? ClientAddress = null,
    string? Host = null,
    string? MatchedHost = null,
    string? Method = null,
    string? Path = null,
    string? Query = null,
    string? Protocol = null,
    int? Status = null,
    long? ResponseBytes = null,
    long? RequestBytes = null,
    double? UpstreamMs = null,
    string? Upstream = null,
    string? RequestIdentifier = null,
    string? CloudflareRay = null,
    string? Referer = null,
    string? UserAgent = null,
    LogTailTiming? Timing = null);

/// <summary>
/// One request's time by stage, in milliseconds. A stage that didn't happen, or wasn't measured, is absent. The stages overlap
/// (the gate runs while the body may still be arriving; a streamed answer is sent while the upstream still writes it), so they don't
/// add up to the total.
/// </summary>
/// <param name="ConnectionRequest">The request's index on its connection, from 1.</param>
/// <param name="TlsHandshakeMs">The connection's TLS handshake, on its first request only.</param>
/// <param name="TlsReused">A later request on a TLS connection: its handshake was paid by the first.</param>
/// <param name="RequestBodyMs">From the first to the last request body byte read; 0 when there was no body.</param>
/// <param name="GateMs">The top of the pipeline to the gate's hand-off (to the forward, or to its own answer): the WAF, rate limits, access lists, auth and rules.</param>
/// <param name="UpstreamConnectMs">The upstream connect (DNS, TCP and TLS); 0 on a pooled connection.</param>
/// <param name="UpstreamWaitMs">The rest of the forward: sending the request and waiting for the upstream's response headers.</param>
/// <param name="ResponseSendMs">The response's headers to its last byte written to the client.</param>
public sealed record LogTailTiming(
    int? ConnectionRequest = null,
    double? TlsHandshakeMs = null,
    bool TlsReused = false,
    double? RequestBodyMs = null,
    double? GateMs = null,
    double? UpstreamConnectMs = null,
    double? UpstreamWaitMs = null,
    double? ResponseSendMs = null);

/// <summary>
/// Which requests a live tail wants; every part that is set must match. Nothing set is everything.
/// </summary>
/// <param name="Host">A name of a configured host (any of its names, ignoring case).</param>
/// <param name="UnmatchedHost">Only requests no configured host owned (not with <paramref name="Host"/>).</param>
/// <param name="StatusClass">The status's first digit, 1 to 5.</param>
/// <param name="Status">One status, 100 to 599; wins over <paramref name="StatusClass"/>.</param>
/// <param name="ClientAddress">The client address, whole or as a leading part (<c>192.168.1.</c>), ignoring case.</param>
/// <param name="Method">The method, ignoring case.</param>
/// <param name="PathContains">A part of the path (not of the query), ignoring case.</param>
public sealed record LogTailFilter(
    string? Host = null,
    bool UnmatchedHost = false,
    int? StatusClass = null,
    int? Status = null,
    string? ClientAddress = null,
    string? Method = null,
    string? PathContains = null)
{
    /// <summary>The longest text any part may have.</summary>
    public const int MaxTextLength = 256;

    /// <summary>Whether anything is set.</summary>
    public bool IsEmpty => this == new LogTailFilter();

    /// <summary>What is wrong with the filter, as a sentence for whoever set it; null when it is fine.</summary>
    public string? Problem()
    {
        if (Host is { Length: > MaxTextLength } || ClientAddress is { Length: > MaxTextLength } || Method is { Length: > MaxTextLength } || PathContains is { Length: > MaxTextLength })
        {
            return $"A filter text is at most {MaxTextLength} characters.";
        }

        if (StatusClass is { } statusClass && !IsStatusClass(statusClass))
        {
            return "statusClass must be 1 to 5.";
        }

        if (Status is { } status && !IsStatus(status))
        {
            return "status must be 100 to 599.";
        }

        return UnmatchedHost && !string.IsNullOrEmpty(Host) ? "host and unmatched can't be combined: a request that no host owns has no host name." : null;
    }

    /// <summary>
    /// Reads a status field: a status (<c>404</c>) or a class (<c>5xx</c>, or just <c>5</c>); the one grammar the API's
    /// <c>status</c> and <c>statusClass</c> parameters and the dashboard's field share. Empty is no filter and succeeds with
    /// both outputs absent; anything else that is no status or class fails.
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
            if (IsStatusClass(digit))
            {
                statusClass = digit;
                return true;
            }

            return false;
        }

        if (int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var exact) && IsStatus(exact))
        {
            status = exact;
            return true;
        }

        return false;
    }

    private static bool IsStatusClass(int value) => value is >= 1 and <= 5;

    private static bool IsStatus(int value) => value is >= 100 and <= 599;
}

/// <summary>The data of a <c>skipped</c> event on the tail stream: how many entries the reader missed because it fell behind.</summary>
/// <param name="Count">Entries dropped since the previous such event.</param>
public sealed record LogTailSkipped(long Count);
