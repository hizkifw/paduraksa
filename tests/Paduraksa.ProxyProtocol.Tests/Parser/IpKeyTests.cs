using System.Net;
using System.Net.Sockets;
using System.Text;
using Paduraksa.Tests.Proxy.Testing;

namespace Paduraksa.ProxyProtocol.Tests;

public sealed class IpKeyTests
{
    [Fact]
    public void IPv4AndMappedIPv4ShareOneKey()
    {
        var v4 = IpKey.FromIPAddress(IPAddress.Parse("192.0.2.1"));
        var mapped = IpKey.FromIPAddress(IPAddress.Parse("::ffff:192.0.2.1"));
        var bclMapped = IpKey.FromIPAddress(IPAddress.Parse("192.0.2.1").MapToIPv6());

        Assert.Equal(v4, mapped);
        Assert.Equal(v4, bclMapped);
        Assert.True(v4.IsIPv4);
        Assert.Equal(0xC000_0201u, v4.IPv4Value);
        Assert.Equal(IpKey.FromIPv4(0xC000_0201u), v4);
        Assert.Equal("192.0.2.1", v4.ToString());
    }

    [Theory]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("::1.2.3.4")]
    [InlineData("::fffe:1.2.3.4")]
    [InlineData("1::ffff:1.2.3.4")]
    [InlineData("2001:db8::1")]
    public void NonMappedAddressesAreNotIPv4(string text) => Assert.False(IpKey.Parse(text).IsIPv4);

    [Fact]
    public void FromIPAddressDropsScopeId()
    {
        var withScope = IPAddress.Parse("fe80::1%3");
        Assert.Equal(IpKey.Parse("fe80::1"), IpKey.FromIPAddress(withScope));
    }

    [Fact]
    public void ToIPAddressRoundTrips()
    {
        Assert.Equal(IPAddress.Parse("10.1.2.3"), IpKey.Parse("10.1.2.3").ToIPAddress());
        Assert.Equal(IPAddress.Parse("2001:db8::7"), IpKey.Parse("2001:db8::7").ToIPAddress());
    }

    [Fact]
    public void TryFromEndPoint()
    {
        Assert.True(IpKey.TryFromEndPoint(new IPEndPoint(IPAddress.Parse("::ffff:10.0.0.9"), 4433), out var key, out var port));
        Assert.Equal(IpKey.Parse("10.0.0.9"), key);
        Assert.Equal(4433, port);

        Assert.False(IpKey.TryFromEndPoint(null, out key, out port));
        Assert.Equal(IpKey.Unspecified, key);
        Assert.Equal(0, port);
        Assert.False(IpKey.TryFromEndPoint(new UnixDomainSocketEndPoint("/tmp/x.sock"), out _, out _));
    }

    [Theory]
    [InlineData("2001:db8:1:2:3:4:5:6", 64, "2001:db8:1:2::")]
    [InlineData("2001:db8:1:2:3:4:5:6", 128, "2001:db8:1:2:3:4:5:6")]
    [InlineData("2001:db8:1:2:3:4:5:6", 0, "::")]
    [InlineData("2001:db8:1:2:3:4:5:6", 33, "2001:db8::")]
    [InlineData("192.0.2.77", 24, "192.0.2.0")]
    [InlineData("192.0.2.77", 32, "192.0.2.77")]
    [InlineData("192.0.2.77", 0, "0.0.0.0")]
    [InlineData("192.0.2.77", 1, "128.0.0.0")]
    public void MaskTo(string address, int prefix, string expected)
    {
        var masked = IpKey.Parse(address).MaskTo(prefix);
        Assert.Equal(IpKey.Parse(expected), masked);
        Assert.Equal(IpKey.Parse(address).IsIPv4, masked.IsIPv4);
    }

    [Fact]
    public void MaskToPerFamily()
    {
        Assert.Equal(IpKey.Parse("192.0.2.77"), IpKey.Parse("192.0.2.77").MaskTo(32, 64));
        Assert.Equal(IpKey.Parse("2001:db8:1:2::"), IpKey.Parse("2001:db8:1:2:3:4:5:6").MaskTo(32, 64));
        Assert.Throws<ArgumentOutOfRangeException>(() => IpKey.Parse("1.2.3.4").MaskTo(33));
        Assert.Throws<ArgumentOutOfRangeException>(() => IpKey.Parse("::1").MaskTo(129));
        Assert.Throws<ArgumentOutOfRangeException>(() => IpKey.Parse("::1").MaskTo(-1));
    }

    [Fact]
    public void EqualityAndOrdering()
    {
        var a = IpKey.Parse("1.2.3.4");
        var b = IpKey.Parse("1.2.3.5");
        var c = IpKey.Parse("2001:db8::");
        Assert.True(a < b && b < c && a <= b && c > a && c >= b);
        Assert.True(a != b);
        Assert.Equal(a.GetHashCode(), IpKey.Parse("::ffff:1.2.3.4").GetHashCode());
        Assert.Equal(-1, a.CompareTo(b));
        Assert.True(a.Equals((object)IpKey.Parse("1.2.3.4")));
        Assert.False(a.Equals("1.2.3.4"));
    }

    [Theory]
    [InlineData("2001:db8:0:0:1:0:0:1", "2001:db8::1:0:0:1")]
    [InlineData("2001:0:0:1:0:0:0:1", "2001:0:0:1::1")]
    [InlineData("2001:db8:0:1:1:1:1:1", "2001:db8:0:1:1:1:1:1")]
    [InlineData("0:0:0:0:0:0:0:0", "::")]
    [InlineData("1:0:0:0:0:0:0:0", "1::")]
    [InlineData("2001:DB8::AbCd", "2001:db8::abcd")]
    [InlineData("::ffff:10.0.0.1", "10.0.0.1")]
    [InlineData("::10.0.0.1", "::a00:1")]
    [InlineData("1:2:3:4:5:6:7:8", "1:2:3:4:5:6:7:8")]
    public void FormatsRfc5952(string input, string expected)
    {
        var key = IpKey.Parse(input);
        Assert.Equal(expected, key.ToString());
        Assert.Equal(expected, $"{key}");

        Span<byte> utf8 = stackalloc byte[IpKey.MaxFormattedLength];
        Assert.True(key.TryFormat(utf8, out var written, default, null));
        Assert.Equal(expected, Encoding.ASCII.GetString(utf8[..written]));
    }

    [Fact]
    public void TryFormatReportsShortBuffers()
    {
        var key = IpKey.Parse("255.255.255.255");
        Assert.False(key.TryFormat(new char[14], out var written));
        Assert.Equal(0, written);
        Assert.True(key.TryFormat(new char[15], out written));
        Assert.Equal(15, written);
        Assert.False(key.TryFormat(new byte[3], out _, default, null));
    }

    [Fact]
    public void ParseThrowsOnInvalid() => Assert.Throws<FormatException>(() => IpKey.Parse("1.2.3"));

    [Fact]
    public void FromBytes()
    {
        Assert.Equal(IpKey.Parse("1.2.3.4"), IpKey.FromBytes([1, 2, 3, 4]));
        Span<byte> sixteen = stackalloc byte[16];
        IpKey.Parse("2001:db8::9").WriteBytes(sixteen);
        Assert.Equal(IpKey.Parse("2001:db8::9"), IpKey.FromBytes(sixteen));
        Assert.Throws<ArgumentException>(() => IpKey.FromBytes([1, 2, 3]));
    }

    [Fact]
    public void FromIPAddressIsAllocationFree()
    {
        AllocAssert.Zero(IPAddress.Parse("203.0.113.9"), static a => _ = IpKey.FromIPAddress(a), "FromIPAddress(v4)");
        AllocAssert.Zero(IPAddress.Parse("2001:db8::1234"), static a => _ = IpKey.FromIPAddress(a), "FromIPAddress(v6)");
        AllocAssert.Zero(IPAddress.Parse("::ffff:203.0.113.9"), static a => _ = IpKey.FromIPAddress(a), "FromIPAddress(mapped)");
        AllocAssert.Zero(new IPEndPoint(IPAddress.IPv6Loopback, 80), static e => _ = IpKey.TryFromEndPoint(e, out _, out _), "TryFromEndPoint");
    }

    [Fact]
    public void FormattingToSpanIsAllocationFree()
    {
        var keys = new[] { IpKey.Parse("203.0.113.9"), IpKey.Parse("2001:db8::1234") };
        AllocAssert.Zero(keys, static k =>
        {
            Span<char> buffer = stackalloc char[IpKey.MaxFormattedLength];
            foreach (var key in k)
            {
                _ = key.TryFormat(buffer, out _);
            }
        }, "IpKey.TryFormat");
    }
}
