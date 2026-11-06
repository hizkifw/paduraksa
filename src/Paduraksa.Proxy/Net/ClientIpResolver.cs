using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Paduraksa.Proxy.Net;

/// <summary>Where a resolved client IP came from.</summary>
public enum ClientIpSource : byte
{
    /// <summary>The TCP/QUIC peer.</summary>
    Socket = 0,

    /// <summary>The PROXY protocol header (stage A).</summary>
    ProxyProtocol = 1,

    /// <summary><c>X-Forwarded-For</c>, or the generic policy's configured header.</summary>
    XForwardedFor = 2,

    CfConnectingIp = 3,

    TrueClientIp = 4,
}

public enum ClientIpOutcome : byte
{
    /// <summary>The peer isn't a trusted proxy (or the policy is off): headers were ignored.</summary>
    PeerNotTrusted = 0,

    /// <summary>The client IP came from a header sent by a trusted peer.</summary>
    FromHeader = 1,

    /// <summary>Trusted peer, but no usable header: fell back to the peer. Worth a counter.</summary>
    HeaderMissing = 2,

    /// <summary>Trusted peer, but the header didn't parse: fell back to the peer. Worth a counter.</summary>
    HeaderInvalid = 3,
}

/// <summary>Result of <see cref="ClientIpResolver.Resolve(ClientInfo, IHeaderDictionary, TrustedProxyPolicy)"/>.</summary>
public readonly record struct ClientIpResolution(IpKey Address, ClientIpSource Source, ClientIpOutcome Outcome)
{
    /// <summary>The peer is a trusted proxy (so its Cloudflare / forwarding headers may be used, e.g. <c>CF-IPCountry</c>,
    /// and needn't be stripped).</summary>
    public bool PeerTrusted => Outcome != ClientIpOutcome.PeerNotTrusted;

    /// <summary>A trusted peer sent no usable header, so the peer address was used. The caller bumps its counter
    /// (the resolver itself has no side effects).</summary>
    public bool IsFallback => Outcome is ClientIpOutcome.HeaderMissing or ClientIpOutcome.HeaderInvalid;
}

/// <summary>
/// Resolves a request's client IP behind trusted proxies and Cloudflare (PLAN §7.2 C, §7.6). Pure and
/// allocation-free: no logging, no counters, no state.
/// </summary>
/// <remarks>
/// <para><b>Peer.</b> The "peer" is the connection-level address from <see cref="ClientInfo"/>: the PROXY
/// protocol source if a header was received, otherwise the socket peer. So a chain like Cloudflare → L4 load
/// balancer (PROXY protocol) → Paduraksa works, and a PROXY-protocol client that isn't itself a trusted proxy
/// is final.</para>
/// <para><b>Precedence.</b> If the policy is off or the peer isn't in its trusted set, the peer is the client
/// and every header is ignored. Otherwise:</para>
/// <list type="bullet">
/// <item><b>Generic</b>: the configured header (default <c>X-Forwarded-For</c>). All header lines are read as one
/// comma-separated list, right to left; the first entry not in the trusted set is the client. If every entry is
/// trusted, the left-most one is. Entries may carry a port (<c>1.2.3.4:5678</c>, <c>[2001:db8::1]:443</c>).</item>
/// <item><b>Cloudflare Proxy / Tunnel</b>: <c>CF-Connecting-IP</c>; else <c>True-Client-IP</c> if enabled;
/// else the right-most untrusted <c>X-Forwarded-For</c> entry if that fallback is enabled. The first header
/// <em>present</em> decides: if it doesn't parse, the result is <see cref="ClientIpOutcome.HeaderInvalid"/> rather
/// than falling through to a lower-priority (and possibly client-supplied) header. CF headers are single-valued:
/// a repeated or comma-separated value is invalid.</item>
/// </list>
/// <para>Any missing or unparsable header from a trusted peer falls back to the peer address, flagged in
/// <see cref="ClientIpResolution.Outcome"/>.</para>
/// </remarks>
public static class ClientIpResolver
{
    /// <summary>Resolves from the connection's <see cref="ClientInfo"/>.</summary>
    public static ClientIpResolution Resolve(ClientInfo connection, IHeaderDictionary headers, TrustedProxyPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return Resolve(
            connection.Address,
            connection.IsFromProxyProtocol ? ClientIpSource.ProxyProtocol : ClientIpSource.Socket,
            headers,
            policy);
    }

