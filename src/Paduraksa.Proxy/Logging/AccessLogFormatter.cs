using System.Globalization;

namespace Paduraksa.Proxy.Logging;

/// <summary>
/// Formats one <see cref="AccessLogEntry"/> into one line. Off the request path: only
/// <see cref="AccessLogDrainService"/> calls it, into a buffer it reuses, so a line costs no allocation.
/// </summary>
/// <remarks>
/// <para><b>The format</b> is a combined-style line, ASCII and single-line by construction:</para>
/// <code>
/// 2026-01-31T12:34:56.789Z 198.51.100.7 "app.example" "GET /a/b?token=REDACTED HTTP/2" 200 512 1.234 0.987
///     "http://10.0.0.2:8080/" "0k3m9x2p-00000000000abc" "8f2a1c3d4e5f6789" "https://example.com/" "Mozilla/5.0"
/// </code>
/// <para>Fields, in order: (1) the time of the end of the request, ISO-8601 UTC with milliseconds; (2) the resolved client
/// address, unquoted (it is an address Paduraksa formatted, never a client's string); (3) the <c>Host</c> as received;
/// (4) the request target, one quoted field holding <c>method SP path[?query] SP protocol</c>; (5) the status, or <c>-</c>
/// when the gate dropped the request without a response; (6) the response body bytes; (7) the total duration; (8) the
/// upstream duration; (9) the upstream destination; (10) the request ID; (11) <c>CF-Ray</c>; (12) the referer; (13) the user
/// agent. Fields are separated by one space, and the line has <b>no trailing newline</b>: the sink writes it as one
/// line.</para>
/// <para><b>Missing values</b> are a bare <c>-</c>. Text fields are quoted; a quoted field is empty of meaning when the
/// entry has no value, which is why <c>-</c> stands outside the quotes.</para>
/// <para><b>Durations</b> are milliseconds with three decimals (microsecond resolution, e.g. <c>1.234</c>): enough to see a
/// slow upstream from a slow client, and it sorts and greps as one token. The upstream duration is YARP's request
/// transform to its response transform, so it includes YARP's own destination choice and hand-off; <c>-</c> when the
/// request never reached an upstream (the gate answered it) or the forwarder returned nothing.</para>
/// <para><b>Escaping.</b> Inside a quoted field <c>"</c> becomes <c>\"</c>, <c>\</c> becomes <c>\\</c>, and a character
/// below 0x20 or 0x7F becomes <c>\xNN</c>; nothing else changes, so non-ASCII text stays readable (the sink writes
/// UTF-8). A line therefore can never be forged from a hostile path, user agent or referer: a CR or LF arrives escaped,
/// and it can't end the line or start a new one.</para>
/// <para><b>Caps.</b> Every text field has an output cap (<see cref="MaxPathLength"/>, <see cref="MaxQueryLength"/> and
/// the others below). A field longer than its cap is cut at the cap and closed with <c>...</c> inside the quotes, so one
/// pathological request can't produce a line a reader (or a log shipper's line limit) chokes on.
/// <see cref="MaxLineLength"/> bounds the whole line: with every cap in force a line is well under it.</para>
/// <para><b>Secrets.</b> Cookies and <c>Authorization</c> are never part of an entry at all. The query string is written
/// with the parameters named in <see cref="AccessLogFormatOptions.SensitiveQueryKeys"/> replaced by
/// <c>name=REDACTED</c>, or left out entirely when <see cref="AccessLogFormatOptions.LogQueryString"/> is off. Names are
/// matched as written (ignoring case, not percent-decoded), so a client that escapes a name escapes redaction too: the
/// list covers the names real clients send, and a deployment can extend it.</para>
/// </remarks>
public static class AccessLogFormatter
{
    /// <summary>The most characters a line can take: the caps below sum to far less, with room to spare.</summary>
    public const int MaxLineLength = 8 * 1024;

    /// <summary>Cap of the whole request target (method, path and query) inside the quotes.</summary>
    public const int MaxRequestTargetLength = 2 * 1024;

    /// <summary>Cap of the path part of the request target.</summary>
    public const int MaxPathLength = 1 * 1024;

    /// <summary>Cap of the query part of the request target.</summary>
    public const int MaxQueryLength = 1 * 1024;

    /// <summary>Cap of the method.</summary>
    public const int MaxMethodLength = 32;

    /// <summary>Cap of the protocol (<c>HTTP/1.1</c>, <c>HTTP/2</c>, <c>HTTP/3</c>).</summary>
    public const int MaxProtocolLength = 16;

    /// <summary>Cap of the client address (<c>2001:db8::1</c> is 11, a formatted IPv4-mapped address 15; the cap is slack).</summary>
    public const int MaxClientAddressLength = 64;

    /// <summary>Cap of the <c>Host</c> field.</summary>
    public const int MaxHostLength = 255;

    /// <summary>Cap of the referer and user-agent fields.</summary>
    public const int MaxHeaderValueLength = 512;

    /// <summary>Cap of the upstream destination field.</summary>
    public const int MaxUpstreamLength = 256;

