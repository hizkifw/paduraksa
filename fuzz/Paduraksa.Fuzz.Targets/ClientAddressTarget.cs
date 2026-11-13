using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Paduraksa.Core.Geo;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Net;
using Paduraksa.ProxyProtocol;

namespace Paduraksa.Fuzz;

/// <summary>
/// The client-address headers a trusted proxy supplies: <c>CF-Connecting-IP</c>, <c>X-Forwarded-For</c>, <c>True-Client-IP</c> and <c>CF-IPCountry</c>, through the
/// resolver in each trusted-proxy mode. The input is a byte that picks the policy and the peer, then the header lines (a line feed ends a line; they are dealt to
/// the four headers in turn, so a header can repeat). Properties: no exception; the same verdict twice; an untrusted peer is never moved by a header and gets no country;
/// an address taken from a header is what <see cref="IPAddress"/> reads from the same text; the parser's text and UTF-8 forms agree.
/// </summary>
internal static class ClientAddressTarget
{
    private static readonly string[] HeaderNames = ["CF-Connecting-IP", "X-Forwarded-For", "CF-IPCountry", "True-Client-IP"];

    private static readonly TrustedProxyPolicy[] Policies =
    [
        TrustedProxyPolicy.Off,
        TrustedProxyPolicy.Generic(CidrSet.Parse(["10.0.0.0/8", "fd00::/8"])),
        TrustedProxyPolicy.Generic(CidrSet.Parse(["10.0.0.0/8"]), "X-Real-IP"),
        TrustedProxyPolicy.CloudflareProxy(),
        TrustedProxyPolicy.CloudflareProxy(null, useTrueClientIp: true, forwardedForFallback: true),
        TrustedProxyPolicy.CloudflareTunnel(CidrSet.Parse(["172.18.0.0/16", "::1"]), useTrueClientIp: true, forwardedForFallback: true),
    ];

    private static readonly IpKey[] Peers =
        [IpKey.Parse("10.0.0.2"), IpKey.Parse("198.51.100.23"), IpKey.Parse("172.70.1.2"), IpKey.Parse("172.18.0.3"), IpKey.Parse("::1"), IpKey.Parse("2a06:98c0:3600::103")];

    public static FuzzTarget Target { get; } = new(
        "client-address",
        "CF-Connecting-IP, X-Forwarded-For, True-Client-IP and CF-IPCountry through the client address resolver",
        Run,
        Seeds);

    private static void Run(ReadOnlySpan<byte> input)
    {
        if (input.IsEmpty)
        {
            return;
        }

        var policy = Policies[input[0] % Policies.Length];
        var peer = Peers[(input[0] / Policies.Length) % Peers.Length];
        var lines = Lines(input[1..]);
        HeaderDictionary headers = [];
        for (var i = 0; i < lines.Count; i++)
        {
            var name = policy.Mode == TrustedProxyMode.Generic && i % HeaderNames.Length == 1 && policy.ForwardingHeader is { } own ? own : HeaderNames[i % HeaderNames.Length];
            headers[name] = StringValues.Concat(headers[name], lines[i]);
        }

        var resolution = Checks.Deterministic(() => ClientIpResolver.Resolve(peer, ClientIpSource.Socket, headers, policy), "the client address verdict");
        Checks.AllocatesNothing(() => _ = ClientIpResolver.Resolve(peer, ClientIpSource.Socket, headers, policy), "ClientIpResolver.Resolve");

        if (!policy.IsTrustedPeer(peer))
        {
            Checks.That(resolution is { Address: var address, Outcome: ClientIpOutcome.PeerNotTrusted } && address == peer, $"an untrusted peer {peer} was moved to {resolution.Address} by a header");
            Checks.That(!resolution.CloudflareHeadersTrusted && resolution.Country == CountryCode.Unknown, "an untrusted peer's Cloudflare headers were believed");
        }
        else if (resolution.Outcome == ClientIpOutcome.FromHeader && resolution.Source is ClientIpSource.CfConnectingIp or ClientIpSource.TrueClientIp)
        {
            var text = Encoding.Latin1.GetString(Encoding.Latin1.GetBytes(headers[resolution.Source == ClientIpSource.CfConnectingIp ? "CF-Connecting-IP" : "True-Client-IP"].ToString())).Trim(' ', '\t');
            Checks.That(IPAddress.TryParse(text, out var parsed), $"'{text}' was taken as an address, and IPAddress can't read it");
            Checks.That(IpKey.FromIPAddress(parsed!) == resolution.Address, $"'{text}' is {IpKey.FromIPAddress(parsed!)} to IPAddress and {resolution.Address} to the resolver");
        }

        // The parser's two forms (text and UTF-8) on every line, and what it accepts is an address IPAddress agrees with.
        foreach (var line in lines)
        {
            foreach (var part in line.Split(','))
            {
                var text = part.Trim(' ', '\t');
                var utf8 = Encoding.Latin1.GetBytes(text);
                var asText = IpParser.TryParse(text, out var fromText);
                var asBytes = IpParser.TryParse(utf8, out var fromBytes);
                Checks.That(asText == asBytes && (!asText || fromText == fromBytes), $"'{text}': the parser's text and UTF-8 forms disagree");
                if (asText)
                {
                    Checks.That(IPAddress.TryParse(text, out var system), $"IpParser reads '{text}' and IPAddress doesn't");
                    Checks.That(IpKey.FromIPAddress(system!) == fromText, $"'{text}' is {fromText} to IpParser and {IpKey.FromIPAddress(system!)} to IPAddress");
                }
            }
        }
    }

