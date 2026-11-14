using System.Text;

using CsCheck;

using Paduraksa.Proxy.Logging;
using Paduraksa.Tests.Shared;

namespace Paduraksa.Tests.Proxy.Logging;

/// <summary>
/// No path, query, user agent, referer or host a client can send breaks the line or forges one. A plain reader of the documented grammar
/// (space-separated tokens, quoted fields with <c>\"</c>, <c>\\</c> and <c>\xNN</c> escapes) must find exactly nineteen fields in what the formatter wrote,
/// the quoted ones must decode to what the client sent (cut at the field's cap and marked), nothing that any common reader takes for a line end may appear,
/// and a sensitive query value must never reach the line.
/// </summary>
public sealed class AccessLogFormatterPropertyTests
{
    private const string Canary = "ZZSECRETZZ";

    // What a client can put in a request: quotes and backslashes, every kind of line end (C0, NEL, LS, PS), a DEL, text that looks like an escape or like the next field, and non-ASCII.
    private static readonly string[] Pieces =
    [
        "a", "b", " ", " ", "/", "?", "&", "=", "\"", "\\", "\r", "\n", "\r\n", "\t", "\0", "\u007f", "\u0085", "\u2028", "\u2029", "\u001b", "\u00e9", "\ud83d\ude00", "\\x0a", "\\\"", "x", "token=", "sig=",
        "...", "%", "\" 200 5 0.1 0.1 \"-\" \"-\" \"-\" \"-\" \"-\"", "\n2026-01-31T00:00:00.000Z 203.0.113.9 \"forged\" \"GET /admin HTTP/1.1\" 200 1 0.1 - - - - - -",
    ];

    private static readonly string[] LongPieces = [new string('a', 700), new string('\n', 300), new string('"', 700), new string('b', 1500)];

    private static readonly Gen<string?> Text = Gen.Frequency(
        (1, Gen.Const(static () => (string?)null)),
        (1, Gen.Const(static () => (string?)string.Empty)),
        (14, Gen.Int[0, Pieces.Length - 1].Select(i => Pieces[i]).List[0, 8].Select(static parts => (string?)string.Concat(parts))),
        (1, Gen.Int[0, LongPieces.Length - 1].Select(i => (string?)LongPieces[i])));

    private static readonly Gen<AccessLogEntry> Entries = Gen.Select(
        Gen.Select(Text, Text, Text, Text, Text, Text),
        Gen.Select(Text, Text, Text, Text),
        Gen.Select(Gen.Int[-1, 600], Gen.Long[-1, 100_000], Gen.Long[0, 10_000_000_000], Gen.Long[-1, 10_000_000_000]),
        Gen.Select(Gen.Long[-2, 10_000_000_000], Gen.Int[0, 100_000], Gen.Long[-1, 10_000_000_000], Gen.Long[-1, 10_000_000_000], Gen.Long[-1, 10_000_000_000], Gen.Long[-1, 10_000_000_000]),
        static (a, b, c, d) => new AccessLogEntry
        {
            TimestampUtcTicks = new DateTime(2026, 1, 31, 12, 34, 56, DateTimeKind.Utc).Ticks,
            // The address is one the proxy formatted itself, never a client's string, so it is the one field that isn't escaped.
            ClientAddress = c.Item1 % 3 == 0 ? null : c.Item1 % 3 == 1 ? "198.51.100.7" : "2001:db8::1",
            Host = a.Item2,
            Method = a.Item3,
            Path = a.Item4,
            Query = a.Item5,
            Protocol = a.Item6,
            UpstreamAddress = b.Item1,
            RequestId = b.Item2,
            CloudflareRay = b.Item3,
            Referer = b.Item4,
            UserAgent = a.Item1 is null ? null : b.Item4,
            StatusCode = c.Item1,
            ResponseBytes = c.Item2,
            TotalTicks = c.Item3,
            UpstreamTicks = c.Item4,
            TlsHandshakeTicks = d.Item1,
            ConnectionRequest = d.Item2,
            RequestBodyTicks = d.Item3,
            GateTicks = d.Item4,
            UpstreamConnectTicks = d.Item5,
            ResponseSendTicks = d.Item6,
        });

