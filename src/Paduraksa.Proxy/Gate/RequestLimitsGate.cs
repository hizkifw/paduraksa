using System.Text;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;

namespace Paduraksa.Proxy.Gate;

/// <summary>What a request's size was judged to be, against a host's limits.</summary>
internal enum RequestLimitVerdict : byte
{
    Within,
    UrlTooLong,
    TooManyHeaders,
    HeadersTooLarge,
}

/// <summary>
/// What the gate measured of a request. <see cref="HeaderBytes"/> is -1 when the header count already decided, since the
/// bytes were then not summed.
/// </summary>
/// <param name="UrlBytes">The request target, in UTF-8 bytes.</param>
/// <param name="HeaderCount">Header lines (a header sent twice is two).</param>
/// <param name="HeaderBytes">Names and values of every line, in UTF-8 bytes.</param>
internal readonly record struct RequestSize(int UrlBytes, int HeaderCount, long HeaderBytes);

/// <summary>
/// The gate's request-size checks and the Kestrel limits they sit inside. Everything here allocates nothing: the headers are
/// copied into a pooled scratch array and read from there, because enumerating Kestrel's header dictionary through
/// its interface boxes the enumerator.
/// </summary>
public static class RequestLimitsGate
{
    private const string ContentLengthName = "Content-Length";

    // CopyTo needs an array, and one holding strings can't be stackalloc'd. A thread-static array cost 4 KB on every thread's first
    // request; this pool is stocked by the first requests of any thread and then never allocates, however threads come and go. The
    // gate decides synchronously, so an array is out for the length of one scan and back, cleared so it keeps no request's strings.
    private static readonly KeyValuePair<string, StringValues>[]?[] ScratchPool = new KeyValuePair<string, StringValues>[]?[Math.Max(16, Environment.ProcessorCount * 2)];

    /// <summary>
    /// Sets the transport's own limits to the outer bound of every host's: a host's header and request-target limits can be
    /// anything up to <see cref="RequestLimitBounds"/>, and the transport must not refuse a request the host would take. The
    /// body limit is not set here: it is per host and route, and the gate puts it on each request.
    /// </summary>
    public static void ConfigureTransport(Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        limits.MaxRequestLineSize = RequestLimitBounds.OuterRequestLineBytes;
        limits.MaxRequestHeadersTotalSize = RequestLimitBounds.OuterHeadersBytes;
        limits.MaxRequestHeaderCount = RequestLimitBounds.MaxHeaderCount;
        limits.Http2.MaxRequestHeaderFieldSize = RequestLimitBounds.OuterHeaderFieldBytes;
        limits.Http3.MaxRequestHeaderFieldSize = RequestLimitBounds.OuterHeaderFieldBytes;
    }

    /// <summary>
    /// Puts the body limit on the request before anything reads the body, so Kestrel itself refuses a body that passes it (413,
    /// or a reset stream once the response has started). 0 is no limit. A request whose body is already being read, or
    /// an upgraded one, keeps what it has.
    /// </summary>
    internal static void ApplyBodyLimit(HttpContext context, long limit)
    {
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
        {
            feature.MaxRequestBodySize = limit == RequestLimitBounds.UnlimitedBodySize ? null : limit;
        }
    }

    /// <summary>Whether the request is within the host's request-target and header limits, and what it measured.</summary>
    internal static RequestLimitVerdict Check(HttpContext context, HostRequestLimitsSpec limits, out RequestSize size)
    {
        var url = UrlBytes(context);
        size = new RequestSize(url, 0, -1);
        if (url > limits.MaxUrlLength)
        {
            return RequestLimitVerdict.UrlTooLong;
        }

        var headers = context.Request.Headers;

        // Kestrel keeps Content-Length as a number and writes a new string for it each time the headers are copied or
        // enumerated: 32 B a request with a body. So the header is taken out for the copy and put back, and counted by hand.
        var length = headers.ContentLength;
        if (length is not null)
        {
            headers.ContentLength = null;
        }

        try
        {
            return CheckHeaders(headers, length, limits, ref size);
        }
        finally
        {
            if (length is not null)
            {
                headers.ContentLength = length;
            }
        }
    }

    private static RequestLimitVerdict CheckHeaders(IHeaderDictionary headers, long? length, HostRequestLimitsSpec limits, ref RequestSize size)
    {
        var lines = length is null ? 0 : 1;
        long bytes = length is null ? 0 : ContentLengthName.Length + DigitCount(length.Value);

        // Distinct header names never exceed the lines, so a host that is over on names is over on lines, and the scan is
        // skipped. It also bounds the copy below by the limit, which is at most the scratch array's size.
        var names = headers.Count;
        if (names + lines > limits.MaxHeaderCount || names > RequestLimitBounds.MaxHeaderCount)
        {
            size = size with { HeaderCount = names + lines };
            return RequestLimitVerdict.TooManyHeaders;
        }

        var slot = Environment.CurrentManagedThreadId % ScratchPool.Length;
        var scratch = RentScratch(slot);
        headers.CopyTo(scratch, 0);
        for (var i = 0; i < names; i++)
        {
            ref readonly var header = ref scratch[i];
            var name = Encoding.UTF8.GetByteCount(header.Key);
            foreach (var value in header.Value)
            {
                lines++;
                bytes += name + (value is null ? 0 : Encoding.UTF8.GetByteCount(value));
            }
        }

        Array.Clear(scratch, 0, names);
        ReturnScratch(slot, scratch);
        size = size with { HeaderCount = lines, HeaderBytes = bytes };
        if (lines > limits.MaxHeaderCount)
        {
            return RequestLimitVerdict.TooManyHeaders;
        }

        return bytes > limits.MaxHeaderBytes ? RequestLimitVerdict.HeadersTooLarge : RequestLimitVerdict.Within;
    }

    private static KeyValuePair<string, StringValues>[] RentScratch(int start)
    {
        for (var i = 0; i < ScratchPool.Length; i++)
        {
            if (Interlocked.Exchange(ref ScratchPool[(start + i) % ScratchPool.Length], null) is { } array)
            {
                return array;
            }
        }

        return new KeyValuePair<string, StringValues>[RequestLimitBounds.MaxHeaderCount];
    }

    private static void ReturnScratch(int start, KeyValuePair<string, StringValues>[] array)
    {
        for (var i = 0; i < ScratchPool.Length; i++)
        {
            if (Interlocked.CompareExchange(ref ScratchPool[(start + i) % ScratchPool.Length], array, null) is null)
            {
                return;
            }
        }
    }

    private static int DigitCount(long value)
    {
        var digits = 1;
        for (; value >= 10; value /= 10)
        {
            digits++;
        }

        return digits;
    }

    /// <summary>The request target as the client sent it; a context without one (a test's) falls back to the decoded path and query.</summary>
    private static int UrlBytes(HttpContext context)
    {
        if (context.Features.Get<IHttpRequestFeature>()?.RawTarget is { Length: > 0 } raw)
        {
            return Encoding.UTF8.GetByteCount(raw);
        }

        var request = context.Request;
        return Encoding.UTF8.GetByteCount(request.Path.Value ?? string.Empty) + Encoding.UTF8.GetByteCount(request.QueryString.Value ?? string.Empty);
    }
}
