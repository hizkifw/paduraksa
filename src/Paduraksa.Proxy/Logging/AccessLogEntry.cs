namespace Paduraksa.Proxy.Logging;

/// <summary>
/// One request, as the hot path hands it to the access-log ring. A struct that <b>references strings the
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
    /// <summary>"Not known": the request wasn't forwarded, or its body bytes weren't counted (it never passed the access log's middleware).</summary>
    public const long Unknown = -1;

    /// <summary>
    /// "No response": the gate dropped the request (<see cref="Gate.GateRequestState.Aborted"/>), so nothing was sent and
    /// the line says <c>-</c> instead of the 200 a response object would still report.
    /// </summary>
    public const int NoResponse = 0;

    /// <summary><see cref="TlsHandshakeTicks"/> of a later request on a TLS connection: the handshake was paid by the first.</summary>
    public const long HandshakeReused = -2;

    /// <summary>A start offset (<see cref="ForwardStartMicros"/> and the others) of a stage that didn't happen or wasn't measured.</summary>
    public const int UnknownOffset = -1;

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
    public int StatusCode
    {
        get => _statusCode;
        init => _statusCode = (short)Math.Clamp(value, short.MinValue, short.MaxValue);
    }

    // A short (a status is three digits) so the entry fits its ring slot with the stages' start offsets and RequestBodyUnfinished.
    private readonly short _statusCode;

    /// <summary>Response body bytes written to the client (<see cref="Http.BodyCounter"/>), or <see cref="Unknown"/>.</summary>
    public long ResponseBytes { get; init; } = Unknown;

    /// <summary>Request body bytes read from the client, or <see cref="Unknown"/>.</summary>
    public long RequestBytes { get; init; } = Unknown;

    /// <summary>Request start to end, in <see cref="TimeSpan"/> ticks (0 is a valid, very fast answer).</summary>
    public long TotalTicks { get; init; }

    /// <summary>YARP's request transform to its response transform, in <see cref="TimeSpan"/> ticks, or <see cref="Unknown"/>.</summary>
    public long UpstreamTicks { get; init; } = Unknown;

    /// <summary>The destination the forwarder chose (<c>http://10.0.0.2:8080/</c>), or <see langword="null"/>.</summary>
    public string? UpstreamAddress { get; init; }

    /// <summary>The request ID (<see cref="RequestIds"/>), always known: taken from a trusted proxy or generated.</summary>
    public string? RequestId { get; init; }

    /// <summary><c>CF-Ray</c>, only when the peer is a trusted Cloudflare edge or tunnel.</summary>
    public string? CloudflareRay { get; init; }

    public string? Referer { get; init; }

    public string? UserAgent { get; init; }

    // The stages (docs/design/observability.md "Request timing"), in TimeSpan ticks, Unknown when the stage didn't happen or wasn't measured.

    /// <summary>
    /// The connection's TLS handshake, on the connection's first request only; <see cref="HandshakeReused"/> on its later requests,
    /// <see cref="Unknown"/> on plain HTTP and when it wasn't measured. Held in an <see cref="int"/> so the entry fits its ring slot
    /// with the start offsets: up to about 214 s, far past Kestrel's handshake timeout; a longer one reads as that.
    /// </summary>
    public long TlsHandshakeTicks
    {
        get => _tlsHandshakeTicks;
        init => _tlsHandshakeTicks = (int)Math.Clamp(value, int.MinValue, int.MaxValue);
    }

    private readonly int _tlsHandshakeTicks = (int)Unknown;

    /// <summary>The request's index on its connection, from 1; 0 when not known.</summary>
    public int ConnectionRequest { get; init; }

    /// <summary>The first to the last request body byte read; 0 when no body byte was, <see cref="Unknown"/> when not counted.</summary>
    public long RequestBodyTicks { get; init; } = Unknown;

    /// <summary>The top of the pipeline to the gate's hand-off (to the forward, or to its own answer); <see cref="Unknown"/> when no gate ran.</summary>
    public long GateTicks { get; init; } = Unknown;

    /// <summary>The upstream connect (DNS, TCP, TLS) inside <see cref="UpstreamTicks"/>; 0 on a pooled connection, <see cref="Unknown"/> when not forwarded.</summary>
    public long UpstreamConnectTicks { get; init; } = Unknown;

    /// <summary>The response's start (its headers) to its end; <see cref="Unknown"/> when no response was sent.</summary>
    public long ResponseSendTicks { get; init; } = Unknown;

    // Where the stages start, in microseconds from the request's start (RequestStages.Offset), UnknownOffset when the stage didn't
    // happen. Only what the durations above can't give: the gate starts at 0, the body ends at its start plus its length, the upstream
    // answered at the forward's start plus UpstreamTicks, and the send starts at the total minus its length.

    /// <summary>Where the forward started (YARP's first request transform); <see cref="UnknownOffset"/> when nothing was forwarded.</summary>
    public int ForwardStartMicros { get; init; } = UnknownOffset;

    /// <summary>Where the upstream connect started; <see cref="UnknownOffset"/> without one (a pooled connection, nothing forwarded).</summary>
    public int UpstreamConnectStartMicros { get; init; } = UnknownOffset;

    /// <summary>Where the first request body byte was read; <see cref="UnknownOffset"/> when none was.</summary>
    public int RequestBodyStartMicros { get; init; } = UnknownOffset;

    /// <summary>The body had begun but no read found its end: the upstream answered (or the forward failed) before it was all sent.</summary>
    public bool RequestBodyUnfinished { get; init; }

    /// <summary>
    /// Waiting for the upstream's response headers once our side was sent, in <see cref="TimeSpan"/> ticks: Chrome DevTools' "Waiting for
    /// server response" (<see cref="RequestStages.WaitTicks"/>); 0 when the upstream answered before the body was all sent,
    /// <see cref="Unknown"/> when nothing was forwarded.
    /// </summary>
    public long UpstreamWaitTicks =>
        RequestStages.WaitTicks(ForwardStartMicros, UpstreamTicks, UpstreamConnectStartMicros, UpstreamConnectTicks, RequestBodyStartMicros, RequestBodyTicks, RequestBodyUnfinished);
}
