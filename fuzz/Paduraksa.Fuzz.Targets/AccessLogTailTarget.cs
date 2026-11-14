using System.Globalization;
using System.Text;

using Paduraksa.Proxy.Logging;

namespace Paduraksa.Fuzz;

/// <summary>
/// One request's hostile text through both of its renderings: the access-log file's line (<see cref="AccessLogFormatter"/>) and the live tail's entry
/// (<see cref="AccessLogTailEntry"/>, built the way the drain builds it), which share one escaper (PLAN §7.4, "As built (M9): the live tail"). The input is a flag byte
/// (the query string logged or not, a secret and a plain sentinel put into the query, the sensitive-key set, how the text is decoded), numbers, then ten text fields
/// separated by 0xFF (host, method, path, query, protocol, upstream, request ID, <c>CF-Ray</c>, referer, user agent).
/// Properties: no exception and no overflow; the same output twice and 0 B for the line; the line is one line (no character a reader takes for a line end) and a plain reader
/// of its grammar finds nineteen fields; every field of the tail equals the file line's field (the numbers by value; the quoted text exactly when no cap cut it, and
/// otherwise a prefix of the full escape that ends in the marker); the request target of the line is the one a plain model builds from the method, path, redacted query and protocol,
/// and the tail's query is that redacted query escaped; a value of a sensitive parameter appears in neither (a sentinel put in one at the front of the query shows it); and with the
/// query string off, no byte of it (a sentinel in a plain parameter shows it) appears in either.
/// </summary>
internal static class AccessLogTailTarget
{
    private const string Secret = "S3cr3tV4lue9f3a";
    private const string Plain = "Pl41nV4lue7c1d";
    private const char Separator = '\u00ff';

    private static readonly string[] ClientAddresses = [null!, "198.51.100.7", "2001:db8::1", "::ffff:203.0.113.9"];

    private static readonly AccessLogFormatOptions[] Options =
    [
        new(),
        new() { LogQueryString = false },
        new() { SensitiveQueryKeys = ["custom_secret", "X-Token", "t"] },
        new() { LogQueryString = false, SensitiveQueryKeys = [] },
    ];

    private static readonly char[] Buffer = new char[AccessLogFormatter.MaxLineLength];

    public static FuzzTarget Target { get; } = new(
        "access-log-tail",
        "a request's path, query, host, request ID, referer and user agent through the access-log line and the live tail's entry",
        Run,
        Seeds);

