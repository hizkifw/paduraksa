using System.Security.Cryptography;

namespace Paduraksa.Proxy.Honeypot;

/// <summary>
/// The keyed hashing the site is built on. The key is the site key the gate derives under the proof-of-work key, never a vault secret, so
/// the vault's ban on <c>HMACSHA256</c> in this project does not bear on it. <see cref="CryptographicOperations.HmacData(HashAlgorithmName, ReadOnlySpan{byte}, ReadOnlySpan{byte}, Span{byte})"/>
/// is the real HMAC: a hash of key then message would let anyone who holds one valid token extend it.
/// </summary>
internal static class SiteKeys
{
    public const int KeyLength = 32;
    public const int MacLength = 32;

    // One byte of domain separation keeps a page's content stream, a token and a canary from ever sharing an HMAC input.
    public const byte TokenDomain = 0x01;
    public const byte PageDomain = 0x02;
    public const byte CanaryDomain = 0x03;
    public const byte SaltDomain = 0x04;
    public const byte PrefixDomain = 0x10;
    public const byte NameDomain = 0x11;
    public const byte CastDomain = 0x12;

    public static void Mac(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data, Span<byte> destination) =>
        CryptographicOperations.HmacData(HashAlgorithmName.SHA256, key, data, destination);

    /// <summary>The generator of the page of <paramref name="kind"/> with ID <paramref name="local"/>.</summary>
    public static SiteRandom PageRandom(ReadOnlySpan<byte> key, HoneypotPageKind kind, uint local) => DomainRandom(key, PageDomain, (byte)kind, local);

    public static SiteRandom DomainRandom(ReadOnlySpan<byte> key, byte domain, byte sub = 0, uint local = 0)
    {
        Span<byte> message = stackalloc byte[6];
        message[0] = domain;
        message[1] = sub;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(message[2..], local);
        Span<byte> mac = stackalloc byte[MacLength];
        Mac(key, message, mac);
        return new SiteRandom(mac);
    }
}
