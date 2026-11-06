using System.Buffers;
using System.Buffers.Binary;

namespace Paduraksa.Proxy.Net;

public enum ProxyProtocolCommand : byte
{
    /// <summary>The connection was relayed on behalf of a client; the addresses (if any) are the client's.</summary>
    Proxy = 0,

    /// <summary>v2 only: the proxy's own connection (e.g. a health check); addresses must be ignored.</summary>
    Local = 1,
}

/// <summary>A parsed PROXY protocol header (v1 or v2).</summary>
public readonly struct ProxyProtocolHeader
{
    public ProxyProtocolHeader(
        int version,
        ProxyProtocolCommand command,
        bool hasAddresses,
        IpKey sourceAddress,
        ushort sourcePort,
        IpKey destinationAddress,
        ushort destinationPort)
    {
        Version = version;
        Command = command;
        HasAddresses = hasAddresses;
        SourceAddress = sourceAddress;
        SourcePort = sourcePort;
        DestinationAddress = destinationAddress;
        DestinationPort = destinationPort;
    }

    /// <summary>1 (text) or 2 (binary).</summary>
    public int Version { get; }

    public ProxyProtocolCommand Command { get; }

    /// <summary>False for v2 <c>LOCAL</c>, <c>AF_UNSPEC</c>, <c>AF_UNIX</c> or an unspecified transport, and for
    /// v1 <c>UNKNOWN</c>: the receiver must then use the socket's own endpoints.</summary>
    public bool HasAddresses { get; }

    public IpKey SourceAddress { get; }

    public ushort SourcePort { get; }

    public IpKey DestinationAddress { get; }

    public ushort DestinationPort { get; }
}

public enum ProxyProtocolParseStatus : byte
{
    /// <summary>The bytes so far are a valid prefix of a header; read more.</summary>
    Incomplete = 0,

    Success = 1,

    /// <summary>Not a valid PROXY protocol header; drop the connection.</summary>
    Invalid = 2,
}

/// <summary>
/// Allocation-free parser for HAProxy PROXY protocol v1 (text) and v2 (binary) headers
/// (https://www.haproxy.org/download/3.0/doc/proxy-protocol.txt).
/// </summary>
/// <remarks>
/// <para>Strictness: v1 must be <c>PROXY TCP4|TCP6 src dst sport dport\r\n</c> with single spaces, addresses
/// valid for the declared family under <see cref="IpParser"/>'s rules, decimal ports 0–65535 without leading
/// zeros, at most 107 bytes; or <c>PROXY UNKNOWN…\r\n</c>. v2 must have the 12-byte signature, version 2, command
/// <c>LOCAL</c> or <c>PROXY</c>, a known family and transport, an address block long enough for the family, and a
/// total length within the configured maximum. TLVs are skipped unvalidated (no CRC32C check). <c>DGRAM</c> is
/// rejected: this parser serves TCP listeners.</para>
/// <para>Garbage is rejected as soon as the first bytes can't start a header, without waiting for more data.</para>
/// </remarks>
public static class ProxyProtocolParser
{
    /// <summary>Longest v1 header, CRLF included.</summary>
    public const int V1MaxLength = 107;

    /// <summary>Fixed part of a v2 header: signature, version/command, family/transport, length.</summary>
    public const int V2FixedLength = 16;

    /// <summary>Default cap on a whole v2 header (fixed part + addresses + TLVs). The spec allows up to 65,551 bytes;
    /// real load balancers send well under 1 KB.</summary>
    public const int DefaultMaxV2Length = 4096;

    // Fixed part + the largest address block (AF_UNIX: 2 × 108 bytes). Also covers v1's 107 bytes.
    private const int PeekLength = V2FixedLength + 216;

    private static ReadOnlySpan<byte> V2Signature => [0x0D, 0x0A, 0x0D, 0x0A, 0x00, 0x0D, 0x0A, 0x51, 0x55, 0x49, 0x54, 0x0A];

    private static ReadOnlySpan<byte> V1Prefix => "PROXY "u8;

