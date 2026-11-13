using System.Net;
using System.Text;

using Paduraksa.Proxy.Net;
using Paduraksa.ProxyProtocol;

namespace Paduraksa.Fuzz;

/// <summary>
/// A CIDR block as an operator types it into a trusted-proxy list, an access list or a Cloudflare range list: <see cref="IpNetwork.TryParse(ReadOnlySpan{char}, out IpNetwork)"/> on the
/// bytes as Latin-1 text. Properties: no exception; the same verdict twice; 0 B, error path included; the verdict is what an independent reading of the three rules gives (the address
/// is one <see cref="IpParser"/> reads, the prefix is decimal without a leading zero or a sign and within the family's width, no host bit is set below the prefix: judged on the bytes
/// <see cref="IPAddress"/> reads from the same text); a block that parses is its network address and spans exactly 2^(128-prefix) addresses; and its canonical text parses back to the
/// same block and is its own canonical text.
/// </summary>
internal static class IpNetworkTarget
{
    public static FuzzTarget Target { get; } = new(
        "ip-network",
        "a CIDR block (address/prefix): the strict parser's verdict, its canonical text and the block's extent",
        Run,
        Seeds);

    private static void Run(ReadOnlySpan<byte> input)
    {
        var text = Encoding.Latin1.GetString(input);

        var verdict = Checks.Deterministic(() => Describe(text), "the CIDR verdict");
        Checks.AllocatesNothing(() => _ = IpNetwork.TryParse(text, out _, out _), "IpNetwork.TryParse");
        Checks.AllocatesNothing(() => _ = IpNetwork.TryParse(text, out _), "IpNetwork.TryParse without a reason");

        var parsed = IpNetwork.TryParse(text, out var network, out var error);
        Checks.That(parsed == (error is null), "a verdict and its reason disagree");
        Checks.That(IpNetwork.TryParse(text, out var plain) == parsed && plain == network, "the two overloads disagree");
        if (!parsed)
        {
            Checks.That(network == default && !string.IsNullOrEmpty(error), $"'{text}' was refused without a reason or with a block left behind");
            Checks.That(Expected(text) is null, $"'{Shown(text)}' is refused ({error}) and a reading of the rules by hand accepts it");
            return;
        }

        var expected = Expected(text);
        Checks.That(
            expected is { } want && want.First == network.First.Value && want.Prefix128 == network.Prefix128,
            $"'{Shown(text)}' is {network} to the parser and {(expected is { } e ? $"{e.First}/{e.Prefix128}" : "refused")} to the rules read by hand");
        Checks.That(verdict == Describe(text), "the verdict changed");

        CheckExtent(network);
        CheckCanonicalText(network);
    }

    /// <summary>The block the three rules give for <paramref name="text"/>, read independently of <see cref="IpNetwork.TryParse(ReadOnlySpan{char}, out IpNetwork)"/>; null when one fails.</summary>
    private static (UInt128 First, int Prefix128)? Expected(string text)
    {
        var slash = text.IndexOf('/', StringComparison.Ordinal);
        var addressText = slash < 0 ? text : text[..slash];
        if (!IpParser.TryParse(addressText, out var key))
        {
            return null;
        }

        var ipv4Text = !addressText.Contains(':', StringComparison.Ordinal);
        var width = ipv4Text ? 32 : 128;
        var prefix = width;
        if (slash >= 0)
        {
            var digits = text[(slash + 1)..];
            var decimalWithoutLeadingZero = digits.Length is >= 1 and <= 3 && digits.All(static c => c is >= '0' and <= '9') && (digits.Length == 1 || digits[0] != '0');
            if (!decimalWithoutLeadingZero || (prefix = int.Parse(digits, System.Globalization.CultureInfo.InvariantCulture)) > width)
            {
                return null;
            }
        }

        // The address as the BCL reads the same text: 16 bytes, an IPv4 address mapped.
        Checks.That(IPAddress.TryParse(addressText, out var system), $"IpParser reads '{Shown(addressText)}' and IPAddress doesn't");
        var bytes = system!.MapToIPv6().GetAddressBytes();
        var prefix128 = ipv4Text ? 96 + prefix : prefix;
        for (var bit = prefix128; bit < 128; bit++)
        {
            if ((bytes[bit / 8] & (0x80 >> (bit % 8))) != 0)
            {
                return null;
            }
        }

        Checks.That(IpKey.FromBytes(bytes) == key, $"'{Shown(addressText)}' is {key} to IpParser and {IpKey.FromBytes(bytes)} to IPAddress");
        return (key.Value, prefix128);
    }

