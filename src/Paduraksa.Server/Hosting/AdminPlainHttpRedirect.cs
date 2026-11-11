using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Paduraksa.Server.Hosting;

/// <summary>
/// Answers a plain-HTTP request on the admin's TLS listener with a permanent redirect to the same URL over <c>https</c>,
/// so a browser handed <c>http://host:9443/</c> - a typo, an old bookmark, a link printed without its scheme - lands on the
/// panel instead of an empty response it can't explain.
/// </summary>
/// <remarks>
/// <para>Kestrel can't serve HTTP and HTTPS on one port, so this connection stage peeks at the first byte instead: a TLS
/// handshake record (<c>0x16</c>, every ClientHello) is left unread in the pipe for the TLS middleware, and anything else
/// is read as an HTTP/1.x request head and answered with a <c>308</c> and an empty body. Nothing is ever served over
/// plain HTTP: no endpoint, no cookie, no certificate, and the connection is closed after the one answer. An untrusted
/// source is still refused by the allowlist before this stage, which never reads a byte from it.</para>
/// <para>The redirect speaks for the admin port, and has to trust the request's <c>Host</c> to know the name to send the
/// browser back to. It is therefore validated strictly (a name, or an IPv6 literal in brackets, with an optional port) and
/// reused verbatim in <c>Location</c>, so a crafted header can neither inject a header of its own nor move the browser to
/// another site. Only GET and HEAD with an origin-form target are answered; a request without a usable host, a target that
/// isn't a path, or another method is a plain <c>400</c>.</para>
/// <para>Install it after the source allowlist and before <c>UseHttps</c>.</para>
/// </remarks>
internal static partial class AdminPlainHttpRedirect
{
    /// <summary>The TLS handshake record's content type: every connection that means TLS starts with a ClientHello.</summary>
    private const byte TlsHandshakeRecord = 0x16;

    /// <summary>The longest request head accepted. Real heads are a few hundred bytes.</summary>
    private const int MaxHeadBytes = 8 * 1024;

    /// <summary>How long a client has to send a complete request head; one that connects and says nothing is dropped.</summary>
    private static readonly TimeSpan DefaultHeadTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Installs <see cref="AdminPlainHttpRedirect"/> on a listener: call it after the source allowlist and before
    /// <c>UseHttps</c>, so a refused source is aborted before any byte is read and a TLS handshake still reaches the TLS
    /// middleware untouched.
    /// </summary>
    /// <param name="listener">The TCP listener.</param>
    /// <param name="logger">Where a redirect or an unreadable plaintext request is logged (Debug).</param>
    /// <param name="headTimeout">How long a client has to send a complete request head; null for ten seconds.</param>
    internal static ListenOptions UsePlainHttpRedirect(this ListenOptions listener, ILogger logger, TimeSpan? headTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(listener);
        ArgumentNullException.ThrowIfNull(logger);
        var timeout = headTimeout ?? DefaultHeadTimeout;
        listener.Use(next => connection => OnConnectionAsync(connection, next, logger, timeout));
        return listener;
    }

    /// <summary>
    /// The stage itself: peeks, then either hands the connection to <paramref name="next"/> (TLS) or answers it and closes.
    /// </summary>
    internal static async Task OnConnectionAsync(
        ConnectionContext connection, ConnectionDelegate next, ILogger logger, TimeSpan headTimeout)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(logger);

        var input = connection.Transport.Input;
        var head = await ReadHeadAsync(input, headTimeout).ConfigureAwait(false);

