using System.Buffers;
using System.Globalization;

using Paduraksa.Proxy.Net;
using Paduraksa.ProxyProtocol;

namespace Paduraksa.Fuzz;

/// <summary>
/// PROXY protocol v1 and v2 headers, the first bytes of a connection from a trusted proxy. The input is a byte for the v2 length cap, a byte for where the
/// buffer is split in two segments, and then the bytes themselves. Properties: no exception; the same verdict for the same bytes; the same verdict
/// whether the bytes arrive in one segment or two (the pipe delivers them either way); 0 B on one segment; a header that parses is the header of exactly
/// <c>consumed</c> bytes, parsed alone, with every shorter prefix of it incomplete; a valid-prefix verdict stays one for every shorter prefix.
/// </summary>
internal static class ProxyProtocolTarget
{
    public static FuzzTarget Target { get; } = new(
        "proxy-protocol",
        "PROXY protocol v1 and v2 header parser",
        Run,
        Seeds);

    private static readonly byte[] V2Signature = [0x0D, 0x0A, 0x0D, 0x0A, 0x00, 0x0D, 0x0A, 0x51, 0x55, 0x49, 0x54, 0x0A];

    // Well-formed headers of each kind, each behind the two bytes the target reads first (a cap near the default, no split), and one split in the middle.
    private static IEnumerable<(string Name, byte[] Bytes)> Seeds()
    {
        byte[] Wrap(byte[] header, byte split = 0) => [64, split, .. header, .. "GET / HTTP/1.1\r\n"u8.ToArray()];
        byte[] V2(byte versionCommand, byte familyTransport, byte[] block) => [.. V2Signature, versionCommand, familyTransport, (byte)(block.Length >> 8), (byte)block.Length, .. block];
        byte[] Ip4(byte a) => [192, 0, 2, a, 198, 51, 100, 2, 0xDC, 0x04, 0x01, 0xBB];
        byte[] ip6 = [0x20, 0x01, 0x0D, 0xB8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0x20, 0x01, 0x0D, 0xB8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0x03, 0xE8, 0x01, 0xBB];

        yield return ("v1-tcp4", Wrap("PROXY TCP4 192.0.2.1 198.51.100.2 56324 443\r\n"u8.ToArray()));
        yield return ("v1-tcp6", Wrap("PROXY TCP6 2001:db8::1 2001:db8::2 1000 443\r\n"u8.ToArray()));
        yield return ("v1-unknown", Wrap("PROXY UNKNOWN\r\n"u8.ToArray()));
        yield return ("v1-unknown-addresses", Wrap("PROXY UNKNOWN ffff:f33e::1 ffff:f33e::2 65535 65535\r\n"u8.ToArray()));
        yield return ("v1-split", Wrap("PROXY TCP4 192.0.2.1 198.51.100.2 56324 443\r\n"u8.ToArray(), 9));
        yield return ("v1-bad-port", Wrap("PROXY TCP4 192.0.2.1 198.51.100.2 056324 443\r\n"u8.ToArray()));
        yield return ("v1-no-crlf", Wrap("PROXY TCP4 192.0.2.1 198.51.100.2 56324 443"u8.ToArray()));
        yield return ("v2-proxy-tcp4", Wrap(V2(0x21, 0x11, Ip4(1))));
        yield return ("v2-proxy-tcp6", Wrap(V2(0x21, 0x21, ip6)));
        yield return ("v2-proxy-tcp4-split", Wrap(V2(0x21, 0x11, Ip4(1)), 14));
        yield return ("v2-local", Wrap(V2(0x20, 0x00, [])));
        yield return ("v2-tlv", Wrap(V2(0x21, 0x11, [.. Ip4(3), 0x04, 0x00, 0x04, 1, 2, 3, 4, 0x20, 0x00, 0x01, 0x07])));
        yield return ("v2-unix", Wrap(V2(0x21, 0x31, new byte[216])));
        yield return ("v2-unspec", Wrap(V2(0x21, 0x00, [1, 2, 3])));
        yield return ("v2-dgram", Wrap(V2(0x21, 0x12, Ip4(1))));
        yield return ("v2-version-3", Wrap(V2(0x31, 0x11, Ip4(1))));
        yield return ("v2-truncated", Wrap([.. V2(0x21, 0x11, Ip4(1))[..20]]));
        yield return ("v2-length-too-long", Wrap([.. V2Signature, 0x21, 0x11, 0xFF, 0xFF, 1, 2, 3]));
        yield return ("not-proxy", Wrap("GET / HTTP/1.1\r\nHost: a\r\n\r\n"u8.ToArray()));
        yield return ("signature-only", Wrap(V2Signature));
    }