    /// <summary>Resolves from an explicit peer (e.g. <c>HttpContext.Connection.RemoteIpAddress</c> where no
    /// <see cref="ClientInfo"/> feature exists, such as HTTP/3 for now).</summary>
    /// <param name="peer">The connection-level address.</param>
    /// <param name="peerSource"><see cref="ClientIpSource.Socket"/> or <see cref="ClientIpSource.ProxyProtocol"/>.</param>
    /// <param name="headers">The request headers.</param>
    /// <param name="policy">The effective trusted-proxy policy.</param>
    public static ClientIpResolution Resolve(IpKey peer, ClientIpSource peerSource, IHeaderDictionary headers, TrustedProxyPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(policy);

        if (!policy.IsTrustedPeer(peer))
        {
            return new ClientIpResolution(peer, peerSource, ClientIpOutcome.PeerNotTrusted);
        }

        if (policy.Mode == TrustedProxyMode.Generic)
        {
            return FromList(headers[policy.ForwardedHeader!], policy.TrustedPeers, peer, peerSource);
        }

        var connectingIp = headers[CloudflareHeaders.ConnectingIp];
        if (connectingIp.Count != 0)
        {
            return FromSingle(connectingIp, ClientIpSource.CfConnectingIp, peer, peerSource);
        }

        if (policy.UseTrueClientIp)
        {
            var trueClientIp = headers[CloudflareHeaders.TrueClientIp];
            if (trueClientIp.Count != 0)
            {
                return FromSingle(trueClientIp, ClientIpSource.TrueClientIp, peer, peerSource);
            }
        }

        if (policy.ForwardedHeader is { } forwardedHeader)
        {
            var forwarded = headers[forwardedHeader];
            if (forwarded.Count != 0)
            {
                return FromList(forwarded, policy.TrustedPeers, peer, peerSource);
            }
        }

        return new ClientIpResolution(peer, peerSource, ClientIpOutcome.HeaderMissing);
    }

    private static ClientIpResolution FromSingle(StringValues values, ClientIpSource source, IpKey peer, ClientIpSource peerSource)
    {
        if (values.Count == 1 && IpParser.TryParse(TrimOws(values[0]), out var address))
        {
            return new ClientIpResolution(address, source, ClientIpOutcome.FromHeader);
        }

        return new ClientIpResolution(peer, peerSource, ClientIpOutcome.HeaderInvalid);
    }

    private static ClientIpResolution FromList(StringValues values, CidrSet trusted, IpKey peer, ClientIpSource peerSource)
    {
        if (IsBlank(values))
        {
            return new ClientIpResolution(peer, peerSource, ClientIpOutcome.HeaderMissing);
        }

        IpKey leftmost = default;
        for (var v = values.Count - 1; v >= 0; v--)
        {
            var line = values[v].AsSpan();
            var end = line.Length;
            while (true)
            {
                var comma = line[..end].LastIndexOf(',');
                if (!TryParseListEntry(TrimOws(line[(comma + 1)..end]), out var address))
                {
                    return new ClientIpResolution(peer, peerSource, ClientIpOutcome.HeaderInvalid);
                }

                if (!trusted.Contains(address))
                {
                    return new ClientIpResolution(address, ClientIpSource.XForwardedFor, ClientIpOutcome.FromHeader);
                }

                leftmost = address;
                if (comma < 0)
                {
                    break;
                }

                end = comma;
            }
        }

        // Every hop is a trusted proxy: the left-most entry is the furthest we can see.
        return new ClientIpResolution(leftmost, ClientIpSource.XForwardedFor, ClientIpOutcome.FromHeader);
    }

    /// <summary>An address, optionally with a port: <c>a.b.c.d[:port]</c> or <c>[v6][:port]</c>.</summary>
    private static bool TryParseListEntry(ReadOnlySpan<char> entry, out IpKey address)
    {
        if (IpParser.TryParse(entry, out address))
        {
            return true;
        }

        if (entry.Length > 2 && entry[0] == '[')
        {
            var close = entry.IndexOf(']');
            if (close < 0)
            {
                return false;
            }

            var after = entry[(close + 1)..];
            return (after.Length == 0 || (after[0] == ':' && IsPort(after[1..])))
                && IpParser.TryParseIPv6(entry[1..close], out address);
        }

        var colon = entry.IndexOf(':');
        return colon > 0
            && entry.LastIndexOf(':') == colon
            && IsPort(entry[(colon + 1)..])
            && IpParser.TryParseIPv4(entry[..colon], out address);
    }

    private static bool IsPort(ReadOnlySpan<char> s)
    {
        if (s.Length is 0 or > 5 || (s.Length > 1 && s[0] == '0'))
        {
            return false;
        }

        var value = 0;
        foreach (var c in s)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }

            value = (value * 10) + (c - '0');
        }

        return value <= ushort.MaxValue;
    }

    private static bool IsBlank(StringValues values)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (!TrimOws(values[i]).IsEmpty)
            {
                return false;
            }
        }

        return true;
    }

    private static ReadOnlySpan<char> TrimOws(ReadOnlySpan<char> s) => s.Trim(" \t");
}
