using System.Net;
using System.Text;
using Paduraksa.Proxy.Net;
using Paduraksa.Tests.Proxy.Testing;

namespace Paduraksa.Tests.Proxy.Net;

public sealed class IpParserTests
{
    [Theory]
    [InlineData("0.0.0.0", "0.0.0.0")]
    [InlineData("1.2.3.4", "1.2.3.4")]
    [InlineData("255.255.255.255", "255.255.255.255")]
    [InlineData("10.0.0.1", "10.0.0.1")]
    [InlineData("100.64.0.1", "100.64.0.1")]
    [InlineData("::", "::")]
    [InlineData("::1", "::1")]
    [InlineData("1::", "1::")]
    [InlineData("2001:db8::1", "2001:db8::1")]
    [InlineData("2001:DB8::1", "2001:db8::1")]
    [InlineData("2001:0db8:0000:0000:0000:0000:0000:0001", "2001:db8::1")]
    [InlineData("2001:db8:0:0:1:0:0:1", "2001:db8::1:0:0:1")]
    [InlineData("1:2:3:4:5:6:7:8", "1:2:3:4:5:6:7:8")]
    [InlineData("1::2:3:4:5:6:7", "1:0:2:3:4:5:6:7")]
    [InlineData("1:2:3:4:5:6:7::", "1:2:3:4:5:6:7:0")]
    [InlineData("::2:3:4:5:6:7:8", "0:2:3:4:5:6:7:8")]
    [InlineData("FFFF:ffff:FfFf:ffff:ffff:ffff:ffff:ffff", "ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff")]
    [InlineData("::ffff:1.2.3.4", "1.2.3.4")]
    [InlineData("::FFFF:1.2.3.4", "1.2.3.4")]
    [InlineData("0:0:0:0:0:ffff:1.2.3.4", "1.2.3.4")]
    [InlineData("::ffff:102:304", "1.2.3.4")]
    [InlineData("64:ff9b::192.0.2.33", "64:ff9b::c000:221")]
    [InlineData("1:2:3:4:5:6:1.2.3.4", "1:2:3:4:5:6:102:304")]
    [InlineData("::1.2.3.4", "::102:304")]
    [InlineData("fe80::1", "fe80::1")]
    [InlineData("ffff:ffff:ffff:ffff:ffff:ffff:255.255.255.255", "ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff")]
    public void Accepts(string text, string canonical)
    {
        Assert.True(IpParser.TryParse(text, out var key));
        Assert.Equal(canonical, key.ToString());
        Assert.True(IpParser.TryParse(Encoding.UTF8.GetBytes(text), out var utf8));
        Assert.Equal(key, utf8);
        Assert.Equal(IpKey.FromIPAddress(IPAddress.Parse(text)), key);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("1.2.3")]
    [InlineData("1.2")]
    [InlineData("1")]
    [InlineData("16909060")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1.2.3.256")]
    [InlineData("256.0.0.0")]
    [InlineData("1.2.3.-1")]
    [InlineData("01.2.3.4")]
    [InlineData("1.2.3.04")]
    [InlineData("00.0.0.0")]
    [InlineData("010.0.0.1")]
    [InlineData("0x7f.0.0.1")]
    [InlineData("0x7f000001")]
    [InlineData("1.2.3.4 ")]
    [InlineData(" 1.2.3.4")]
    [InlineData("1.2.3.4\t")]
    [InlineData("1. 2.3.4")]
    [InlineData("1..3.4")]
    [InlineData(".1.2.3")]
    [InlineData("1.2.3.")]
    [InlineData("1.2.3.4:80")]
    [InlineData("1.2.3.4/32")]
    [InlineData("１.2.3.4")]
    [InlineData(":")]
    [InlineData(":::")]
    [InlineData(":1")]
    [InlineData("1:")]
    [InlineData("1:2:3:4:5:6:7")]
    [InlineData("1:2:3:4:5:6:7:8:9")]
    [InlineData("1:2:3:4:5:6:7:8::")]
    [InlineData("::1:2:3:4:5:6:7:8")]
    [InlineData("1:2:3:4::5:6:7:8")]
    [InlineData("1::2::3")]
    [InlineData("1:::2")]
    [InlineData("12345::")]
    [InlineData("g::1")]
    [InlineData("[::1]")]
    [InlineData("[::1]:443")]
    [InlineData("fe80::1%eth0")]
    [InlineData("fe80::1%1")]
    [InlineData(" ::1")]
    [InlineData("::1 ")]
    [InlineData(":: 1")]
    [InlineData("::ffff:01.2.3.4")]
    [InlineData("::ffff:1.2.3")]
    [InlineData("::ffff:1.2.3.4.5")]
    [InlineData("1.2.3.4::")]
    [InlineData("::1.2.3.4:1")]
    [InlineData("1:2:3:4:5:6:7:1.2.3.4")]
    [InlineData("1:2:3:4:5:6:7:8:1.2.3.4")]
    [InlineData("::ffff:1a.2.3.4")]
    [InlineData("ffff:ffff:ffff:ffff:ffff:ffff:255.255.255.255 ")]
    [InlineData("0000:0000:0000:0000:0000:0000:0000:00001")]
    public void Rejects(string text)
    {
        Assert.False(IpParser.TryParse(text, out _));
        Assert.False(IpParser.TryParse(Encoding.UTF8.GetBytes(text), out _));
    }

    [Fact]
    public void Utf8RejectsNonAscii()
    {
        Assert.False(IpParser.TryParse("1.2.3.4é"u8, out _));
        Assert.False(IpParser.TryParse([(byte)'1', 0xC2, 0xB7, (byte)'2'], out _));
    }

    [Fact]
    public void DeprecatedCompatibleFormDoesNotCollapseToIPv4()
    {
        Assert.True(IpParser.TryParse("::1.2.3.4", out var compatible));
        Assert.True(IpParser.TryParse("1.2.3.4", out var v4));
        Assert.True(IpParser.TryParse("::ffff:1.2.3.4", out var mapped));

        Assert.False(compatible.IsIPv4);
        Assert.NotEqual(v4, compatible);
        Assert.Equal(v4, mapped);
        Assert.True(mapped.IsIPv4);
    }

    [Fact]
    public void FamilySpecificParsers()
    {
        Assert.True(IpParser.TryParseIPv4("1.2.3.4", out _));
        Assert.False(IpParser.TryParseIPv4("::ffff:1.2.3.4", out _));
        Assert.True(IpParser.TryParseIPv6("::ffff:1.2.3.4"u8, out var mapped));
        Assert.True(mapped.IsIPv4);
        Assert.False(IpParser.TryParseIPv6("1.2.3.4"u8, out _));
    }

    [Fact]
    public void ParseIsAllocationFree()
    {
        string[] inputs = ["192.168.100.200", "2001:db8:85a3::8a2e:370:7334", "::ffff:10.1.2.3", "not an ip", "::"];
        AllocAssert.Zero(inputs, static s =>
        {
            foreach (var x in s)
            {
                _ = IpParser.TryParse(x, out _);
            }
        }, "IpParser.TryParse(char)");

        byte[][] utf8 = [.. inputs.Select(Encoding.UTF8.GetBytes)];
        AllocAssert.Zero(utf8, static s =>
        {
            foreach (var x in s)
            {
                _ = IpParser.TryParse(x, out _);
            }
        }, "IpParser.TryParse(UTF-8)");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Differential fuzz against IPAddress.TryParse: never accept what the BCL rejects, and agree when both accept.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void FuzzRandomStringsNeverAcceptWhatTheBclRejects()
    {
        const string Alphabet = "0123456789abcdefABCDEF:::...%[]x -/0";
        var random = new Random(20261106);
        Span<char> buffer = stackalloc char[48];
        var accepted = 0;
        for (var i = 0; i < 300_000; i++)
        {
            var length = random.Next(0, 47);
            for (var k = 0; k < length; k++)
            {
                buffer[k] = Alphabet[random.Next(Alphabet.Length)];
            }

            accepted += CheckAgainstBcl(new string(buffer[..length])) ? 1 : 0;
        }

        // Sanity: random strings occasionally form valid addresses ("::", "1::a", ...).
        Assert.True(accepted > 0);
    }

    [Fact]
    public void FuzzNonCanonicalValidFormsAreAcceptedAndEqualToBcl()
    {
        var random = new Random(4291);
        for (var i = 0; i < 100_000; i++)
        {
            var text = RandomValidAddress(random);
            Assert.True(IpParser.TryParse(text, out var ours), $"rejected valid '{text}'");
            Assert.True(IPAddress.TryParse(text, out var bcl), $"BCL rejected generated '{text}'");
            Assert.Equal(IpKey.FromIPAddress(bcl), ours);

            // Formatting round-trips through both parsers.
            var formatted = ours.ToString();
            Assert.True(IpParser.TryParse(formatted, out var again), formatted);
            Assert.Equal(ours, again);
            Assert.Equal(IpKey.FromIPAddress(IPAddress.Parse(formatted)), ours);
        }
    }

    [Fact]
    public void FuzzMutatedValidFormsNeverAcceptWhatTheBclRejects()
    {
        const string Alphabet = "0123456789abcdefABCDEF:.%[] ";
        var random = new Random(5952);
        for (var i = 0; i < 200_000; i++)
        {
            var chars = new List<char>(RandomValidAddress(random));
            var edits = random.Next(1, 4);
            for (var e = 0; e < edits; e++)
            {
                var at = random.Next(chars.Count + 1);
                switch (random.Next(3))
                {
                    case 0:
                        chars.Insert(at, Alphabet[random.Next(Alphabet.Length)]);
                        break;
                    case 1 when at < chars.Count:
                        chars.RemoveAt(at);
                        break;
                    case 2 when at < chars.Count:
                        chars[at] = Alphabet[random.Next(Alphabet.Length)];
                        break;
                    default:
                        break;
                }
            }

            _ = CheckAgainstBcl(new string([.. chars]));
        }
    }

    /// <summary>Asserts the differential property for one input; returns whether we accepted it.</summary>
    private static bool CheckAgainstBcl(string text)
    {
        var ours = IpParser.TryParse(text, out var key);
        var oursUtf8 = IpParser.TryParse(Encoding.UTF8.GetBytes(text), out var keyUtf8);
        Assert.Equal(ours, oursUtf8);
        if (!ours)
        {
            return false;
        }

        Assert.Equal(key, keyUtf8);
        Assert.True(IPAddress.TryParse(text, out var bcl), $"accepted '{text}' which the BCL rejects");
        Assert.Equal(IpKey.FromIPAddress(bcl), key);
        return true;
    }

    /// <summary>A valid literal in a random non-canonical spelling: mixed case, zero-padded groups, uncompressed
    /// zero runs, "::" at any position (covering one or more zero groups), IPv4 tails.</summary>
    internal static string RandomValidAddress(Random random)
    {
        if (random.Next(4) == 0)
        {
            return $"{random.Next(256)}.{random.Next(256)}.{random.Next(256)}.{random.Next(256)}";
        }

        var groups = new int[8];
        for (var g = 0; g < 8; g++)
        {
            groups[g] = random.Next(3) == 0 ? 0 : random.Next(4) == 0 ? random.Next(16) : random.Next(65536);
        }

        var tailKind = random.Next(6); // 0: mapped IPv4 tail, 1: other IPv4 tail, else hex only
        if (tailKind == 0)
        {
            Array.Clear(groups, 0, 5);
            groups[5] = 0xFFFF;
        }

        var tail = tailKind <= 1;
        var hexGroups = tail ? 6 : 8;

        // Pick a zero run [start, start + length) to compress, if any.
        int runStart = -1, runLength = 0;
        if (random.Next(3) != 0)
        {
            runStart = random.Next(hexGroups);
            runLength = random.Next(1, hexGroups - runStart + 1);
            for (var g = runStart; g < runStart + runLength; g++)
            {
                groups[g] = 0;
            }
        }

        var sb = new StringBuilder();
        for (var g = 0; g < hexGroups; g++)
        {
            if (g == runStart)
            {
                sb.Append("::");
                g += runLength - 1;
                continue;
            }

            if (sb.Length > 0 && sb[^1] != ':')
            {
                sb.Append(':');
            }

            var hex = groups[g].ToString("x", System.Globalization.CultureInfo.InvariantCulture);
            foreach (var c in hex.PadLeft(random.Next(hex.Length, 5), '0'))
            {
                sb.Append(random.Next(2) == 0 ? char.ToUpperInvariant(c) : c);
            }
        }

        if (tail)
        {
            if (sb.Length > 0 && sb[^1] != ':')
            {
                sb.Append(':');
            }

            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"{random.Next(256)}.{random.Next(256)}.{random.Next(256)}.{random.Next(256)}");
        }

        return sb.ToString();
    }
}