    private static List<string> Lines(ReadOnlySpan<byte> bytes)
    {
        List<string> lines = [];
        while (lines.Count < 8)
        {
            var feed = bytes.IndexOf((byte)'\n');
            lines.Add(Encoding.Latin1.GetString(feed < 0 ? bytes : bytes[..feed]));
            if (feed < 0)
            {
                break;
            }

            bytes = bytes[(feed + 1)..];
        }

        return lines;
    }

    private static IEnumerable<(string Name, byte[] Bytes)> Seeds()
    {
        // policy 3 = Cloudflare proxy; the peer 2 (index 2 * 6 + ... ) is Cloudflare's edge.
        static byte[] Case(int policy, int peer, params string[] lines) => [(byte)(policy + (Policies.Length * peer)), .. Encoding.Latin1.GetBytes(string.Join('\n', lines))];

        yield return ("cf-ipv4", Case(3, 2, "198.51.100.7", string.Empty, "NL"));
        yield return ("cf-ipv6", Case(3, 2, "2001:db8::7", string.Empty, "de"));
        yield return ("cf-invalid", Case(3, 2, "not-an-ip", string.Empty, "XX"));
        yield return ("cf-with-port", Case(3, 2, "198.51.100.7:443", string.Empty, "T1"));
        yield return ("cf-mapped", Case(3, 2, "::ffff:198.51.100.7"));
        yield return ("cf-true-client", Case(4, 2, string.Empty, string.Empty, string.Empty, "203.0.113.9"));
        yield return ("cf-xff-fallback", Case(4, 2, string.Empty, "203.0.113.9, 172.70.1.2"));
        yield return ("generic-chain", Case(1, 0, string.Empty, "203.0.113.9, 10.0.0.7, 10.0.0.2"));
        yield return ("generic-ipv6-port", Case(1, 0, string.Empty, "[2001:db8::1]:443, 10.0.0.7"));
        yield return ("generic-real-ip", Case(2, 0, string.Empty, "203.0.113.9"));
        yield return ("untrusted-peer", Case(3, 1, "198.51.100.7", string.Empty, "NL"));
        yield return ("tunnel", Case(5, 3, "198.51.100.7", string.Empty, "ID"));
        yield return ("leading-zeros", Case(3, 2, "198.051.100.007"));
        yield return ("zone-id", Case(3, 2, "fe80::1%eth0"));
        yield return ("padded", Case(3, 2, " \t198.51.100.7\t "));
    }
}