    [Fact]
    public void ALineHasExactlyTheDocumentedFieldsWhateverTheClientSent() =>
        Entries.Check(
            12_000,
            entry =>
            {
                var line = Format(entry, AccessLogFormatOptions.Default);

                Assert.True(line.Length <= AccessLogFormatter.MaxLineLength, $"{line.Length} characters");
                Assert.True(line.All(static c => !(char.IsControl(c) || c is '\u2028' or '\u2029')), "a character a reader may take for the end of a line: " + line);
                var fields = Reader.Fields(line);

                Assert.Equal(19, fields.Count);
                CheckQuoted(fields[2], entry.Host, AccessLogFormatter.MaxHostLength);
                CheckQuoted(fields[8], entry.UpstreamAddress, AccessLogFormatter.MaxUpstreamLength);
                CheckQuoted(fields[9], entry.RequestId, RequestIds.MaxLength);
                CheckQuoted(fields[10], entry.CloudflareRay, AccessLogFormatter.MaxCloudflareRayLength);
                CheckQuoted(fields[11], entry.Referer, AccessLogFormatter.MaxHeaderValueLength);
                CheckQuoted(fields[12], entry.UserAgent, AccessLogFormatter.MaxHeaderValueLength);
                CheckDuration(fields[13], entry.TlsHandshakeTicks);
                Assert.Equal(entry.ConnectionRequest > 0 ? entry.ConnectionRequest.ToString(System.Globalization.CultureInfo.InvariantCulture) : null, fields[14]);
                CheckDuration(fields[15], entry.RequestBodyTicks);
                CheckDuration(fields[16], entry.GateTicks);
                CheckDuration(fields[17], entry.UpstreamConnectTicks);
                CheckDuration(fields[18], entry.ResponseSendTicks);
            });

    // A duration reads back to its microseconds (the line's resolution), and any unknown value is a bare dash.
    private static void CheckDuration(string? field, long ticks)
    {
        if (ticks < 0)
        {
            Assert.Null(field);
            return;
        }

        Assert.NotNull(field);
        var milliseconds = double.Parse(field, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(ticks / (double)TimeSpan.TicksPerMillisecond, milliseconds, 0.0005 + (ticks / (double)TimeSpan.TicksPerMillisecond * 1e-12));
    }

    [Fact]
    public void TheRequestTargetDecodesToWhatTheClientSentWithItsSensitiveParametersRedacted() =>
        Gen.Select(Text, Text, Property.StringOf("ab&=?%", 0, 12), Text, Gen.Bool).Check(
            12_000,
            t =>
            {
                var (method, path, noise, protocol, withCanary) = t;
                var query = withCanary ? "?" + string.Join("&", noise.Split('&').Select((p, i) => i % 2 == 0 ? $"token={Canary}" : p)) : noise.Length == 0 ? null : "?" + noise;
                var entry = new AccessLogEntry { Method = method, Path = path, Query = query, Protocol = protocol, StatusCode = 200 };

                var line = Format(entry, AccessLogFormatOptions.Default);
                var fields = Reader.Fields(line);

                Assert.Equal(19, fields.Count);
                Assert.DoesNotContain(Canary, line, StringComparison.Ordinal);
                var expected = $"{OrDash(method, AccessLogFormatter.MaxMethodLength)} {OrDash(path, AccessLogFormatter.MaxPathLength)}{Redacted(query)} {OrDash(protocol, AccessLogFormatter.MaxProtocolLength)}";
                var decoded = Assert.IsType<string>(fields[3]);
                if (expected.Length <= AccessLogFormatter.MaxRequestTargetLength && Escaped(expected) <= AccessLogFormatter.MaxRequestTargetLength
                    && (method?.Length ?? 0) <= AccessLogFormatter.MaxMethodLength && (path?.Length ?? 0) <= AccessLogFormatter.MaxPathLength
                    && (protocol?.Length ?? 0) <= AccessLogFormatter.MaxProtocolLength && (query?.Length ?? 0) <= AccessLogFormatter.MaxQueryLength)
                {
                    Assert.Equal(expected, decoded);
                }
                else
                {
                    Assert.EndsWith(AccessLogFormatter.TruncationMarker, decoded, StringComparison.Ordinal);
                }
            });

    private static void CheckQuoted(string? decoded, string? sent, int cap)
    {
        if (string.IsNullOrEmpty(sent))
        {
            Assert.Null(decoded);
            return;
        }

        Assert.NotNull(decoded);
        if (Escaped(sent) <= cap)
        {
            Assert.Equal(sent, decoded);
            return;
        }

        // Too long once escaped: cut at the cap, as a prefix of what was sent, and marked.
        Assert.EndsWith(AccessLogFormatter.TruncationMarker, decoded, StringComparison.Ordinal);
        var kept = decoded[..^AccessLogFormatter.TruncationMarker.Length];
        Assert.StartsWith(kept, sent, StringComparison.Ordinal);
        Assert.True(Escaped(kept) <= cap, $"{Escaped(kept)} escaped characters kept of {cap}");
    }

    private static string OrDash(string? value, int cap) => string.IsNullOrEmpty(value) ? "-" : value.Length > cap ? value[..cap] : value;

    // The query as the target shows it: sensitive parameters as name=REDACTED, the rest as sent.
    private static string Redacted(string? query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return string.Empty;
        }

        var parameters = (query[0] == '?' ? query[1..] : query).Split('&');
        return "?" + string.Join(
            "&",
            parameters.Select(parameter =>
            {
                var name = parameter.Split('=')[0];
                return AccessLogFormatOptions.DefaultSensitiveQueryKeys.Contains(name, StringComparer.OrdinalIgnoreCase) ? name + "=REDACTED" : parameter;
            }));
    }