    private static void Run(ReadOnlySpan<byte> input)
    {
        if (input.Length < 8)
        {
            return;
        }

        var flags = input[0];
        var options = Options[(flags & 1) | (((flags >> 1) & 1) << 1)];
        var secretInQuery = (flags & 4) != 0;
        var plainInQuery = (flags & 8) != 0;
        var decode = (flags >> 4) & 3;
        var status = (input[1] | (input[2] << 8)) % 700;
        var fields = Fields(input[8..], decode);

        var query = fields[3];
        if (secretInQuery || plainInQuery)
        {
            // Sentinels at the front of the query, where a cap can't cut them away; the key is one the options redact (in a spelling of its own case).
            var key = options.SensitiveQueryKeys.Count > 0 ? options.SensitiveQueryKeys[0].ToUpperInvariant() : "TOKEN";
            var front = new StringBuilder("?");
            if (secretInQuery)
            {
                front.Append(key).Append('=').Append(Secret).Append('&');
            }

            if (plainInQuery)
            {
                front.Append("zz=").Append(Plain).Append('&');
            }

            query = front + (query ?? string.Empty).TrimStart('?');
        }

        var entry = new AccessLogEntry
        {
            TimestampUtcTicks = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks + ((input[6] | (input[7] << 8)) * 10_000_017L),
            ClientAddress = ClientAddresses[input[1] % ClientAddresses.Length],
            Host = fields[0],
            Method = fields[1],
            Path = fields[2],
            Query = query,
            Protocol = fields[4],
            StatusCode = status,
            ResponseBytes = input[3] == 0 ? AccessLogEntry.Unknown : input[3] * 1_000_003L,
            TotalTicks = input[4] * 12_345L,
            UpstreamTicks = input[5] == 0 ? AccessLogEntry.Unknown : input[5] * 7_001L,
            UpstreamAddress = fields[5],
            RequestId = fields[6],
            CloudflareRay = fields[7],
            Referer = fields[8],
            UserAgent = fields[9],
            // The stages from the same bytes, so the input layout (and the corpus) stays as it was.
            TlsHandshakeTicks = (input[6] % 3) switch { 0 => AccessLogEntry.Unknown, 1 => AccessLogEntry.HandshakeReused, _ => input[5] * 911L },
            ConnectionRequest = input[7] % 5,
            RequestBodyTicks = input[3] % 3 == 0 ? AccessLogEntry.Unknown : input[3] * 1_234L,
            GateTicks = input[4] % 4 == 0 ? AccessLogEntry.Unknown : input[4] * 17L,
            UpstreamConnectTicks = input[5] == 0 ? AccessLogEntry.Unknown : input[5] % 2 == 0 ? 0 : input[5] * 3_003L,
            ResponseSendTicks = input[2] % 5 == 0 ? AccessLogEntry.Unknown : input[2] * 99_991L,
            ForwardStartMicros = input[5] == 0 ? AccessLogEntry.UnknownOffset : input[4] * 3,
            UpstreamConnectStartMicros = input[5] % 2 == 0 ? AccessLogEntry.UnknownOffset : input[4] * 5,
            RequestBodyStartMicros = input[3] % 3 == 0 ? AccessLogEntry.UnknownOffset : input[2] * 7,
        };

        var length = AccessLogFormatter.Format(in entry, options, Buffer);
        Checks.That(length >= 0, "the line overflowed its maximum length");
        var line = new string(Buffer, 0, length);
        Checks.That(AccessLogFormatter.Format(in entry, options, Buffer) == length && new string(Buffer, 0, length) == line, "the same entry gave a different line the second time");
        Checks.AllocatesNothing(() => _ = AccessLogFormatter.Format(in entry, options, Buffer), "AccessLogFormatter.Format");

        var tail = AccessLogTailEntry.From(AccessLogTailEntry.Bound(in entry), options, null);
        var again = AccessLogTailEntry.From(AccessLogTailEntry.Bound(in entry), options, null);
        Checks.That(SameFields(tail, again), "the same entry gave a different tail entry the second time");

        CheckLine(line);
        var file = ReadFields(line);
        CheckAgainstFile(entry, options, tail, file);
        CheckSecrets(entry, options, line, tail, secretInQuery, plainInQuery);
    }

    private static void CheckLine(string line)
    {
        foreach (var c in line)
        {
            Checks.That(c is not (< ' ' or (>= '\u007f' and <= '\u009f') or '\u2028' or '\u2029'), $"the line holds U+{(int)c:X4}, which a reader takes for a line end or a control character");
        }
    }