    private static void CheckExtent(IpNetwork network)
    {
        Checks.That(network.Prefix128 is >= 0 and <= 128, "a prefix outside 0-128");
        Checks.That(network.Contains(network.First) && network.Contains(network.Last), $"{network} doesn't contain its own ends");
        Checks.That(network.First <= network.Last, $"{network} ends before it starts");
        Checks.That(network.Prefix128 >= 96 || !network.IsIPv4, $"{network} is IPv4 with a prefix shorter than the mapped range");

        // 2^(128 - prefix) addresses; a /0 holds them all, a /128 only itself.
        var size = network.Last.Value - network.First.Value;
        Checks.That(network.Prefix128 == 128 ? size == UInt128.Zero : size == (UInt128.MaxValue >> network.Prefix128), $"{network} has the wrong number of addresses");
        if (network.First.Value != UInt128.Zero)
        {
            Checks.That(!network.Contains(new IpKey(network.First.Value - UInt128.One)) || network.Prefix128 == 0, $"{network} contains the address before its own");
        }

        if (network.Last.Value != UInt128.MaxValue)
        {
            Checks.That(!network.Contains(new IpKey(network.Last.Value + UInt128.One)) || network.Prefix128 == 0, $"{network} contains the address after its own");
        }
    }

    private static void CheckCanonicalText(IpNetwork network)
    {
        var canonical = network.ToString();
        Checks.That(IpNetwork.TryParse(canonical, out var again, out var error), $"{canonical} (from {network}) doesn't parse back: {error}");
        Checks.That(again == network, $"{canonical} parses back to {again}, not {network}");
        Checks.That(again.ToString() == canonical, $"{canonical} is not its own canonical text");
        if (network.IsIPv4 || !network.First.IsIPv4)
        {
            // Create takes a prefix of the block's own family; a mapped-range block shorter than /96 has no IPv4 reading to build from.
            Checks.That(network == IpNetwork.Create(network.First, network.PrefixLength), $"{network} is not what Create makes of its own parts");
        }
    }

    private static string Describe(string text) =>
        IpNetwork.TryParse(text, out var network, out var error) ? $"{network.First.Value}/{network.Prefix128}" : "refused: " + error;

    private static string Shown(string text) => text.Length <= 64 ? text : string.Concat(text.AsSpan(0, 64), "...");

    private static IEnumerable<(string Name, byte[] Bytes)> Seeds()
    {
        static (string, byte[]) Case(string name, string text) => (name, Encoding.Latin1.GetBytes(text));

        yield return Case("ipv4-8", "10.0.0.0/8");
        yield return Case("ipv4-32", "203.0.113.9/32");
        yield return Case("ipv4-bare", "203.0.113.9");
        yield return Case("ipv4-all", "0.0.0.0/0");
        yield return Case("ipv4-host-bits", "10.0.0.1/8");
        yield return Case("ipv4-33", "10.0.0.0/33");
        yield return Case("ipv4-leading-zero-prefix", "10.0.0.0/08");
        yield return Case("ipv4-leading-zero-octet", "010.0.0.0/8");
        yield return Case("ipv6-32", "2001:db8::/32");
        yield return Case("ipv6-bare", "2001:db8::1");
        yield return Case("ipv6-all", "::/0");
        yield return Case("ipv6-128", "::1/128");
        yield return Case("ipv6-129", "::1/129");
        yield return Case("ipv6-host-bits", "2001:db8::1/32");
        yield return Case("mapped-104", "::ffff:10.0.0.0/104");
        yield return Case("mapped-80", "::ffff:0:0/80");
        yield return Case("cloudflare", "2606:4700::/32");
        yield return Case("signed-prefix", "10.0.0.0/+8");
        yield return Case("empty-prefix", "10.0.0.0/");
        yield return Case("two-slashes", "10.0.0.0/8/8");
        yield return Case("zone", "fe80::/10%eth0");
        yield return Case("spaces", " 10.0.0.0/8 ");
        yield return Case("empty", string.Empty);
    }
}
