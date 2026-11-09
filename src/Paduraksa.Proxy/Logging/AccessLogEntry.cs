namespace Paduraksa.Proxy.Logging;

/// <summary>
/// One request, as the hot path hands it to the access-log ring (PLAN §7.4). A struct that <b>references strings the
/// request already owns</b>: the method, path, query, host, protocol and headers Kestrel parsed, the cached client-address
/// text (<see cref="Gate.GateRequestState.FormattedClientAddress"/>), the gate's request ID and YARP's destination
/// address. Nothing is copied, and no <see cref="Microsoft.AspNetCore.Http.HttpContext"/> or pooled object is kept, so an
/// entry stays valid after the request ends (and after its connection or stream is reused).
/// <para>Missing values are <see langword="null"/> here and become <c>-</c> in the formatted line.
/// <see cref="UpstreamTicks"/> and <see cref="ResponseBytes"/> use -1 for "not known": a request the gate answered (a
/// redirect, a 403, the 404 for an unknown host) never reached an upstream. <see cref="StatusCode"/> is
/// <see cref="NoResponse"/> when nothing was sent at all.</para>
/// </summary>
public readonly record struct AccessLogEntry
{
    /// <summary>"Not known": the response declared no <c>Content-Length</c>, or the request wasn't forwarded.</summary>
    public const long Unknown = -1;

    /// <summary>
    /// "No response": the gate dropped the request (<see cref="Gate.GateRequestState.Aborted"/>), so nothing was sent and
    /// the line says <c>-</c> instead of the 200 a response object would still report.
    /// </summary>
    public const int NoResponse = 0;

    /// <summary>An entry with every field at its default ("unknown"), which the middleware then fills in.</summary>
    public AccessLogEntry()
    {
    }

    /// <summary>The moment the request ended, as UTC <see cref="DateTime"/> ticks.</summary>
    public long TimestampUtcTicks { get; init; }

    /// <summary>The resolved client address as text (behind a trusted proxy, the address its header carried).</summary>
    public string? ClientAddress { get; init; }

    /// <summary>The <c>Host</c> (H2/H3 <c>:authority</c>) as received, port included.</summary>
    public string? Host { get; init; }

    public string? Method { get; init; }

    /// <summary>The decoded path, without the query.</summary>
    public string? Path { get; init; }

    /// <summary>The raw query string, leading <c>?</c> included; the formatter redacts its sensitive parameters.</summary>
    public string? Query { get; init; }

    /// <summary><c>HTTP/1.1</c>, <c>HTTP/2</c>, <c>HTTP/3</c>: as Kestrel reports it.</summary>
    public string? Protocol { get; init; }

    /// <summary>The status sent to the client, or <see cref="NoResponse"/> when nothing was.</summary>
    public int StatusCode { get; init; }
    /// <summary>The response's <c>Content-Length</c> in bytes, or <see cref="Unknown"/>.</summary>
    public long ResponseBytes { get; init; } = Unknown;

    /// <summary>Request start to end, in <see cref="TimeSpan"/> ticks (0 is a valid, very fast answer).</summary>
    public long TotalTicks { get; init; }

    /// <summary>YARP's request transform to its response transform, in <see cref="TimeSpan"/> ticks, or <see cref="Unknown"/>.</summary>
    public long UpstreamTicks { get; init; } = Unknown;

    /// <summary>The destination the forwarder chose (<c>http://10.0.0.2:8080/</c>), or <see langword="null"/>.</summary>
    public string? UpstreamAddress { get; init; }

    /// <summary>The request ID (<see cref="RequestIds"/>), always known: taken from a trusted proxy or generated.</summary>
    public string? RequestId { get; init; }

    /// <summary><c>CF-Ray</c>, only when the peer is a trusted Cloudflare edge or tunnel (PLAN §7.6).</summary>
    public string? CloudflareRay { get; init; }

    public string? Referer { get; init; }

    public string? UserAgent { get; init; }
}