        switch (head.Kind)
        {
            case HeadKind.TlsHandshake:
                // Not a byte consumed: the TLS middleware reads the same ClientHello from the same pipe.
                await next(connection).ConfigureAwait(false);
                return;

            case HeadKind.Closed:
                input.Complete();
                connection.Transport.Output.Complete();
                return;

            case HeadKind.Redirect:
                var location = "https://" + head.Host + head.Target;
                LogRedirect(logger, location);
                await AnswerAsync(connection, "308 Permanent Redirect", location).ConfigureAwait(false);
                return;

            default:
                LogUnreadable(logger);
                await AnswerAsync(connection, "400 Bad Request", location: null).ConfigureAwait(false);
                return;
        }
    }

    /// <summary>
    /// Reads just enough of the connection's first bytes to tell TLS from plain HTTP, and the head of a plain request.
    /// Never throws for anything the peer sends or does: a timeout, a close and a reset are <see cref="HeadKind.Closed"/>.
    /// </summary>
    private static async ValueTask<RequestHead> ReadHeadAsync(PipeReader input, TimeSpan headTimeout)
    {
        // Only a connection that has to wait pays for the timer: a typed URL usually arrives in one segment.
        CancellationTokenSource? timer = null;

        // How far the search for the empty line that ends the head has gone. The buffer is never consumed until the head is
        // complete, so each arrival only has to look at the bytes it added (see TryFindHeadEnd).
        var searched = 0L;
        try
        {
            while (true)
            {
                if (!input.TryRead(out var result))
                {
                    timer ??= new CancellationTokenSource(headTimeout);
                    result = await input.ReadAsync(timer.Token).ConfigureAwait(false);
                }

                var buffer = result.Buffer;
                if (result.IsCanceled)
                {
                    input.AdvanceTo(buffer.Start);
                    return RequestHead.Closed;
                }

                if (!buffer.IsEmpty && buffer.FirstSpan[0] == TlsHandshakeRecord)
                {
                    // Peeked, not consumed: the consumed *and* examined position stay at the start, so the TLS middleware's
                    // very next read returns these same bytes instead of waiting for more (which would deadlock the
                    // handshake: the client is waiting for the ServerHello and sends nothing else).
                    input.AdvanceTo(buffer.Start);
                    return RequestHead.Tls;
                }

                if (TryFindHeadEnd(buffer, ref searched, out var headEnd))
                {
                    // Parsed before the bytes are consumed: AdvanceTo hands the segments back to the pipe's pool.
                    var parsed = headEnd > MaxHeadBytes ? RequestHead.Unreadable : ParseHead(buffer.Slice(0, headEnd));
                    input.AdvanceTo(buffer.End);
                    return parsed;
                }

                if (result.IsCompleted || buffer.Length >= MaxHeadBytes)
                {
                    input.AdvanceTo(buffer.End);

                    // A peer that connected, sent nothing and left is a scan or a probe, not a request: no answer, no log.
                    return result.IsCompleted && buffer.IsEmpty ? RequestHead.Closed : RequestHead.Unreadable;
                }

                // Nothing usable yet: mark what we have as examined so the next read waits for more bytes.
                input.AdvanceTo(buffer.Start, buffer.End);
            }
        }
        catch (OperationCanceledException)
        {
            // The head timeout, or Kestrel's ConnectionAbortedException when the peer went away.
            return RequestHead.Closed;
        }
        catch (IOException)
        {
            // Includes ConnectionResetException: the peer reset before or while it was sending.
            return RequestHead.Closed;
        }
        finally
        {
            timer?.Dispose();
        }
    }

    /// <summary>
    /// Finds the empty line (<c>\r\n\r\n</c>) that ends an HTTP head, returning the offset where the head itself stops.
    /// </summary>
    /// <param name="buffer">Everything buffered so far, from the first byte of the request.</param>
    /// <param name="searched">How far the previous call got; updated. Only the bytes since then are scanned, so a client
    /// that drips its head a byte at a time costs linear work over the whole head instead of a re-scan per byte.</param>
    /// <param name="headEnd">The index of the terminating <c>\r</c>, when found.</param>
    internal static bool TryFindHeadEnd(in ReadOnlySequence<byte> buffer, ref long searched, out long headEnd)
    {
        headEnd = -1;
        if (buffer.Length < 4)
        {
            return false;
        }

        // A terminator split across two arrivals starts at most three bytes before what was already searched.
        var position = Math.Max(0, searched - 3);
        Span<byte> window = stackalloc byte[4];
        var seen = 0;
        SequenceReader<byte> reader = new(buffer.Slice(position));
        while (reader.TryRead(out var b))
        {
            window[0] = window[1];
            window[1] = window[2];
            window[2] = window[3];
            window[3] = b;
            position++;
            seen++;
            if (seen >= 4 && window[0] == (byte)'\r' && window[1] == (byte)'\n'
                && window[2] == (byte)'\r' && window[3] == (byte)'\n')
            {
                searched = position;
                headEnd = position - 4;
                return true;
            }
        }

        searched = position;
        return false;
    }

    /// <summary>
    /// Parses a complete HTTP/1.x request head - everything before the empty line - and decides whether it is one this
    /// stage will redirect.
    /// </summary>
    private static RequestHead ParseHead(in ReadOnlySequence<byte> head)
    {
        // Latin-1 maps every byte to the char of the same value, so a non-ASCII byte in the target or the host stays out of
        // the printable range the checks below allow instead of being silently replaced.
        var lines = Encoding.Latin1.GetString(head).Split("\r\n");
        var requestLine = lines[0];

        // METHOD SP request-target SP HTTP-version: one space either side of the target, a path as the target.
        var firstSpace = requestLine.IndexOf(' ', StringComparison.Ordinal);
        var lastSpace = firstSpace < 0 ? -1 : requestLine.LastIndexOf(' ');
        if (firstSpace <= 0 || lastSpace <= firstSpace)
        {
            return RequestHead.Unreadable;
        }

        var method = requestLine[..firstSpace];
        var target = requestLine[(firstSpace + 1)..lastSpace];
        var version = requestLine[(lastSpace + 1)..];
        if (!string.Equals(method, "GET", StringComparison.Ordinal)
            && !string.Equals(method, "HEAD", StringComparison.Ordinal))
        {
            return RequestHead.Unreadable;
        }

        if (version is not ("HTTP/1.0" or "HTTP/1.1") || !IsOriginFormTarget(target))
        {
            return RequestHead.Unreadable;
        }

        string? host = null;
        foreach (var line in lines.AsSpan(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0 || !line.AsSpan(0, colon).Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (host is not null)
            {
                return RequestHead.Unreadable; // two Host headers are never a request this port should follow
            }

            host = line[(colon + 1)..].Trim();
        }

        return host is not null && IsValidHost(host) ? new RequestHead(HeadKind.Redirect, host, target) : RequestHead.Unreadable;
    }

    /// <summary>An origin-form target: a path, printable ASCII only, so it can go into <c>Location</c> untouched.</summary>
    private static bool IsOriginFormTarget(string target)
    {
        if (target.Length == 0 || target[0] != '/')
        {
            return false;
        }

        foreach (var c in target)
        {
            if (c is < (char)0x21 or > (char)0x7E)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A host this port will send a browser back to: a DNS name (<c>letters</c>, digits, <c>.</c>, <c>-</c>, <c>_</c>), or
    /// an IPv6 literal in brackets, each optionally followed by a port. Nothing that could end the header line or turn the
    /// authority into a path (<c>/</c>, <c>\</c>, <c>@</c>, <c>?</c>, <c>#</c>, <c>%</c>) survives, and a malformed literal
    /// (unbalanced or doubled brackets, an empty name, a non-numeric port) is refused rather than sent on to a browser that
    /// could not parse it.
    /// </summary>
    private static bool IsValidHost(string host)
    {
        if (host.Length is 0 or > 255)
        {
            return false;
        }

        ReadOnlySpan<char> name = host;
        var lastColon = host.LastIndexOf(':');
        if (lastColon >= 0 && lastColon > host.IndexOf(']'))
        {
            // A port after the last colon, which can't be one of an IPv6 literal's own (those sit inside the brackets). It has
            // to be a number: a bare "host:" is not something a browser writes, and a Location ending in ":" reads as broken.
            if (lastColon == host.Length - 1)
            {
                return false;
            }

            foreach (var c in host.AsSpan(lastColon + 1))
            {
                if (!char.IsAsciiDigit(c))
                {
                    return false;
                }
            }

            name = host.AsSpan(0, lastColon);
        }

        if (name.IsEmpty)
        {
            return false;
        }

        if (name[0] == '[')
        {
            if (name.Length < 3 || name[^1] != ']')
            {
                return false;
            }

            var literal = name[1..^1];
            foreach (var c in literal)
            {
                if (!char.IsAsciiHexDigit(c) && c is not (':' or '.'))
                {
                    return false;
                }
            }

            return IPAddress.TryParse(literal, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6;
        }

        foreach (var c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Writes one plain-HTTP answer and closes the connection; a peer that leaves first is not an error.</summary>
    private static async Task AnswerAsync(ConnectionContext connection, string status, string? location)
    {
        var output = connection.Transport.Output;
        var input = connection.Transport.Input;
        StringBuilder answer = new(192);
        answer.Append("HTTP/1.1 ").Append(status).Append("\r\n");
        if (location is not null)
        {
            answer.Append("Location: ").Append(location).Append("\r\n");
        }

        // no-store: a 308 is cacheable for good, and a browser must ask again rather than pin a stale scheme or port.
        answer.Append("Content-Length: 0\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("X-Content-Type-Options: nosniff\r\n")
            .Append("Connection: close\r\n\r\n");

        try
        {
            await output.WriteAsync(Encoding.ASCII.GetBytes(answer.ToString()), connection.ConnectionClosed)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            // The client went away before the answer was out; there is no one left to tell.
        }
        finally
        {
            output.Complete();
            input.Complete();
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "The admin port answered a plain-HTTP request with a redirect to {Location}")]
    private static partial void LogRedirect(ILogger logger, string location);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "The admin port closed a connection that sent neither a TLS handshake nor a redirectable HTTP/1 request")]
    private static partial void LogUnreadable(ILogger logger);

    /// <summary>What the first bytes of a connection turned out to be.</summary>
    private enum HeadKind : byte
    {
        /// <summary>A TLS ClientHello, left for the TLS middleware.</summary>
        TlsHandshake = 0,

        /// <summary>A plain GET or HEAD naming a host: answer it with a redirect.</summary>
        Redirect = 1,

        /// <summary>Plaintext that isn't a request this port follows: answer it with a 400.</summary>
        Unreadable = 2,

        /// <summary>Nothing arrived, or the peer left: close.</summary>
        Closed = 3,
    }

    private readonly record struct RequestHead(HeadKind Kind, string Host, string Target)
    {
        public static RequestHead Tls => new(HeadKind.TlsHandshake, string.Empty, string.Empty);

        public static RequestHead Unreadable => new(HeadKind.Unreadable, string.Empty, string.Empty);

        public static RequestHead Closed => new(HeadKind.Closed, string.Empty, string.Empty);
    }
}