    private static void Run(ReadOnlySpan<byte> input)
    {
        if (input.Length < 2)
        {
            return;
        }

        // The cap is anywhere from the smallest the parser accepts (it refuses a lower one) to well above its default.
        var maxV2 = input[0] switch
        {
            < 64 => ProxyProtocolParser.V2MaxLengthWithoutTlvs + (input[0] * 4),
            < 128 => ProxyProtocolParser.DefaultMaxV2Length,
            _ => ProxyProtocolParser.DefaultMaxV2Length * (input[0] - 126),
        };
        var split = input[1];
        var data = input[2..].ToArray();

        var single = new ReadOnlySequence<byte>(data);
        var verdict = Checks.Deterministic(() => Describe(single, maxV2, out _, out _), "one segment");

        // The same bytes in two segments: the first `split % (length + 1)` bytes, then the rest.
        var at = split % (data.Length + 1);
        var segmented = Split(data, at);
        Checks.That(Describe(segmented, maxV2, out _, out _) == verdict, $"split at {at}: {Describe(segmented, maxV2, out _, out _)} but in one piece {verdict}");

        Checks.AllocatesNothing(() => _ = ProxyProtocolParser.TryParse(in single, out _, out _, maxV2), "ProxyProtocolParser.TryParse on one segment");

        var status = ProxyProtocolParser.TryParse(in single, out var header, out var consumed, maxV2);
        switch (status)
        {
            case ProxyProtocolParseStatus.Success:
                CheckSuccess(data, maxV2, header, consumed);
                break;
            case ProxyProtocolParseStatus.Incomplete:
                for (var length = 0; length < data.Length; length += Math.Max(1, data.Length / 16))
                {
                    var prefix = ProxyProtocolParser.TryParse(new ReadOnlySequence<byte>(data, 0, length), out _, out _, maxV2);
                    Checks.That(prefix == ProxyProtocolParseStatus.Incomplete, $"{data.Length} bytes are a valid prefix, but the first {length} are {prefix}");
                }

                break;
            default:
                Checks.That(status == ProxyProtocolParseStatus.Invalid, "an undefined status");
                break;
        }
    }

    private static void CheckSuccess(byte[] data, int maxV2, ProxyProtocolHeader header, long consumed)
    {
        Checks.That(consumed > 0 && consumed <= data.Length, $"consumed {consumed} of {data.Length}");
        Checks.That(header.Version is 1 or 2, "the version");
        Checks.That(header.HasAddresses || (header.SourceAddress == default && header.DestinationAddress == default), "addresses on a header that has none");

        // The header is its own `consumed` bytes: parsed alone it is the same, and no shorter prefix of it is a header or a refusal yet.
        var alone = ProxyProtocolParser.TryParse(new ReadOnlySequence<byte>(data, 0, (int)consumed), out var again, out var aloneConsumed, maxV2);
        Checks.That(alone == ProxyProtocolParseStatus.Success && aloneConsumed == consumed, $"the {consumed} header bytes alone: {alone}, consumed {aloneConsumed}");
        Checks.That(Describe(header) == Describe(again), "the header parsed alone differs");
        var step = (int)Math.Max(1, consumed / 24);
        for (var length = 0; length < consumed; length += step)
        {
            var prefix = ProxyProtocolParser.TryParse(new ReadOnlySequence<byte>(data, 0, length), out _, out _, maxV2);
            Checks.That(prefix == ProxyProtocolParseStatus.Incomplete, $"a header of {consumed} bytes, but the first {length} are {prefix}");
        }
    }

    private static string Describe(in ReadOnlySequence<byte> sequence, int maxV2, out ProxyProtocolHeader header, out long consumed)
    {
        var status = ProxyProtocolParser.TryParse(in sequence, out header, out consumed, maxV2);
        return status == ProxyProtocolParseStatus.Success ? $"{status} {consumed} {Describe(header)}" : status.ToString();
    }

    private static string Describe(ProxyProtocolHeader header) => string.Create(
        CultureInfo.InvariantCulture,
        $"v{header.Version} {header.Command} {header.HasAddresses} {header.SourceAddress}:{header.SourcePort} > {header.DestinationAddress}:{header.DestinationPort}");

    private static ReadOnlySequence<byte> Split(byte[] data, int at)
    {
        if (at == 0 || at == data.Length)
        {
            return new ReadOnlySequence<byte>(data);
        }

        var first = new Segment(data.AsMemory(0, at));
        var last = first.Append(data.AsMemory(at));
        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            Segment next = new(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }
}
