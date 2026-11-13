using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Paduraksa.Proxy.Net;

/// <summary>
/// The public listeners' slow-client and connection limits, stated once and set on Kestrel explicitly (docs/design/hot-path.md "A. Connection", G8 of the
/// threat model). They are process settings, not config: Kestrel reads its limits when the listeners bind. <c>0</c> turns a
/// connection cap off. <see cref="Validate"/> is the one place that says what is allowed; the server's options validator calls it.
/// </summary>
/// <remarks>
/// <para><b>The headers timeout is per listener, not per host.</b> It runs from the first byte of a request until its headers are
/// complete, and the host is only known once they are, so no host can own it.</para>
/// <para><b>HTTP/3 has no limit here.</b> Kestrel's stable API has none for concurrent streams (the QUIC transport's own is a
/// preview API), so a QUIC connection keeps the transport's default of 100 concurrent requests, the same number as HTTP/2's.</para>
/// <para><b>Long-lived connections.</b> Kestrel stops enforcing the request-body rate on an upgraded connection (a WebSocket) and
/// times the response rate only while a write waits for the client, so a WebSocket or an event stream that is idle between
/// messages isn't cut by these limits. <see cref="KeepAliveTimeout"/> is the wait between requests and doesn't touch a request in
/// progress. <see cref="MaxUpgradedConnections"/> only refuses a new upgrade.</para>
/// </remarks>
public sealed record ServerLimits
{
    /// <summary>Kestrel's own value is 30 s; a legitimate client sends a request's headers in well under a second, and an HTTP/2 or
    /// TLS client has already paid the handshake.</summary>
    public static readonly TimeSpan DefaultRequestHeadersTimeout = TimeSpan.FromSeconds(15);

    public static ServerLimits Default { get; } = new();

    /// <summary>From the first byte of a request to the end of its headers; a slower client is closed (a slowloris).</summary>
    public TimeSpan RequestHeadersTimeout { get; init; } = DefaultRequestHeadersTimeout;

    /// <summary>How long an idle HTTP/1.1 or HTTP/2 connection waits for the next request.</summary>
    public TimeSpan KeepAliveTimeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>The TLS handshake on TCP, from the first byte.</summary>
    public TimeSpan TlsHandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The slowest a request body may arrive, in bytes per second, once the grace has passed; 0 turns the limit off.
    /// Kestrel's own is 240 B/s with 5 s.</summary>
    public long MinRequestBodyRate { get; init; } = 120;

    public TimeSpan MinRequestBodyGrace { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The slowest a client may take a response, in bytes per second, once the grace has passed; 0 turns it off.</summary>
    public long MinResponseRate { get; init; } = 240;

    public TimeSpan MinResponseGrace { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Open connections the public listeners hold together; Kestrel stops accepting at it. 0 is unlimited.</summary>
    public long MaxConnections { get; init; } = 10_000;

    /// <summary>The WebSocket (upgraded) share of <see cref="MaxConnections"/>; 0 is unlimited.</summary>
    public long MaxUpgradedConnections { get; init; } = 2_000;

    /// <summary>Concurrent requests on one HTTP/2 connection.</summary>
    public int Http2MaxStreamsPerConnection { get; init; } = 100;

    /// <summary>Why these values can't be used, one sentence each; empty when they can.</summary>
    public IReadOnlyList<string> Validate()
    {
        List<string> failures = [];
        Positive(RequestHeadersTimeout, nameof(RequestHeadersTimeout), failures);
        Positive(KeepAliveTimeout, nameof(KeepAliveTimeout), failures);
        Positive(TlsHandshakeTimeout, nameof(TlsHandshakeTimeout), failures);
        if (MinRequestBodyRate < 0 || MinResponseRate < 0)
        {
            failures.Add("a minimum data rate can't be negative (0 turns it off)");
        }

        if (MinRequestBodyRate > 0)
        {
            Grace(MinRequestBodyGrace, nameof(MinRequestBodyGrace), failures);
        }

        if (MinResponseRate > 0)
        {
            Grace(MinResponseGrace, nameof(MinResponseGrace), failures);
        }

        if (MaxConnections < 0 || MaxUpgradedConnections < 0)
        {
            failures.Add("the connection limits can't be negative (0 is unlimited)");
        }

        if (Http2MaxStreamsPerConnection < 1)
        {
            failures.Add("Http2MaxStreamsPerConnection must be at least 1");
        }

        return failures;
    }

    /// <summary>Sets every limit this type states on Kestrel's.</summary>
    /// <exception cref="InvalidOperationException">The values are invalid (<see cref="Validate"/>).</exception>
    public void Apply(KestrelServerLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (Validate() is { Count: > 0 } failures)
        {
            throw new InvalidOperationException(string.Join("; ", failures));
        }

        limits.RequestHeadersTimeout = RequestHeadersTimeout;
        limits.KeepAliveTimeout = KeepAliveTimeout;
        limits.MinRequestBodyDataRate = MinRequestBodyRate == 0 ? null : new MinDataRate(MinRequestBodyRate, MinRequestBodyGrace);
        limits.MinResponseDataRate = MinResponseRate == 0 ? null : new MinDataRate(MinResponseRate, MinResponseGrace);
        limits.MaxConcurrentConnections = MaxConnections == 0 ? null : MaxConnections;
        limits.MaxConcurrentUpgradedConnections = MaxUpgradedConnections == 0 ? null : MaxUpgradedConnections;
        limits.Http2.MaxStreamsPerConnection = Http2MaxStreamsPerConnection;
    }

    // Kestrel's own rule: its MinDataRate refuses a grace period of one second or less.
    private static void Grace(TimeSpan value, string name, List<string> failures)
    {
        if (value <= TimeSpan.FromSeconds(1))
        {
            failures.Add($"{name} must be longer than 1 second");
        }
    }

    private static void Positive(TimeSpan value, string name, List<string> failures)
    {
        if (value <= TimeSpan.Zero)
        {
            failures.Add($"{name} must be positive");
        }
    }
}
