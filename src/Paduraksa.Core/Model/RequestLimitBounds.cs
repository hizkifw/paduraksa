namespace Paduraksa.Core.Model;

/// <summary>
/// The bounds and built-in values of the request limits, shared by the validator, the compiler, the dashboard and the
/// public listeners. A host's header and URL limits sit at or below the <c>Max</c> values; the listeners' Kestrel limits
/// are set from them, so the transport never stops a request the host's own limit would have admitted.
/// </summary>
public static class RequestLimitBounds
{
    /// <summary>A body limit of 0 means no limit, said outright.</summary>
    public const long UnlimitedBodySize = 0;

    /// <summary>
    /// The built-in body limit: 100 MiB. Kestrel's own 30 MB breaks the photo, document and package uploads people put
    /// behind a proxy, while no limit at all lets one request fill a disk; above 100 MiB an upload is large enough that the
    /// host's owner should choose the figure, per host or per route.
    /// </summary>
    public const long DefaultMaxBodySize = 100L * 1024 * 1024;

    /// <summary>The largest body limit written as a number (1 TiB); beyond it, use <see cref="UnlimitedBodySize"/>.</summary>
    public const long MaxBodySize = 1L << 40;

    /// <summary>Fewest header lines a limit may allow: below it an ordinary browser request would be refused.</summary>
    public const int MinHeaderCount = 8;

    /// <summary>The built-in header-line limit (Kestrel's own default).</summary>
    public const int DefaultMaxHeaderCount = 100;

    /// <summary>Most header lines a limit may allow, and what the listeners accept at all.</summary>
    public const int MaxHeaderCount = 128;

    /// <summary>Fewest bytes of header names and values a limit may allow.</summary>
    public const int MinHeaderBytes = 1024;

    /// <summary>The built-in limit on header names and values together, in bytes (Kestrel's own default, 32 KiB).</summary>
    public const int DefaultMaxHeaderBytes = 32 * 1024;

    /// <summary>Most header bytes a limit may allow, and what the listeners accept at all (64 KiB).</summary>
    public const int MaxHeaderBytes = 64 * 1024;

    /// <summary>Shortest request target a limit may allow.</summary>
    public const int MinUrlLength = 256;

    /// <summary>The built-in request-target limit, in bytes (8 KiB, Kestrel's request-line default).</summary>
    public const int DefaultMaxUrlLength = 8 * 1024;

    /// <summary>Longest request target a limit may allow, and what the listeners accept at all (16 KiB).</summary>
    public const int MaxUrlLength = 16 * 1024;

    /// <summary>Room the transport's limits keep above the host's: a method token has no length of its own, and HTTP/2 and HTTP/3 count
    /// the pseudo-headers (<c>:method</c>, <c>:scheme</c>, <c>:authority</c>, <c>:path</c>) with the headers.</summary>
    public const int TransportHeadroom = 4 * 1024;

    /// <summary>Kestrel's request-line limit: the longest target, the method and the protocol on the line, and headroom for a long method token.</summary>
    public const int OuterRequestLineBytes = MaxUrlLength + TransportHeadroom;

    /// <summary>
    /// Kestrel's limit on the headers together. It sums names and values (HTTP/2 and HTTP/3 include the pseudo-headers, so the target
    /// counts again), so it is the host's largest header limit plus the longest target plus headroom: the gate answers before Kestrel can.
    /// </summary>
    public const int OuterHeadersBytes = MaxHeaderBytes + MaxUrlLength + TransportHeadroom;

    /// <summary>One header field on HTTP/2 and HTTP/3 (name and value): as large as all the headers together, so one big cookie the host admits gets through.</summary>
    public const int OuterHeaderFieldBytes = OuterHeadersBytes;
}

/// <summary>The bounds and built-in values of response compression, shared by the validator, the compiler and the dashboard.</summary>
public static class CompressionBounds
{
    /// <summary>The built-in smallest body compressed: 1 KiB.</summary>
    public const int DefaultMinimumSize = 1024;

    /// <summary>The largest minimum a host may set: 1 MiB.</summary>
    public const int MaxMinimumSize = 1024 * 1024;
}