    /// <summary>Cap of the <c>CF-Ray</c> field.</summary>
    public const int MaxCloudflareRayLength = 64;

    /// <summary>The marker a capped field ends with, inside its quotes.</summary>
    public const string TruncationMarker = "...";

    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    private const string MillisecondsFormat = "0.000";
    private const string Missing = "-";
    private const string Redacted = "=REDACTED";

    /// <summary>
    /// Formats <paramref name="entry"/> into <paramref name="destination"/> and returns the number of characters written;
    /// -1 when the buffer is smaller than the line (pass <see cref="MaxLineLength"/>: then it can't be).
    /// </summary>
    public static int Format(in AccessLogEntry entry, AccessLogFormatOptions options, Span<char> destination)
    {
        ArgumentNullException.ThrowIfNull(options);
        Line line = new(destination);

        // 1. Time of the end of the request.
        if (line.Remaining.Length >= 24
            && new DateTime(entry.TimestampUtcTicks, DateTimeKind.Utc).TryFormat(line.Remaining, out var timeWritten, TimeFormat, CultureInfo.InvariantCulture))
        {
            line.Advance(timeWritten);
        }
        else
        {
            return -1;
        }

        // 2. Client address (an address we formatted, never a client-supplied string, so unquoted), 3. host.
        line.Write(' ');
        line.Write(Cap(entry.ClientAddress, MaxClientAddressLength));
        line.Write(' ');
        line.Quote(entry.Host, MaxHostLength);

        // 4. The request target, built (with its query redacted) apart from the line.
        line.Write(' ');
        Span<char> target = stackalloc char[MaxRequestTargetLength];
        var targetLength = BuildRequestTarget(in entry, options, target);
        line.Quote(target[..targetLength], MaxRequestTargetLength);

        // 5-8. Status, bytes, total and upstream durations.
        line.Write(' ');
        if (entry.StatusCode <= AccessLogEntry.NoResponse)
        {
            // The gate dropped the request: nothing was sent, so there is no status to report.
            line.Write(Missing);
        }
        else
        {
            line.Write(entry.StatusCode);
        }
        line.Write(' ');
        if (entry.ResponseBytes < 0)
        {
            line.Write(Missing);
        }
        else
        {
            line.Write(entry.ResponseBytes);
        }

        line.Write(' ');
        line.WriteMilliseconds(entry.TotalTicks);
        line.Write(' ');
        if (entry.UpstreamTicks < 0)
        {
            line.Write(Missing);
        }
        else
        {
            line.WriteMilliseconds(entry.UpstreamTicks);
        }

        // 9-13. Upstream, request ID, CF-Ray, referer, user agent.
        line.Write(' ');
        line.Quote(entry.UpstreamAddress, MaxUpstreamLength);
        line.Write(' ');
        line.Quote(entry.RequestId, RequestIds.MaxLength);
        line.Write(' ');
        line.Quote(entry.CloudflareRay, MaxCloudflareRayLength);
        line.Write(' ');
        line.Quote(entry.Referer, MaxHeaderValueLength);
        line.Write(' ');
        line.Quote(entry.UserAgent, MaxHeaderValueLength);

        return line.Overflowed ? -1 : line.Length;
    }

    /// <summary>The quoted request target: the entry's method, a space, its path, then the query with its sensitive
    /// parameters redacted (or left out).</summary>
    private static int BuildRequestTarget(in AccessLogEntry entry, AccessLogFormatOptions options, Span<char> target)
    {
        var length = 0;
        var truncated = false;
        AppendCapped(entry.Method, MaxMethodLength, target, ref length, ref truncated);
        truncated |= !Append(" ", target, ref length);
        AppendCapped(entry.Path, MaxPathLength, target, ref length, ref truncated);

        if (options.LogQueryString && entry.Query is { Length: > 0 } query)
        {
            // The entry carries the raw query, '?' included; the target writes it back as one token.
            truncated |= !Append("?", target, ref length);
            var start = query[0] == '?' ? 1 : 0;
            while (true)
            {
                var end = query.AsSpan(start).IndexOf('&');
                var parameter = end < 0 ? query.AsSpan(start) : query.AsSpan(start, end);
                var equals = parameter.IndexOf('=');
                var name = equals < 0 ? parameter : parameter[..equals];
                if (options.IsSensitive(name))
                {
                    // Whatever the value was, it doesn't reach the log. A bare name becomes an explicit redacted pair, so
                    // `?token` and `?token=` read the same way.
                    truncated |= !Append(name, target, ref length);
                    truncated |= !Append(Redacted, target, ref length);
                }
                else
                {
                    truncated |= !Append(parameter, target, ref length);
                }

                if (end < 0)
                {
                    break;
                }

                truncated |= !Append("&", target, ref length);
                start += end + 1;
            }
        }

        truncated |= !Append(" ", target, ref length);
        AppendCapped(entry.Protocol, MaxProtocolLength, target, ref length, ref truncated);

        if (truncated && length > target.Length - TruncationMarker.Length)
        {
            length = target.Length - TruncationMarker.Length;
        }

        if (truncated)
        {
            TruncationMarker.AsSpan().CopyTo(target[length..]);
            length += TruncationMarker.Length;
        }

        return length;
    }

