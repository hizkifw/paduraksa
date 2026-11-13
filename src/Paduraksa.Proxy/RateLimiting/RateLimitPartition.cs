using System.Buffers.Binary;
using System.Security.Cryptography;

using Paduraksa.Proxy.Net;

namespace Paduraksa.Proxy.RateLimiting;

/// <summary>
/// Turns a rate-limit partition key into the table's 48-bit hash: the client IP (IPv4 whole, IPv6 /64), IP + canonical
/// path, or a header value. Allocation-free, so it can run on the request path.
/// </summary>
/// <remarks>
/// <para>The hash is keyed by a per-process random seed (<see cref="Shared"/>), so a client cannot work out which values
/// collide with its own: a precomputed collision would otherwise let it share one bucket with another client, or hide in
/// one. <see cref="RateLimitPartition"/> with an explicit seed exists for tests, which pin the one property outsiders
/// can't see: two seeds give two different hashes for the same bytes.</para>
/// <para>The three kinds are domain-separated, so an address, the same text as a header value and an IP-plus-path key
/// never share a bucket by accident (a header that spells an IP is not that client).</para>
/// </remarks>
public sealed class RateLimitPartition
{
    /// <summary>The hash is 48 bits, the width the table's key leaves for it.</summary>
    public const ulong HashMask = (1UL << 48) - 1;

    private const ulong FnvPrime = 0x0000_0100_0000_01B3UL;
    private const ulong DomainStep = 0x9E37_79B9_7F4A_7C15UL;
    private const ulong IpDomain = 1;
    private const ulong IpAndPathDomain = 2;
    private const ulong HeaderDomain = 3;

    /// <summary>The process's partitioner: one seed, drawn when the process starts.</summary>
    public static RateLimitPartition Shared { get; } = new(ProcessSeed());

    private readonly ulong _seed;

    public RateLimitPartition(ulong seed) => _seed = seed;

    /// <summary>
    /// The client's key. An IPv4 client is keyed by the whole address, an IPv6 client by its /64: one end site holds a
    /// /64 as a matter of course, so per-address keys would be free to rotate (docs/design/waf.md "Rate limiting", <see cref="IpKey.MaskTo(int, int)"/>).
    /// </summary>
    public ulong ForIp(IpKey client) => Hash128(IpDomain, client.MaskTo(32, 64).Value);

    /// <summary>The key for a client on one path: the address as <see cref="ForIp"/>, plus the canonical path the gate
    /// matched on, so <c>/api</c> and <c>//API;</c> are the same partition. Case is folded (ASCII), because route
    /// matching ignores it: otherwise <c>/API</c> would spend a separate budget from <c>/api</c>.</summary>
    public ulong ForIpAndPath(IpKey client, ReadOnlySpan<char> path)
    {
        var hash = Start(IpAndPathDomain);
        var address = client.MaskTo(32, 64).Value;
        hash = Step(hash, (ulong)address);
        hash = Step(hash, (ulong)(address >> 64));
        return AppendPath(hash, path);
    }

    /// <summary>The key for one header value (a cookie, an API key), as written in the request. Every empty value is the
    /// same key, so a caller partitioning by header must not build one from an absent or empty header.</summary>
    public ulong ForHeader(ReadOnlySpan<char> value) => AppendText(Start(HeaderDomain), value);

    private ulong Hash128(ulong domain, UInt128 value)
    {
        var hash = Start(domain);
        hash = Step(hash, (ulong)value);
        return Avalanche(Step(hash, (ulong)(value >> 64)));
    }

    private static ulong AppendText(ulong hash, ReadOnlySpan<char> text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            hash = Step(hash, text[i]);
        }

        return Avalanche(hash);
    }

    /// <summary>Like <see cref="AppendText"/>, with ASCII case folded: the gate matches paths ignoring case.</summary>
    private static ulong AppendPath(ulong hash, ReadOnlySpan<char> path)
    {
        for (var i = 0; i < path.Length; i++)
        {
            var c = path[i];
            hash = Step(hash, c is >= 'A' and <= 'Z' ? (char)(c | 0x20) : c);
        }

        return Avalanche(hash);
    }

    private ulong Start(ulong domain) => Avalanche(_seed ^ (domain * DomainStep));

    private static ulong Step(ulong hash, ulong value) => (hash ^ value) * FnvPrime;

    /// <summary>Murmur3's 64-bit finalizer (Thomas Wang's shift-multiply-xor rounds).</summary>
    private static ulong Avalanche(ulong hash)
    {
        hash ^= hash >> 33;
        hash *= 0xFF51_AFD7_ED55_8CCDUL;
        hash ^= hash >> 33;
        hash *= 0xC4CE_B9FE_1A85_EC53UL;
        hash ^= hash >> 33;
        return hash & HashMask;
    }

    private static ulong ProcessSeed()
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        RandomNumberGenerator.Fill(bytes);
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }
}