    /// <summary>Parses a header at the start of <paramref name="buffer"/>. On <see cref="ProxyProtocolParseStatus.Success"/>,
    /// <paramref name="consumed"/> is the header length; anything after it is application data.</summary>
    public static ProxyProtocolParseStatus TryParse(
        in ReadOnlySequence<byte> buffer,
        out ProxyProtocolHeader header,
        out long consumed,
        int maxV2Length = DefaultMaxV2Length)
    {
        var first = buffer.FirstSpan;
        if (buffer.IsSingleSegment || first.Length >= PeekLength)
        {
            return Parse(first, buffer.Length, maxV2Length, out header, out consumed);
        }

        Span<byte> peek = stackalloc byte[PeekLength];
        var length = (int)Math.Min(buffer.Length, PeekLength);
        buffer.Slice(0, length).CopyTo(peek);
        return Parse(peek[..length], buffer.Length, maxV2Length, out header, out consumed);
    }

    /// <summary>As <see cref="TryParse(in ReadOnlySequence{byte}, out ProxyProtocolHeader, out long, int)"/>, over
    /// a contiguous buffer.</summary>
    public static ProxyProtocolParseStatus TryParse(
        ReadOnlySpan<byte> buffer,
        out ProxyProtocolHeader header,
        out int consumed,
        int maxV2Length = DefaultMaxV2Length)
    {
        var status = Parse(buffer, buffer.Length, maxV2Length, out header, out var consumedLong);
        consumed = (int)consumedLong;
        return status;
    }

    // peek: the first bytes of the input (at least PeekLength when that many are available).
    // available: total bytes available, possibly more than peek holds (TLVs are only counted, never read).
    private static ProxyProtocolParseStatus Parse(
        ReadOnlySpan<byte> peek,
        long available,
        int maxV2Length,
        out ProxyProtocolHeader header,
        out long consumed)
    {
        header = default;
        consumed = 0;
        if (peek.Length == 0)
        {
            return ProxyProtocolParseStatus.Incomplete;
        }

        return peek[0] switch
        {
            0x0D => ParseV2(peek, available, maxV2Length, out header, out consumed),
            (byte)'P' => ParseV1(peek, out header, out consumed),
            _ => ProxyProtocolParseStatus.Invalid,
        };
    }

    private static ProxyProtocolParseStatus ParseV2(
        ReadOnlySpan<byte> peek,
        long available,
        int maxV2Length,
        out ProxyProtocolHeader header,
        out long consumed)
    {
        header = default;
        consumed = 0;

        var signatureBytes = Math.Min(peek.Length, V2Signature.Length);
        if (!peek[..signatureBytes].SequenceEqual(V2Signature[..signatureBytes]))
        {
            return ProxyProtocolParseStatus.Invalid;
        }

        if (peek.Length < V2FixedLength)
        {
            return ProxyProtocolParseStatus.Incomplete;
        }

        var versionCommand = peek[12];
        if (versionCommand >> 4 != 2 || (versionCommand & 0x0F) > 1)
        {
            return ProxyProtocolParseStatus.Invalid;
        }

        var family = peek[13] >> 4;
        var transport = peek[13] & 0x0F;
        var addressLength = BinaryPrimitives.ReadUInt16BigEndian(peek[14..16]);
        var total = V2FixedLength + addressLength;
        if (total > maxV2Length)
        {
            return ProxyProtocolParseStatus.Invalid;
        }

        if (available < total)
        {
            return ProxyProtocolParseStatus.Incomplete;
        }

        if ((versionCommand & 0x0F) == 0)
        {
            // LOCAL: the proxy's own connection. The address block (whatever it holds) must be ignored.
            consumed = total;
            header = new ProxyProtocolHeader(2, ProxyProtocolCommand.Local, false, default, 0, default, 0);
            return ProxyProtocolParseStatus.Success;
        }

        if (family > 3 || transport > 2 || transport == 2)
        {
            // Unknown family/transport, or DGRAM on a stream listener.
            return ProxyProtocolParseStatus.Invalid;
        }

        var addresses = peek[V2FixedLength..];
        switch (family)
        {
            case 1 when addressLength >= 12 && transport == 1:
                consumed = total;
                header = new ProxyProtocolHeader(
                    2,
                    ProxyProtocolCommand.Proxy,
                    true,
                    IpKey.FromBytes(addresses[..4]),
                    BinaryPrimitives.ReadUInt16BigEndian(addresses[8..10]),
                    IpKey.FromBytes(addresses[4..8]),
                    BinaryPrimitives.ReadUInt16BigEndian(addresses[10..12]));
                return ProxyProtocolParseStatus.Success;

            case 2 when addressLength >= 36 && transport == 1:
                consumed = total;
                header = new ProxyProtocolHeader(
                    2,
                    ProxyProtocolCommand.Proxy,
                    true,
                    IpKey.FromBytes(addresses[..16]),
                    BinaryPrimitives.ReadUInt16BigEndian(addresses[32..34]),
                    IpKey.FromBytes(addresses[16..32]),
                    BinaryPrimitives.ReadUInt16BigEndian(addresses[34..36]));
                return ProxyProtocolParseStatus.Success;

            case 3 when addressLength >= 216:
            case 0:
            case 1 or 2 when transport == 0 && addressLength >= (family == 1 ? 12 : 36):
                // AF_UNIX (no IP), AF_UNSPEC, or an unspecified transport: use the socket's endpoints.
                consumed = total;
                header = new ProxyProtocolHeader(2, ProxyProtocolCommand.Proxy, false, default, 0, default, 0);
                return ProxyProtocolParseStatus.Success;

            default:
                // Address block too short for the declared family.
                return ProxyProtocolParseStatus.Invalid;
        }
    }