    private static void CheckAgainstFile(in AccessLogEntry entry, AccessLogFormatOptions options, AccessLogTailEntry tail, List<(string Text, bool Quoted)> file)
    {
        Checks.That(file.Count == 20, $"a plain reader finds {file.Count} fields in the line, not 20");

        // The numbers: the line's time, status, sizes and durations are the tail's, written as text.
        Checks.That(
            DateTime.TryParseExact(file[0].Text, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time)
            && time.Ticks == tail.TimestampUtcTicks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond,
            $"the line's time '{file[0].Text}' is not the tail's {tail.TimestampUtcTicks}");
        Checks.That(
            tail.StatusCode <= 0 ? file[4] == ("-", false) : file[4].Text == tail.StatusCode.ToString(CultureInfo.InvariantCulture),
            $"the line's status '{file[4].Text}' is not the tail's {tail.StatusCode}");
        Checks.That(
            tail.ResponseBytes < 0 ? file[5] == ("-", false) : file[5].Text == tail.ResponseBytes.ToString(CultureInfo.InvariantCulture),
            $"the line's size '{file[5].Text}' is not the tail's {tail.ResponseBytes}");
        Checks.That(Milliseconds(file[6].Text, tail.TotalTicks), $"the line's duration '{file[6].Text}' is not the tail's {tail.TotalTicks} ticks");
        Checks.That(tail.UpstreamTicks < 0 ? file[7] == ("-", false) : Milliseconds(file[7].Text, tail.UpstreamTicks), $"the line's upstream duration '{file[7].Text}' is not the tail's {tail.UpstreamTicks} ticks");
        SameStage(file[13], tail.TlsHandshakeTicks, "TLS handshake");
        Checks.That(
            tail.ConnectionRequest <= 0 ? file[14] == ("-", false) : file[14].Text == tail.ConnectionRequest.ToString(CultureInfo.InvariantCulture),
            $"the line's connection request '{file[14].Text}' is not the tail's {tail.ConnectionRequest}");
        SameStage(file[15], tail.RequestBodyTicks, "request body");
        SameStage(file[16], tail.GateTicks, "gate");
        SameStage(file[17], tail.UpstreamConnectTicks, "upstream connect");
        SameStage(file[18], tail.ResponseSendTicks, "response send");
        SameStage(file[19], tail.UpstreamWaitTicks, "upstream wait");

        // The client address is one the proxy formatted: unquoted, as it is.
        Checks.That(
            entry.ClientAddress is null ? file[1] == ("-", false) && tail.ClientAddress is null : file[1] == (entry.ClientAddress, false) && tail.ClientAddress == entry.ClientAddress,
            "the client address differs between the line and the tail");

        // The fields that are one quoted field in each: the same text, whatever the caps did to it.
        SameText(file[2], tail.Host, entry.Host, AccessLogFormatter.MaxHostLength, "host");
        SameText(file[8], tail.UpstreamAddress, entry.UpstreamAddress, AccessLogFormatter.MaxUpstreamLength, "upstream");
        SameText(file[9], tail.RequestId, entry.RequestId, 128, "request ID");
        SameText(file[10], tail.CloudflareRay, entry.CloudflareRay, AccessLogFormatter.MaxCloudflareRayLength, "CF-Ray");
        SameText(file[11], tail.Referer, entry.Referer, AccessLogFormatter.MaxHeaderValueLength, "referer");
        SameText(file[12], tail.UserAgent, entry.UserAgent, AccessLogFormatter.MaxHeaderValueLength, "user agent");

        // The request target is one field in the line and four in the tail.
        var method = Escape(entry.Method);
        var path = Escape(entry.Path);
        var protocol = Escape(entry.Protocol);
        var modelQuery = options.LogQueryString && entry.Query is { Length: > 0 } raw ? ModelQuery(raw, options) : null;
        var query = modelQuery is null ? null : Escape(modelQuery);

        Checks.That(options.LogQueryString || tail.Query is null, "a tail entry holds a query with the query string off");
        Checks.That(file[3].Quoted && file[3].Text.Length <= AccessLogFormatter.MaxRequestTargetLength + AccessLogFormatter.TruncationMarker.Length, "the request target is not one quoted field within its cap");
        SameTail(tail.Method, entry.Method, method, AccessLogFormatter.MaxMethodLength, "method");
        SameTail(tail.Path, entry.Path, path, AccessLogFormatter.MaxPathLength, "path");
        SameTail(tail.Protocol, entry.Protocol, protocol, AccessLogFormatter.MaxProtocolLength, "protocol");
        if (query is not null)
        {
            // Two markers' room: the redaction closes a cut query with one, and a query whose escapes then run over the budget is cut again and closed with another.
            Checks.That(tail.Query is not null && SameOrCut(tail.Query, query, AccessLogFormatter.MaxQueryLength + (2 * AccessLogFormatter.TruncationMarker.Length), doubleMarker: true), $"the tail's query is not the redacted query: {Difference(tail.Query, query)}");
        }

        // With no cap in play, the line's target is exactly what the tail's four fields say.
        var plain = !modelQuery?.Any(static c => c > '\u007f') ?? true;
        var uncut = Length(entry.Method) <= AccessLogFormatter.MaxMethodLength && Length(entry.Path) <= AccessLogFormatter.MaxPathLength && Length(entry.Protocol) <= AccessLogFormatter.MaxProtocolLength
            && (entry.Query?.Length ?? 0) <= AccessLogFormatter.MaxQueryLength && (query?.Length ?? 0) <= AccessLogFormatter.MaxQueryLength
            && method.Length <= AccessLogFormatter.MaxMethodLength && path.Length <= AccessLogFormatter.MaxPathLength;
        if (uncut && plain)
        {
            var target = $"{(method.Length == 0 ? "-" : method)} {(path.Length == 0 ? "-" : path)}{query}";
            var expected = $"{target} {(protocol.Length == 0 ? "-" : protocol)}";
            if (expected.Length <= AccessLogFormatter.MaxRequestTargetLength)
            {
                Checks.That(file[3].Text == expected, $"the line's target is '{file[3].Text}', a plain model says '{expected}'");
            }
        }
    }

