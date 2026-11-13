using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Text;
using BenchmarkDotNet.Attributes;
using Paduraksa.Proxy.Net;
using Paduraksa.ProxyProtocol;

namespace Paduraksa.Benchmarks;

/// <summary>
/// <see cref="ProxyProtocolParser"/> on a header followed by the start of a TLS ClientHello, as the connection
/// middleware sees it: v1 TCP4 / TCP6 text, v2 IPv4, and v2 IPv6 with 40 bytes of TLVs (AWS NLB / GCP style). The
/// last case also runs over a two-segment <see cref="ReadOnlySequence{T}"/> split inside the header.
/// </summary>
[MemoryDiagnoser]
public class ProxyProtocolBenchmarks
{
    private static readonly byte[] Signature = [0x0D, 0x0A, 0x0D, 0x0A, 0x00, 0x0D, 0x0A, 0x51, 0x55, 0x49, 0x54, 0x0A];
    private static readonly byte[] ClientHelloStart = [0x16, 0x03, 0x01, 0x02, 0x00, 0x01, 0x00, 0x01, 0xFC, 0x03, 0x03];

    private readonly byte[] _v1Tcp4 = WithPayload(Encoding.ASCII.GetBytes("PROXY TCP4 203.0.113.7 192.0.2.1 50123 443\r\n"));
    private readonly byte[] _v1Tcp6 = WithPayload(Encoding.ASCII.GetBytes("PROXY TCP6 2001:db8:85a3::8a2e:370:7334 2001:db8::1 50123 443\r\n"));
    private readonly byte[] _v2Ipv4 = WithPayload(V2("203.0.113.7", "192.0.2.1", tlvBytes: 0));
    private readonly byte[] _v2Ipv6Tlv = WithPayload(V2("2001:db8:85a3::8a2e:370:7334", "2001:db8::1", tlvBytes: 40));
    private ReadOnlySequence<byte> _v2Ipv6TlvSplit;

    [GlobalSetup]
    public void Setup()
    {
        _v2Ipv6TlvSplit = Split(_v2Ipv6Tlv, at: 20);
        foreach (var input in new[] { _v1Tcp4, _v1Tcp6, _v2Ipv4, _v2Ipv6Tlv })
        {
            if (ProxyProtocolParser.TryParse(input, out var header, out var _) != ProxyProtocolParseStatus.Success || !header.HasAddresses)
            {
                throw new InvalidOperationException("A benchmark input doesn't parse.");
            }
        }

        if (ProxyProtocolParser.TryParse(in _v2Ipv6TlvSplit, out _, out var _) != ProxyProtocolParseStatus.Success)
        {
            throw new InvalidOperationException("The split input doesn't parse.");
        }
    }

    [Benchmark]
    public IpKey V1Tcp4() => Parse(_v1Tcp4);

    [Benchmark]
    public IpKey V1Tcp6() => Parse(_v1Tcp6);

    [Benchmark]
    public IpKey V2IPv4() => Parse(_v2Ipv4);

    [Benchmark]
    public IpKey V2IPv6WithTlvs() => Parse(_v2Ipv6Tlv);

    [Benchmark]
    public IpKey V2IPv6WithTlvsSplit()
    {
        _ = ProxyProtocolParser.TryParse(in _v2Ipv6TlvSplit, out var header, out var _);
        return header.SourceAddress;
    }

    private static IpKey Parse(byte[] input)
    {
        _ = ProxyProtocolParser.TryParse(input, out var header, out var _);
        return header.SourceAddress;
    }

    private static byte[] WithPayload(byte[] header) => [.. header, .. ClientHelloStart];

    /// <summary>A v2 PROXY command over TCP (INET or INET6 by the address family) with zero-filled TLV bytes.</summary>
    private static byte[] V2(string source, string destination, int tlvBytes)
    {
        var src = IPAddress.Parse(source);
        var dst = IPAddress.Parse(destination);
        var v6 = src.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        var addressLength = v6 ? 36 : 12;
        var header = new byte[16 + addressLength + tlvBytes];
        Signature.CopyTo(header, 0);
        header[12] = 0x21; // version 2, PROXY
        header[13] = (byte)(v6 ? 0x21 : 0x11); // INET6/INET, STREAM
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(14), (ushort)(addressLength + tlvBytes));
        var offset = 16;
        src.TryWriteBytes(header.AsSpan(offset), out var written);
        offset += written;
        dst.TryWriteBytes(header.AsSpan(offset), out written);
        offset += written;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(offset), 50123);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(offset + 2), 443);
        if (tlvBytes > 0)
        {
            // One PP2_TYPE_NOOP TLV covering the rest.
            header[offset + 4] = 0x04;
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(offset + 5), (ushort)(tlvBytes - 3));
        }

        return header;
    }

    private static ReadOnlySequence<byte> Split(byte[] data, int at)
    {
        Segment first = new(data.AsMemory(0, at));
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
