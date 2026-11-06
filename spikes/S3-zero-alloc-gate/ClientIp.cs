using System.Buffers.Binary;
using System.Net;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace S3;

/// <summary>
/// Client IPs are UInt128 everywhere. IPv4 is stored IPv4-mapped (::ffff:a.b.c.d), so a dual-mode TCP
/// listener (which reports ::ffff:a.b.c.d) and QUIC (which reports a.b.c.d) give the same key.
/// </summary>
public static class Ip
{
    public const ulong MappedPrefixLow = 0x0000_FFFF_0000_0000UL;

    public static UInt128 FromV4(uint v4) => new(0, MappedPrefixLow | v4);
    public static bool IsV4(UInt128 ip) => (ulong)(ip >> 64) == 0 && ((ulong)ip & 0xFFFF_FFFF_0000_0000UL) == MappedPrefixLow;

    /// <summary>From a socket address, no allocation (TryWriteBytes into a stack buffer).</summary>
    public static UInt128 From(IPAddress a)
    {
        Span<byte> b = stackalloc byte[16];
        if (!a.TryWriteBytes(b, out var n)) return 0;
        return n == 4 ? FromV4(BinaryPrimitives.ReadUInt32BigEndian(b)) : BinaryPrimitives.ReadUInt128BigEndian(b);
        // An IPv6 ::ffff:a.b.c.d from a dual-mode socket already *is* the mapped form, so nothing else to do.
    }

    /// <summary>The BCL route: IPAddress.TryParse(span) allocates an IPAddress (plus a ushort[8] for IPv6).</summary>
    public static bool TryParseBcl(ReadOnlySpan<char> s, out UInt128 ip)
    {
        if (IPAddress.TryParse(s, out var a)) { ip = From(a); return true; }
        ip = 0;
        return false;
    }

    /// <summary>
    /// Allocation-free parser for a bare IPv4 or IPv6 literal (what CF-Connecting-IP / X-Real-IP carry).
    /// Accepts: dotted quad (no leading zeros beyond "0"), IPv6 with one "::", embedded IPv4 tail.
    /// Rejects: zone ids, brackets, ports, whitespace (trim before calling).
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> s, out UInt128 ip)
    {
        ip = 0;
        if (s.Length is 0 or > 45) return false;
        if (s.IndexOf(':') < 0)
        {
            if (!TryParseV4(s, out var v4)) return false;
            ip = FromV4(v4);
            return true;
        }
        return TryParseV6(s, out ip);
    }

    private static bool TryParseV4(ReadOnlySpan<char> s, out uint v)
    {
        v = 0;
        int part = 0, val = 0, digits = 0;
        for (var i = 0; i <= s.Length; i++)
        {
            if (i == s.Length || s[i] == '.')
            {
                if (digits == 0 || part > 3) return false;
                v = (v << 8) | (uint)val;
                part++; val = 0; digits = 0;
                continue;
            }
            var c = s[i] - '0';
            if ((uint)c > 9) return false;
            if (digits > 0 && val == 0) return false; // leading zero ("01") is ambiguous (octal in some parsers): reject
            val = val * 10 + c;
            if (val > 255 || ++digits > 3) return false;
        }
        return part == 4;
    }

    private static bool TryParseV6(ReadOnlySpan<char> s, out UInt128 ip)
    {
        ip = 0;
        Span<ushort> g = stackalloc ushort[8];
        int n = 0, gap = -1, i = 0;
        if (s.StartsWith("::"))
        {
            gap = 0; i = 2;
            if (s.Length == 2) { ip = 0; return true; }
        }
        else if (s[0] == ':') return false;

        while (i < s.Length)
        {
            if (n == 8) return false;
            // IPv4 tail?
            var rest = s[i..];
            var nextColon = rest.IndexOf(':');
            var seg = nextColon < 0 ? rest : rest[..nextColon];
            if (nextColon < 0 && seg.IndexOf('.') >= 0)
            {
                if (n > 6 || !TryParseV4(seg, out var v4)) return false;
                g[n++] = (ushort)(v4 >> 16);
                g[n++] = (ushort)v4;
                i = s.Length;
                break;
            }
            if (seg.Length is 0 or > 4) return false;
            uint val = 0;
            foreach (var ch in seg)
            {
                int d = ch switch
                {
                    >= '0' and <= '9' => ch - '0',
                    >= 'a' and <= 'f' => ch - 'a' + 10,
                    >= 'A' and <= 'F' => ch - 'A' + 10,
                    _ => -1,
                };
                if (d < 0) return false;
                val = (val << 4) | (uint)d;
            }
            g[n++] = (ushort)val;
            i += seg.Length;
            if (i == s.Length) break;
            // at ':'
            i++;
            if (i < s.Length && s[i] == ':')
            {
                if (gap >= 0) return false;  // second "::"
                gap = n; i++;
                if (i == s.Length) break;     // trailing "::"
            }
            else if (i == s.Length) return false; // trailing single ':'
        }

        if (gap < 0 && n != 8) return false;
        if (gap >= 0 && n > 7) return false;
        UInt128 r = 0;
        if (gap < 0)
        {
            for (var k = 0; k < 8; k++) r = (r << 16) | g[k];
        }
        else
        {
            var zeros = 8 - n;
            for (var k = 0; k < gap; k++) r = (r << 16) | g[k];
            for (var k = 0; k < zeros; k++) r <<= 16;
            for (var k = gap; k < n; k++) r = (r << 16) | g[k];
        }
        ip = r;
        return true;
    }
}

// ---------------------------------------------------------------------------------------------
// Per-connection state
// ---------------------------------------------------------------------------------------------

/// <summary>Computed once per connection in a ListenOptions.Use middleware (stage A).</summary>
public interface IClientInfoFeature
{
    ClientInfo Info { get; }
}

/// <summary>
/// Immutable: on HTTP/2 (and H3) every stream's feature collection falls back to the same connection feature
/// collection, so concurrent requests on one connection share this instance.
/// </summary>
public sealed class ClientInfo : IClientInfoFeature
{
    public required UInt128 PeerIp { get; init; }
    public ushort Country { get; init; }           // index into the snapshot's country table; 0 = unknown (placeholder)
    public bool PeerIsTrustedProxy { get; init; }  // CF edge / cloudflared / trusted CIDR → honour CF-Connecting-IP
    ClientInfo IClientInfoFeature.Info => this;
}

public static class ConnectionGate
{
    public static readonly object ItemsKey = new();
    public static long Connections;
    /// <summary>Peers whose CF-Connecting-IP we honour (spike: a single CIDR check stand-in).</summary>
    public static Func<UInt128, bool> IsTrustedProxy = static _ => false;

    /// <summary>Stage A: one ClientInfo per TCP connection, exposed both as a feature and via Items (to compare).</summary>
    public static ListenOptions UseClientInfo(this ListenOptions lo)
    {
        lo.Use(static next => ctx =>
        {
            Interlocked.Increment(ref Connections);
            var ip = ctx.RemoteEndPoint is IPEndPoint ep ? Ip.From(ep.Address) : 0;
            var info = new ClientInfo
            {
                PeerIp = ip,
                PeerIsTrustedProxy = IsTrustedProxy(ip),
                Country = (ushort)((ulong)ip % 250), // placeholder: "country lookup" result
            };
            ctx.Features.Set<IClientInfoFeature>(info);
            // Comparison only: Items is ONE plain Dictionary per connection, shared (unsynchronised) by all H2 streams.
            ctx.Items[ItemsKey] = info;
            return next(ctx);
        });
        return lo;
    }
}