    // A quoted field: bare "-" when the entry has none, else the full escape of the text, or a prefix of it closed by the marker when the cap cut it.
    private static void SameText((string Text, bool Quoted) field, string? shown, string? raw, int cap, string what)
    {
        if (string.IsNullOrEmpty(raw))
        {
            Checks.That(field == ("-", false) && shown is null, $"{what}: the entry has none, the line says '{field.Text}' and the tail '{shown}'");
            return;
        }

        Checks.That(field.Quoted && shown is not null && shown == field.Text, $"{what}: the line says '{field.Text}', the tail '{shown}'");
        Checks.That(SameOrCut(field.Text, Escape(raw), cap + AccessLogFormatter.TruncationMarker.Length), $"{what}: '{field.Text}' is neither the escape of the text nor a cut of it");
    }

    private static void SameTail(string? shown, string? raw, string full, int cap, string what)
    {
        if (string.IsNullOrEmpty(raw))
        {
            Checks.That(shown is null, $"{what}: the entry has none and the tail shows '{shown}'");
            return;
        }

        Checks.That(shown is not null && SameOrCut(shown, full, cap + AccessLogFormatter.TruncationMarker.Length), $"{what}: the tail shows '{shown}', which is neither '{full}' nor a cut of it");
    }

    private static string Difference(string? shown, string full)
    {
        shown ??= string.Empty;
        var at = 0;
        while (at < shown.Length && at < full.Length && shown[at] == full[at])
        {
            at++;
        }

        return $"{shown.Length} and {full.Length} characters, first different at {at}: '{shown[Math.Max(0, at - 20)..Math.Min(shown.Length, at + 20)]}' against '{full[Math.Max(0, at - 20)..Math.Min(full.Length, at + 20)]}'";
    }

