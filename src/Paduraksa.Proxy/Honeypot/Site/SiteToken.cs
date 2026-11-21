using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Paduraksa.Proxy.Honeypot;

/// <summary>
/// The token of a trap URL: 20 lowercase base32 characters, 100 bits. The first 64 carry the link kind (4 bits), the parent's page kind
/// (4) and ID (28), and the page's own ID (28); the last 36 are a truncated HMAC-SHA-256 tag over those fields, the page kind the URL
/// names, and the URL's own text before the token. The first 64 bits are XORed with a mix of the tag and a per-site salt, so
/// tokens of neighbouring pages share no leading characters and the structure doesn't show. A 36-bit tag makes a guessed token a one-in-68-billion hit, which only matters
/// for a page that is itself generated for anyone, so a longer one would only lengthen the URL.
/// </summary>
internal static class SiteToken
{
    public const int Length = 20;

    /// <summary>The IDs a token can carry: 28 bits.</summary>
    public const uint MaxLocal = (1U << 28) - 1;

    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";
    private const ulong TagMask = (1UL << 36) - 1;

    /// <summary>The longest URL text before a token that is verified; anything longer is refused before any hashing.</summary>
    public const int MaxTail = 120;

    /// <summary>Writes the token of a link to <paramref name="destination"/> (20 bytes).</summary>
    public static void Encode(
        ReadOnlySpan<byte> key,
        ulong salt,
        HoneypotPageKind pageKind,
        HoneypotLinkKind via,
        uint local,
        HoneypotPageKind parentKind,
        uint parentLocal,
        ReadOnlySpan<byte> tail,
        Span<byte> destination)
    {
        var head = ((ulong)via << 60) | ((ulong)parentKind << 56) | ((ulong)parentLocal << 28) | local;
        var tag = Tag(key, pageKind, via, local, parentKind, parentLocal, tail);
        var raw = ((UInt128)(head ^ SiteRandom.Mix(tag ^ salt)) << 36) | tag;
        for (var i = 0; i < Length; i++)
        {
            destination[i] = (byte)Alphabet[(int)((raw >> (95 - (5 * i))) & 31)];
        }
    }

    /// <summary>
    /// Reads and verifies a token. <paramref name="token"/> is exactly the 20 characters; anything else, any character outside the
    /// alphabet and any tag that does not match is refused, and the work is one HMAC over at most <see cref="MaxTail"/> + 12 bytes.
    /// </summary>
    public static bool TryDecode(
        ReadOnlySpan<byte> key,
        ulong salt,
        HoneypotPageKind pageKind,
        ReadOnlySpan<char> token,
        ReadOnlySpan<byte> tail,
        out HoneypotLinkKind via,
        out uint local,
        out HoneypotPageKind parentKind,
        out uint parentLocal)
    {
        via = HoneypotLinkKind.Direct;
        local = 0;
        parentKind = HoneypotPageKind.Index;
        parentLocal = 0;
        if (token.Length != Length || tail.Length > MaxTail)
        {
            return false;
        }

        UInt128 raw = 0;
        foreach (var c in token)
        {
            var value = c is >= 'a' and <= 'z' ? c - 'a' : c is >= '2' and <= '7' ? c - '2' + 26 : -1;
            if (value < 0)
            {
                return false;
            }

            raw = (raw << 5) | (uint)value;
        }

        var tag = (ulong)raw & TagMask;
        var head = (ulong)(raw >> 36) ^ SiteRandom.Mix(tag ^ salt);
        var viaValue = (int)(head >> 60);
        var parentKindValue = (int)((head >> 56) & 0xF);

        // A token is never made for a URL that carries no link, and the kind fields have gaps a forger could land in.
        if (viaValue is 0 or > (int)HoneypotLinkKind.Image || parentKindValue > (int)HoneypotPageKind.NotFound)
        {
            return false;
        }

        var candidateVia = (HoneypotLinkKind)viaValue;
        var candidateParentKind = (HoneypotPageKind)parentKindValue;
        var candidateParentLocal = (uint)((head >> 28) & MaxLocal);
        var candidateLocal = (uint)(head & MaxLocal);
        var expected = Tag(key, pageKind, candidateVia, candidateLocal, candidateParentKind, candidateParentLocal, tail);
        Span<byte> a = stackalloc byte[8];
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(a, expected);
        BinaryPrimitives.WriteUInt64LittleEndian(b, tag);
        if (!CryptographicOperations.FixedTimeEquals(a, b))
        {
            return false;
        }

        via = candidateVia;
        local = candidateLocal;
        parentKind = candidateParentKind;
        parentLocal = candidateParentLocal;
        return true;
    }

    private static ulong Tag(
        ReadOnlySpan<byte> key,
        HoneypotPageKind pageKind,
        HoneypotLinkKind via,
        uint local,
        HoneypotPageKind parentKind,
        uint parentLocal,
        ReadOnlySpan<byte> tail)
    {
        Span<byte> input = stackalloc byte[12 + MaxTail];
        input[0] = SiteKeys.TokenDomain;
        input[1] = (byte)pageKind;
        input[2] = (byte)via;
        input[3] = (byte)parentKind;
        BinaryPrimitives.WriteUInt32LittleEndian(input[4..], local);
        BinaryPrimitives.WriteUInt32LittleEndian(input[8..], parentLocal);
        // Resolve refuses a longer tail, so a URL built from one would be dead.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(tail.Length, MaxTail);
        var length = tail.Length;
        tail.CopyTo(input[12..]);

        Span<byte> mac = stackalloc byte[SiteKeys.MacLength];
        SiteKeys.Mac(key, input[..(12 + length)], mac);
        return BinaryPrimitives.ReadUInt64BigEndian(mac) >> 28;
    }
}