    /// <summary>
    /// Appends <paramref name="value"/> cut at <paramref name="cap"/> (flagging the cut, so the target ends with
    /// <see cref="TruncationMarker"/>), or <see cref="Missing"/> when the entry has none. Chars are counted as written
    /// here, not as escaped: <see cref="Line.Quote(ReadOnlySpan{char}, int)"/> enforces each quoted field's output cap.
    /// </summary>
    private static void AppendCapped(string? value, int cap, Span<char> target, ref int length, ref bool truncated)
    {
        if (value is not { Length: > 0 })
        {
            truncated |= !Append(Missing, target, ref length);
            return;
        }

        if (value.Length > cap)
        {
            truncated = true;
            truncated |= !Append(value.AsSpan(0, cap), target, ref length);
            return;
        }

        truncated |= !Append(value, target, ref length);
    }

    /// <summary>A value cut at <paramref name="cap"/>: for the unquoted client address, which we formatted ourselves.</summary>
    private static ReadOnlySpan<char> Cap(string? value, int cap) =>
        value is not { Length: > 0 } ? Missing : value.Length <= cap ? value : value.AsSpan(0, cap);

    /// <summary>Appends <paramref name="text"/>; false when the buffer filled first.</summary>
    private static bool Append(ReadOnlySpan<char> text, Span<char> target, ref int length)
    {
        var room = target.Length - length;
        if (text.Length > room)
        {
            text[..room].CopyTo(target[length..]);
            length = target.Length;
            return false;
        }

        text.CopyTo(target[length..]);
        length += text.Length;
        return true;
    }

    /// <summary>The line under construction: a span, an offset and whether anything overflowed.</summary>
    private ref struct Line
    {
        private readonly Span<char> _buffer;
        private int _length;

        public Line(Span<char> buffer)
        {
            _buffer = buffer;
            _length = 0;
            Overflowed = false;
        }

        public readonly int Length => _length;

        /// <summary>True when a write didn't fit: <see cref="Format"/> reports that as -1.</summary>
        public bool Overflowed { get; private set; }

        public readonly Span<char> Remaining => _buffer[_length..];

        public void Advance(int written) => _length += written;

        public void Write(char value)
        {
            if (_length < _buffer.Length)
            {
                _buffer[_length++] = value;
            }
            else
            {
                MarkOverflow();
            }
        }

        public void Write(ReadOnlySpan<char> value)
        {
            if (value.Length > _buffer.Length - _length)
            {
                MarkOverflow();
                return;
            }

            value.CopyTo(_buffer[_length..]);
            _length += value.Length;
        }

        public void Write(int value)
        {
            if (!value.TryFormat(_buffer[_length..], out var written, provider: CultureInfo.InvariantCulture))
            {
                MarkOverflow();
                return;
            }

            _length += written;
        }

        public void Write(long value)
        {
            if (!value.TryFormat(_buffer[_length..], out var written, provider: CultureInfo.InvariantCulture))
            {
                MarkOverflow();
                return;
            }

            _length += written;
        }

        public void WriteMilliseconds(long ticks)
        {
            var milliseconds = ticks / (double)TimeSpan.TicksPerMillisecond;
            if (!milliseconds.TryFormat(_buffer[_length..], out var written, MillisecondsFormat, CultureInfo.InvariantCulture))
            {
                MarkOverflow();
                return;
            }

            _length += written;
        }

        /// <summary>
        /// A quoted, escaped field: <c>"..."</c>, or a bare <c>-</c> when there is no value. Escaping is documented on
        /// <see cref="AccessLogFormatter"/>; a value that would exceed <paramref name="budget"/> output characters stops
        /// there and closes with <see cref="TruncationMarker"/>.
        /// </summary>
        public void Quote(string? value, int budget) => Quote(value is null ? default : value.AsSpan(), budget);

        public void Quote(scoped ReadOnlySpan<char> value, int budget)
        {
            if (value.IsEmpty)
            {
                Write(Missing);
                return;
            }

            Write('"');
            var written = 0;
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (c is '"' or '\\')
                {
                    if (written + 2 > budget)
                    {
                        Write(TruncationMarker);
                        break;
                    }

                    Write('\\');
                    Write(c);
                    written += 2;
                }
                else if (c < ' ' || c == '\x7F')
                {
                    if (written + 4 > budget)
                    {
                        Write(TruncationMarker);
                        break;
                    }

                    Write('\\');
                    Write('x');
                    Write(HexDigit(c >> 4));
                    Write(HexDigit(c & 0xF));
                    written += 4;
                }
                else
                {
                    if (written + 1 > budget)
                    {
                        Write(TruncationMarker);
                        break;
                    }

                    Write(c);
                    written++;
                }
            }

            Write('"');
        }

        private static char HexDigit(int value) => (char)(value < 10 ? '0' + value : 'a' + (value - 10));

        private void MarkOverflow()
        {
            Overflowed = true;
            _length = _buffer.Length;
        }
    }
}
