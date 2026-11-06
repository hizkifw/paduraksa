using System.Buffers;
using Paduraksa.Proxy.Net;
using Paduraksa.Tests.Proxy.Testing;
using static Paduraksa.Tests.Proxy.Net.ProxyProtocolTestData;

namespace Paduraksa.Tests.Proxy.Net;

public sealed class ProxyProtocolParserTests
{
    [Theory]
    [InlineData("PROXY TCP4 203.0.113.7 192.0.2.1 50123 443\r\n", "203.0.113.7", 50123, "192.0.2.1", 443)]
    [InlineData("PROXY TCP4 0.0.0.0 255.255.255.255 0 65535\r\n", "0.0.0.0", 0, "255.255.255.255", 65535)]
    [InlineData("PROXY TCP6 2001:db8::1 2001:db8::2 1 2\r\n", "2001:db8::1", 1, "2001:db8::2", 2)]
    [InlineData("PROXY TCP6 ::ffff:203.0.113.7 ::1 1 2\r\n", "203.0.113.7", 1, "::1", 2)]
    [InlineData("PROXY TCP6 ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff 65535 65535\r\n", "ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff", 65535, "ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff", 65535)]
    public void V1_Valid(string line, string source, int sourcePort, string destination, int destinationPort)
    {
        var bytes = V1(line + "GET / HTTP/1.1\r\n");
        Assert.Equal(ProxyProtocolParseStatus.Success, ProxyProtocolParser.TryParse(bytes, out var header, out int consumed));
        Assert.Equal(line.Length, consumed);
        Assert.Equal(1, header.Version);
        Assert.Equal(ProxyProtocolCommand.Proxy, header.Command);
        Assert.True(header.HasAddresses);
        Assert.Equal(IpKey.Parse(source), header.SourceAddress);
        Assert.Equal(sourcePort, header.SourcePort);
        Assert.Equal(IpKey.Parse(destination), header.DestinationAddress);
        Assert.Equal(destinationPort, header.DestinationPort);
    }

    [Theory]
    [InlineData("PROXY UNKNOWN\r\n")]
    [InlineData("PROXY UNKNOWN ffff:f...f:ffff ffff:f...f:ffff 65535 65535\r\n")]
    public void V1_Unknown_HasNoAddresses(string line)
    {
        Assert.Equal(ProxyProtocolParseStatus.Success, ProxyProtocolParser.TryParse(V1(line), out var header, out int consumed));
        Assert.Equal(line.Length, consumed);
        Assert.False(header.HasAddresses);
    }

    [Theory]
    [InlineData("PROXY TCP4 203.0.113.7 192.0.2.1 50123 443\n")]
    [InlineData("PROXY TCP4 203.0.113.7 192.0.2.1 50123 443\r\r\n")]
    [InlineData("PROXY TCP4 203.0.113.7  192.0.2.1 50123 443\r\n")]
    [InlineData("PROXY TCP4 203.0.113.7 192.0.2.1 50123 443 \r\n")]
    [InlineData("PROXY TCP4 203.0.113.7 192.0.2.1 50123\r\n")]
    [InlineData("PROXY TCP4 203.0.113.7 192.0.2.1 50123 443 1\r\n")]
    [InlineData("PROXY TCP4 203.0.113.7 192.0.2.1 050123 443\r\n")]
    [InlineData("PROXY TCP4 203.0.113.7 192.0.2.1 65536 443\r\n")]
    [InlineData("PROXY TCP4 203.0.113.7 192.0.2.1 -1 443\r\n")]
    [InlineData("PROXY TCP4 203.0.113.07 192.0.2.1 1 443\r\n")]
    [InlineData("PROXY TCP4 2001:db8::1 192.0.2.1 1 443\r\n")]
    [InlineData("PROXY TCP4 ::ffff:1.2.3.4 192.0.2.1 1 443\r\n")]
    [InlineData("PROXY TCP6 203.0.113.7 2001:db8::1 1 443\r\n")]
    [InlineData("PROXY TCP6 fe80::1%eth0 2001:db8::1 1 443\r\n")]
    [InlineData("PROXY TCP6 [2001:db8::1] 2001:db8::1 1 443\r\n")]
    [InlineData("PROXY UDP4 203.0.113.7 192.0.2.1 1 443\r\n")]
    [InlineData("PROXY tcp4 203.0.113.7 192.0.2.1 1 443\r\n")]
    [InlineData("PROXY UNKNOWNX\r\n")]
    [InlineData("PROXY \r\n")]
    [InlineData("PROXY\r\n")]
    [InlineData("proxy TCP4 203.0.113.7 192.0.2.1 50123 443\r\n")]
    [InlineData("GET / HTTP/1.1\r\n")]
    [InlineData("\x16\x03\x01")]
    public void V1_Invalid(string line) =>
        Assert.Equal(ProxyProtocolParseStatus.Invalid, ProxyProtocolParser.TryParse(V1(line), out _, out int _));