    // The text, or a prefix of its escape closed by the marker. With <paramref name="doubleMarker"/> the prefix itself may end in some of the first marker's dots, which the second cut leaves behind.
    private static bool SameOrCut(string shown, string full, int limit, bool doubleMarker = false)
    {
        if (shown.Length > limit)
        {
            return false;
        }

        if (shown == full)
        {
            return true;
        }

        if (!shown.EndsWith(AccessLogFormatter.TruncationMarker, StringComparison.Ordinal))
        {
            return false;
        }

        var kept = shown[..^AccessLogFormatter.TruncationMarker.Length];
        for (var dots = 0; dots <= (doubleMarker ? AccessLogFormatter.TruncationMarker.Length : 0) && dots <= kept.Length; dots++)
        {
            if (kept[^dots..].All(static c => c == '.') && full.StartsWith(kept[..^dots], StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void CheckSecrets(in AccessLogEntry entry, AccessLogFormatOptions options, string line, AccessLogTailEntry tail, bool secretInQuery, bool plainInQuery)
    {
        string[] outputs = [line, .. Texts(tail)];
        if (secretInQuery)
        {
            Checks.That(outputs.All(static o => !o.Contains(Secret, StringComparison.Ordinal)), "a sensitive parameter's value reached an output");
        }

        // The sentinel in a plain parameter shows that the query is written when it is on (unless a cap cut it, which it can't at the front) and not a byte of it when it is off.
        if (plainInQuery && !options.LogQueryString)
        {
            Checks.That(outputs.All(static o => !o.Contains(Plain, StringComparison.Ordinal)), "a query byte reached an output with the query string off");
        }

        if (plainInQuery && options.LogQueryString)
        {
            // The tail keeps the query's front whatever the path was. In the line the query comes after the method and the path, which may have used up the target's room.
            Checks.That(tail.Query?.Contains(Plain, StringComparison.Ordinal) == true, "the query string is on and its plain parameter is missing from the tail");
            if (Escape(entry.Method).Length + Escape(entry.Path).Length < AccessLogFormatter.MaxRequestTargetLength - 512)
            {
                Checks.That(line.Contains(Plain, StringComparison.Ordinal), "the query string is on and its plain parameter is missing from the line");
            }
        }

        if (!options.LogQueryString
            && LeakedQueryValue(entry.Query, [entry.Host, entry.Method, entry.Path, entry.Protocol, entry.UpstreamAddress, entry.RequestId, entry.CloudflareRay, entry.Referer, entry.UserAgent], outputs) is { } leaked)
        {
            throw new PropertyViolationException($"a value of the query reached an output with the query string off: '{leaked}'");
        }
    }

    // No value of the query: each parameter's value (past its '=') is searched for in what was written, when it is long enough to be told from the rest of the line. A value that
    // another field of the entry carries is not a leak, and "carries" is read on the escaped text, which is what an output holds: an escape's own characters (the "7" of "\x07")
    // can join the plain text next to it into a value the field never held.
    internal static string? LeakedQueryValue(string? query, IEnumerable<string?> otherFields, IReadOnlyList<string> outputs)
    {
        if (query is not { Length: > 0 })
        {
            return null;
        }

        string[] escapedFields = [.. otherFields.Select(Escape)];
        foreach (var parameter in query.TrimStart('?').Split('&'))
        {
            var value = parameter.Contains('=') ? parameter[(parameter.IndexOf('=') + 1)..] : string.Empty;
            if (value.Length >= 12
                && value.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'z' or >= 'A' and <= 'Z')
                && !escapedFields.Any(field => field.Contains(value, StringComparison.Ordinal))
                && outputs.Any(o => o.Contains(value, StringComparison.Ordinal)))
            {
                return value;
            }
        }

        return null;
    }

    private static IEnumerable<string> Texts(AccessLogTailEntry tail) =>
        new[] { tail.ClientAddress, tail.Host, tail.Method, tail.Path, tail.Query, tail.Protocol, tail.UpstreamAddress, tail.RequestId, tail.CloudflareRay, tail.Referer, tail.UserAgent }.OfType<string>();

    private static bool SameFields(AccessLogTailEntry a, AccessLogTailEntry b) =>
        Texts(a).SequenceEqual(Texts(b)) && a.StatusCode == b.StatusCode && a.ResponseBytes == b.ResponseBytes && a.TotalTicks == b.TotalTicks && a.UpstreamTicks == b.UpstreamTicks && a.TimestampUtcTicks == b.TimestampUtcTicks
        && a.TlsHandshakeTicks == b.TlsHandshakeTicks && a.ConnectionRequest == b.ConnectionRequest && a.RequestBodyTicks == b.RequestBodyTicks
        && a.GateTicks == b.GateTicks && a.UpstreamConnectTicks == b.UpstreamConnectTicks && a.ResponseSendTicks == b.ResponseSendTicks
        && a.UpstreamWaitTicks == b.UpstreamWaitTicks && a.ForwardStartMicros == b.ForwardStartMicros && a.UpstreamConnectStartMicros == b.UpstreamConnectStartMicros
        && a.RequestBodyStartMicros == b.RequestBodyStartMicros;

    private static void SameStage((string Text, bool Quoted) field, long ticks, string what) =>
        Checks.That(ticks < 0 ? field == ("-", false) : !field.Quoted && Milliseconds(field.Text, ticks), $"the line's {what} '{field.Text}' is not the tail's {ticks} ticks");

    private static bool Milliseconds(string text, long ticks) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds) && Math.Abs((milliseconds * TimeSpan.TicksPerMillisecond) - ticks) <= (TimeSpan.TicksPerMillisecond / 1000.0 * 5) + 1;

    private static int Length(string? value) => value?.Length ?? 0;

    // The query as a plain reading of the rules gives it (leading '?' kept, each parameter whose name is sensitive written as name=REDACTED). A name with a non-ASCII character
    // is left to the proxy's own comparison, so a name the model reads as plain may be redacted there, and then the model is not used.
    private static string ModelQuery(string raw, AccessLogFormatOptions options)
    {
        var parameters = raw.StartsWith('?') ? raw[1..].Split('&') : raw.Split('&');
        var rewritten = parameters.Select(parameter =>
        {
            var equals = parameter.IndexOf('=');
            var name = equals < 0 ? parameter : parameter[..equals];
            return options.SensitiveQueryKeys.Any(key => AsciiLower(key) == AsciiLower(name)) ? name + "=REDACTED" : parameter;
        });
        return "?" + string.Join('&', rewritten);
    }

    private static string AsciiLower(string text) => string.Create(text.Length, text, static (span, source) =>
    {
        for (var i = 0; i < span.Length; i++)
        {
            span[i] = source[i] is >= 'A' and <= 'Z' ? (char)(source[i] + 32) : source[i];
        }
    });

    // The documented escapes, written plainly.
    private static string Escape(string? text)
    {
        StringBuilder escaped = new();
        foreach (var c in text ?? string.Empty)
        {
            switch (c)
            {
                case '"':
                    escaped.Append("\\\"");
                    break;
                case '\\':
                    escaped.Append("\\\\");
                    break;
                case '\u2028' or '\u2029':
                    escaped.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    break;
                case < ' ' or (>= '\u007f' and <= '\u009f'):
                    escaped.Append("\\x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
                    break;
                default:
                    escaped.Append(c);
                    break;
            }
        }

        return escaped.ToString();
    }

    // The line as the grammar says: tokens split by one space, a quoted token running to its closing quote with \" and \\ inside; the text is kept as written, escapes and all.
    private static List<(string Text, bool Quoted)> ReadFields(string line)
    {
        List<(string, bool)> fields = [];
        var i = 0;
        while (i <= line.Length)
        {
            if (i < line.Length && line[i] == '"')
            {
                var j = i + 1;
                while (j < line.Length && line[j] != '"')
                {
                    j += line[j] == '\\' ? 2 : 1;
                }

                Checks.That(j < line.Length, "a quoted field is not closed");
                fields.Add((line[(i + 1)..j], true));
                i = j + 1;
            }
            else
            {
                var space = line.IndexOf(' ', i);
                var end = space < 0 ? line.Length : space;
                fields.Add((line[i..end], false));
                i = end;
            }

            if (i == line.Length)
            {
                break;
            }

            Checks.That(line[i] == ' ', "two fields are not separated by one space");
            i++;
        }

        return fields;
    }

    private static string?[] Fields(ReadOnlySpan<byte> bytes, int decode)
    {
        var fields = new string?[10];
        for (var i = 0; i < fields.Length; i++)
        {
            var end = bytes.IndexOf((byte)0xFF);
            var piece = end < 0 ? bytes : bytes[..end];
            fields[i] = piece.IsEmpty ? null : decode switch
            {
                1 => Encoding.Latin1.GetString(piece),
                2 => Encoding.Unicode.GetString(piece),
                _ => Encoding.UTF8.GetString(piece),
            };
            bytes = end < 0 ? default : bytes[(end + 1)..];
        }

        return fields;
    }

    private static IEnumerable<(string Name, byte[] Bytes)> Seeds()
    {
        // flags: 1 no query string, 2 custom sensitive keys, 4 a secret in the query, 8 a plain sentinel, 16 Latin-1, 32 UTF-16 (x16).
        static byte[] Case(int flags, int status, params string[] fields)
        {
            List<byte> bytes = [(byte)flags, (byte)(status & 0xFF), (byte)(status >> 8), 7, 5, 3, 9, 1];
            for (var i = 0; i < fields.Length; i++)
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(fields[i]));
                bytes.Add(0xFF);
            }

            return [.. bytes];
        }

        static string[] Normal(string path = "/a/b", string query = "?x=1&token=abc&y=2", string agent = "Mozilla/5.0") =>
            ["app.example:443", "GET", path, query, "HTTP/2", "http://10.0.0.2:8080/", "0k3m9x2p-00000000000abc", "8f2a1c3d4e5f6789", "https://example.com/", agent];

        yield return ("plain", Case(0, 200, Normal()));
        yield return ("query-off", Case(1, 200, Normal()));
        yield return ("secret-front", Case(4 | 8, 200, Normal()));
        yield return ("secret-front-query-off", Case(1 | 4 | 8, 200, Normal()));
        yield return ("custom-keys", Case(2 | 4, 200, Normal(query: "?t=abc&X-TOKEN=def&token=ghi")));
        yield return ("bare-sensitive-name", Case(0, 200, Normal(query: "?token&sig=&Code=1")));
        yield return ("forged-line", Case(0, 200, Normal(path: "/a\r\n2026-01-31T00:00:00.000Z 203.0.113.9 \"forged\" \"GET /admin HTTP/1.1\" 200 1 0.1 - - - - - -", agent: "x\u0085y\u2028z\u2029\u001b\u007f")));
        yield return ("hostile-every-field", Case(0, 200, "h\"o\\st\r\n", "G\u0085T", "/p\u2028q", "?a=\"\n&sig=\u2029", "HTTP/\u007f", "u\"p\\", "i\nd", "r\u0085y", "ref\r\n2026-01-31T00:00:00.000Z 203.0.113.9 \"forged\"", "ua\u2028\u2029\u001b"));
        yield return ("quotes-and-backslashes", Case(0, 404, Normal(path: "/\"\\\"\\x0a\\", agent: "\"\"\\\\")));
        yield return ("empty-query", Case(0, 200, Normal(query: "?")));
        yield return ("amp-only", Case(0, 200, Normal(query: "?&&&=&=x&token=")));
        yield return ("long-path", Case(0, 200, Normal(path: "/" + new string('a', 1500))));
        yield return ("long-escapes", Case(0, 200, Normal(path: "/" + new string('\n', 600))));
        yield return ("long-query", Case(0, 200, Normal(query: "?" + string.Join('&', Enumerable.Range(0, 200).Select(i => $"p{i}=v{i}")) + "&token=" + new string('s', 100))));
        yield return ("long-sensitive-straddle", Case(4, 200, Normal(query: "?" + new string('q', 1018) + "&token=" + new string('s', 50))));
        yield return ("long-headers", Case(0, 200, Normal(agent: new string('u', 600))));
        yield return ("no-values", Case(0, 0, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty));
        yield return ("non-ascii", Case(0, 200, Normal(path: "/caf\u00e9/\U0001F600", query: "?q=\u00fc&token=\u00e9", agent: "\U0001F600")));
        yield return ("status-zero", Case(0, 0, Normal()));
    }
}