    private static ProxyProtocolParseStatus ParseV1(ReadOnlySpan<byte> peek, out ProxyProtocolHeader header, out long consumed)
    {
        header = default;
        consumed = 0;

        var prefixBytes = Math.Min(peek.Length, V1Prefix.Length);
        if (!peek[..prefixBytes].SequenceEqual(V1Prefix[..prefixBytes]))
        {
            return ProxyProtocolParseStatus.Invalid;
        }

        var window = peek[..Math.Min(peek.Length, V1MaxLength)];
        var lf = window.IndexOf((byte)'\n');
        if (lf < 0)
        {
            return window.Length >= V1MaxLength ? ProxyProtocolParseStatus.Invalid : ProxyProtocolParseStatus.Incomplete;
        }

        if (lf < V1Prefix.Length + 1 || window[lf - 1] != '\r')
        {
            return ProxyProtocolParseStatus.Invalid;
        }

        var line = window[V1Prefix.Length..(lf - 1)];
        if (line.StartsWith("UNKNOWN"u8) && (line.Length == 7 || line[7] == ' '))
        {
            // The rest of the line is to be ignored; use the socket's endpoints.
            consumed = lf + 1;
            header = new ProxyProtocolHeader(1, ProxyProtocolCommand.Proxy, false, default, 0, default, 0);
            return ProxyProtocolParseStatus.Success;
        }

        bool isV6;
        if (line.StartsWith("TCP4 "u8))
        {
            isV6 = false;
        }
        else if (line.StartsWith("TCP6 "u8))
        {
            isV6 = true;
        }
        else
        {
            return ProxyProtocolParseStatus.Invalid;
        }

        var rest = line[5..];
        if (!NextToken(ref rest, out var sourceText)
            || !NextToken(ref rest, out var destinationText)
            || !NextToken(ref rest, out var sourcePortText)
            || rest.Length == 0
            || rest.IndexOf((byte)' ') >= 0
            || !TryParseAddress(sourceText, isV6, out var source)
            || !TryParseAddress(destinationText, isV6, out var destination)
            || !TryParsePort(sourcePortText, out var sourcePort)
            || !TryParsePort(rest, out var destinationPort))
        {
            return ProxyProtocolParseStatus.Invalid;
        }

        consumed = lf + 1;
        header = new ProxyProtocolHeader(1, ProxyProtocolCommand.Proxy, true, source, sourcePort, destination, destinationPort);
        return ProxyProtocolParseStatus.Success;
    }

    /// <summary>Splits off the token before the next single space; empty tokens (double spaces) fail.</summary>
    private static bool NextToken(ref ReadOnlySpan<byte> rest, out ReadOnlySpan<byte> token)
    {
        var space = rest.IndexOf((byte)' ');
        if (space <= 0)
        {
            token = default;
            return false;
        }

        token = rest[..space];
        rest = rest[(space + 1)..];
        return true;
    }

    private static bool TryParseAddress(ReadOnlySpan<byte> text, bool isV6, out IpKey address) =>
        isV6 ? IpParser.TryParseIPv6(text, out address) : IpParser.TryParseIPv4(text, out address);

    private static bool TryParsePort(ReadOnlySpan<byte> text, out ushort port)
    {
        port = 0;
        if (text.Length is 0 or > 5 || (text.Length > 1 && text[0] == '0'))
        {
            return false;
        }

        var value = 0;
        foreach (var b in text)
        {
            var d = b - '0';
            if ((uint)d > 9)
            {
                return false;
            }

            value = (value * 10) + d;
        }

        if (value > ushort.MaxValue)
        {
            return false;
        }

        port = (ushort)value;
        return true;
    }
}