    [Fact]
    public void V1_LongerThan107BytesIsInvalid()
    {
        var line = "PROXY UNKNOWN " + new string('x', 200) + "\r\n";
        Assert.Equal(ProxyProtocolParseStatus.Invalid, ProxyProtocolParser.TryParse(V1(line), out _, out int _));

        var exactly107 = "PROXY UNKNOWN " + new string('x', 107 - 16) + "\r\n";
        Assert.Equal(107, exactly107.Length);
        Assert.Equal(ProxyProtocolParseStatus.Success, ProxyProtocolParser.TryParse(V1(exactly107), out _, out int _));
    }

    [Fact]
    public void V2_Inet()
    {
        var bytes = Concat(V2(), V1("rest"));
        Assert.Equal(ProxyProtocolParseStatus.Success, ProxyProtocolParser.TryParse(bytes, out var header, out int consumed));
        Assert.Equal(16 + 12, consumed);
        Assert.Equal(2, header.Version);
        Assert.Equal(ProxyProtocolCommand.Proxy, header.Command);
        Assert.True(header.HasAddresses);
        Assert.Equal(IpKey.Parse("203.0.113.7"), header.SourceAddress);
        Assert.Equal(50123, header.SourcePort);
        Assert.Equal(IpKey.Parse("192.0.2.1"), header.DestinationAddress);
        Assert.Equal(443, header.DestinationPort);
    }

    [Fact]
    public void V2_Inet6WithTlvs()
    {
        var bytes = V2(family: 2, source: "2001:db8::77", destination: "2001:db8::1", tlvBytes: 300);
        Assert.Equal(ProxyProtocolParseStatus.Success, ProxyProtocolParser.TryParse(bytes, out var header, out int consumed));
        Assert.Equal(bytes.Length, consumed);
        Assert.Equal(IpKey.Parse("2001:db8::77"), header.SourceAddress);
        Assert.Equal(IpKey.Parse("2001:db8::1"), header.DestinationAddress);
    }

    [Theory]
    [InlineData(0, 1, 1)] // LOCAL, address block ignored
    [InlineData(0, 0, 0)] // LOCAL, UNSPEC
    [InlineData(0, 7, 7)] // LOCAL ignores even a bogus family byte
    [InlineData(1, 0, 0)] // PROXY, AF_UNSPEC
    [InlineData(1, 3, 1)] // PROXY, AF_UNIX
    [InlineData(1, 1, 0)] // PROXY, INET, transport UNSPEC
    public void V2_WithoutAddresses(int command, int family, int transport)
    {
        var bytes = V2(command: command, family: family, transport: transport);
        Assert.Equal(ProxyProtocolParseStatus.Success, ProxyProtocolParser.TryParse(bytes, out var header, out int consumed));
        Assert.Equal(bytes.Length, consumed);
        Assert.False(header.HasAddresses);
        Assert.Equal(command == 0 ? ProxyProtocolCommand.Local : ProxyProtocolCommand.Proxy, header.Command);
    }

    [Fact]
    public void V2_Invalid()
    {
        Assert.Equal(ProxyProtocolParseStatus.Invalid, Parse(V2(version: 1)));
        Assert.Equal(ProxyProtocolParseStatus.Invalid, Parse(V2(version: 3)));
        Assert.Equal(ProxyProtocolParseStatus.Invalid, Parse(V2(command: 2)));
        Assert.Equal(ProxyProtocolParseStatus.Invalid, Parse(V2(transport: 2))); // DGRAM
        Assert.Equal(ProxyProtocolParseStatus.Invalid, Parse(V2(transport: 3)));
        Assert.Equal(ProxyProtocolParseStatus.Invalid, Parse(V2(family: 4, transport: 1)));
        Assert.Equal(ProxyProtocolParseStatus.Invalid, Parse(V2(family: 2, lengthOverride: 12))); // too short for INET6
        Assert.Equal(ProxyProtocolParseStatus.Invalid, Parse(V2(family: 1, lengthOverride: 11)));
        Assert.Equal(ProxyProtocolParseStatus.Invalid, Parse(V2(family: 3, lengthOverride: 100)));
        Assert.Equal(ProxyProtocolParseStatus.Invalid, Parse(V2(tlvBytes: 5000))); // over the 4 KiB default cap

        var badSignature = V2();
        badSignature[7] ^= 0xFF;
        Assert.Equal(ProxyProtocolParseStatus.Invalid, Parse(badSignature));

        static ProxyProtocolParseStatus Parse(byte[] bytes) => ProxyProtocolParser.TryParse(bytes, out _, out int _);
    }