    // How many characters the formatter writes for a value: a quote or backslash takes two, a control four (and a Unicode line separator six).
    private static int Escaped(string value)
    {
        var size = 0;
        foreach (var c in value)
        {
            size += c is '"' or '\\' ? 2 : char.IsControl(c) ? 4 : c is '\u2028' or '\u2029' ? 6 : 1;
        }

        return size;
    }

    private static string Format(AccessLogEntry entry, AccessLogFormatOptions options)
    {
        var buffer = new char[AccessLogFormatter.MaxLineLength];
        var length = AccessLogFormatter.Format(in entry, options, buffer);
        Assert.True(length >= 0, "the line overflowed the buffer of MaxLineLength");
        return new string(buffer, 0, length);
    }

    /// <summary>The grammar in the formatter's remarks, read the plain way.</summary>
    private static class Reader
    {
        public static List<string?> Fields(string line)
        {
            List<string?> fields = [];
            var at = 0;
            while (true)
            {
                if (at < line.Length && line[at] == '"')
                {
                    StringBuilder value = new();
                    at++;
                    while (true)
                    {
                        Assert.True(at < line.Length, "an unterminated quoted field: " + line);
                        var c = line[at++];
                        if (c == '"')
                        {
                            break;
                        }

                        if (c != '\\')
                        {
                            value.Append(c);
                            continue;
                        }

                        var escape = line[at++];
                        if (escape is '"' or '\\')
                        {
                            value.Append(escape);
                        }
                        else if (escape == 'x')
                        {
                            value.Append((char)Convert.ToInt32(line.Substring(at, 2), 16));
                            at += 2;
                        }
                        else
                        {
                            Assert.Equal('u', escape);
                            value.Append((char)Convert.ToInt32(line.Substring(at, 4), 16));
                            at += 4;
                        }
                    }

                    fields.Add(value.ToString());
                }
                else
                {
                    var end = line.IndexOf(' ', at);
                    var token = end < 0 ? line[at..] : line[at..end];
                    at += token.Length;
                    fields.Add(token == "-" ? null : token);
                }

                if (at >= line.Length)
                {
                    return fields;
                }

                Assert.Equal(' ', line[at++]);
            }
        }
    }
}