    [Fact]
    public void V2_MaxLengthIsConfigurable()
    {
        var bytes = V2(tlvBytes: 5000);
        Assert.Equal(ProxyProtocolParseStatus.Success, ProxyProtocolParser.TryParse(bytes, out _, out int _, maxV2Length: 8192));
    }

    [Fact]
    public void EveryPrefixOfAValidHeaderIsIncomplete()
    {
        byte[][] headers =
        [
            V1("PROXY TCP6 2001:db8::1 2001:db8::2 50123 443\r\n"),
            V1("PROXY UNKNOWN\r\n"),
            V2(),
            V2(family: 2, source: "2001:db8::1", destination: "2001:db8::2", tlvBytes: 40),
            V2(command: 0, family: 0, transport: 0),
        ];

        foreach (var header in headers)
        {
            for (var length = 0; length < header.Length; length++)
            {
                Assert.Equal(ProxyProtocolParseStatus.Incomplete, ProxyProtocolParser.TryParse(header.AsSpan(0, length), out _, out int _));
            }

            Assert.Equal(ProxyProtocolParseStatus.Success, ProxyProtocolParser.TryParse(header, out _, out int consumed));
            Assert.Equal(header.Length, consumed);
        }
    }

    [Fact]
    public void GarbageIsRejectedFromTheFirstBytes()
    {
        Assert.Equal(ProxyProtocolParseStatus.Invalid, ProxyProtocolParser.TryParse("G"u8, out _, out int _));
        Assert.Equal(ProxyProtocolParseStatus.Invalid, ProxyProtocolParser.TryParse("PRX"u8, out _, out int _));
        Assert.Equal(ProxyProtocolParseStatus.Invalid, ProxyProtocolParser.TryParse([0x0D, 0x0A, 0x0D, 0x00], out _, out int _));
        Assert.Equal(ProxyProtocolParseStatus.Invalid, ProxyProtocolParser.TryParse([0x16, 0x03, 0x01], out _, out int _)); // TLS ClientHello
    }

    [Fact]
    public void ParsesAcrossSegments()
    {
        foreach (var header in new[] { V2(family: 2, source: "2001:db8::1", destination: "2001:db8::2", tlvBytes: 20), V1("PROXY TCP4 1.2.3.4 5.6.7.8 1 2\r\n") })
        {
            var bytes = Concat(header, V1("payload"));
            for (var split = 1; split < bytes.Length; split++)
            {
                var sequence = TwoSegments(bytes, split);
                Assert.Equal(ProxyProtocolParseStatus.Success, ProxyProtocolParser.TryParse(sequence, out var parsed, out long consumed));
                Assert.Equal(header.Length, consumed);
                Assert.True(parsed.HasAddresses);
            }
        }
    }

    [Fact]
    public void ParseIsAllocationFree()
    {
        var state = (V1: V1("PROXY TCP4 203.0.113.7 192.0.2.1 50123 443\r\n"), V2: V2(family: 2, source: "2001:db8::1", destination: "2001:db8::2", tlvBytes: 64));
        AllocAssert.Zero(state, static s =>
        {
            _ = ProxyProtocolParser.TryParse(s.V1, out _, out int _);
            _ = ProxyProtocolParser.TryParse(s.V2, out _, out int _);
        }, "ProxyProtocolParser.TryParse");
    }

    /// <summary>The <see cref="ReadOnlySequence{T}"/> overload is what <see cref="ProxyProtocolReader"/> calls; a header
    /// split across pipe segments goes through its stack-copy path.</summary>
    [Fact]
    public void ParseOverSplitSequenceIsAllocationFree()
    {
        var v1 = V1("PROXY TCP4 203.0.113.7 192.0.2.1 50123 443\r\nGET /");
        var v2 = Concat(V2(family: 2, source: "2001:db8::1", destination: "2001:db8::2", tlvBytes: 64), V1("payload"));
        var state = (V1: TwoSegments(v1, 9), V2: TwoSegments(v2, 20), V2Single: new ReadOnlySequence<byte>(v2));
        AllocAssert.Zero(state, static s =>
        {
            _ = ProxyProtocolParser.TryParse(s.V1, out _, out long _);
            _ = ProxyProtocolParser.TryParse(s.V2, out _, out long _);
            _ = ProxyProtocolParser.TryParse(s.V2Single, out _, out long _);
        }, "ProxyProtocolParser.TryParse(ReadOnlySequence, split)");
    }

    internal static ReadOnlySequence<byte> TwoSegments(byte[] bytes, int split)
    {
        var first = new Segment(bytes.AsMemory(0, split), 0);
        var second = first.Append(bytes.AsMemory(split));
        return new ReadOnlySequence<byte>(first, 0, second, second.Memory.Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory, RunningIndex + Memory.Length);
            Next = next;
            return next;
        }
    }
}
