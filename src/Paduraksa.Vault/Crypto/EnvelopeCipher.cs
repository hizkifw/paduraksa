using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Paduraksa.Vault.Crypto;

/// <summary>
/// What a sealed value is bound to: the ID of the row it lives in, what the value is (a secret's kind name,
/// or the purpose of a key), and its version. They go into the AAD of both AES-GCM operations, so a sealed value moved to
/// another row, kind or version fails to open instead of opening as something else.
/// </summary>
/// <param name="Id">The typed ID's string form (<c>sec_...</c>, <c>acct_...</c>), whose prefix also separates the ID spaces.</param>
/// <param name="Purpose">A stable name: <c>dns-api-token</c>, <c>hmac-signing-key</c>, <c>acme-account-key</c>...</param>
/// <param name="Version">1 for the first value of a row, +1 on each replacement.</param>
internal readonly record struct SealContext(string Id, string Purpose, int Version);

/// <summary>A sealed value whose KEK is not the one asked to open it.</summary>
internal sealed class KekMismatchException() : CryptographicException("The value is sealed under a different master key.");

/// <summary>
/// Envelope encryption of one value: a random 256-bit data-encryption key (DEK) per value encrypts it with
/// AES-256-GCM, and the KEK wraps the DEK, also with AES-256-GCM. This is the one place the vault encrypts at rest; secrets
/// and keys (and later certificate keys) all store the output of <see cref="Seal"/> in a <c>BLOB</c> column.
/// </summary>
/// <remarks>
/// <para>
/// <b>Format, version 1</b> (all fixed-width but the ciphertext), the whole value is one byte string:
/// </para>
/// <code>
/// offset  size  field
///      0     1  format (1)
///      1     8  KEK ID (Kek.Id) the DEK is wrapped under
///      9    12  wrap nonce, random
///     21    32  wrapped DEK (AES-256-GCM ciphertext of the DEK)
///     53    16  wrap tag
///     69    12  data nonce, random
///     81     n  ciphertext of the value (same length as the plaintext)
///   81+n    16  data tag
/// </code>
/// <para>
/// The format byte selects the algorithms, so a later format (another cipher, a longer nonce scheme) can sit beside this one
/// in the same column; <see cref="Open"/> refuses a format it does not know.
/// </para>
/// <para>
/// <b>AAD.</b> Both operations authenticate <c>domain || format || (KEK ID, wrap only) || len(id) || id || len(purpose) ||
/// purpose || version</c>: lengths are 16-bit big-endian, the version a 32-bit big-endian integer, and the domain byte is
/// <c>'W'</c> for the wrap and <c>'D'</c> for the data, so one cannot stand in for the other. Binding the context (not
/// just the row ID) into the wrap as well means a DEK cannot be moved between rows either.
/// </para>
/// <para>
/// <b>Why AES-GCM wrap, not AES-KW.</b> .NET has no AES key wrap, and writing RFC 3394 by hand in a trust boundary is a worse
/// trade than reusing the one authenticated mode we already depend on. Wrapping with GCM also lets the wrap carry the AAD,
/// which KW cannot. The cost is a nonce: a fresh random 96-bit nonce per wrap, under a KEK that wraps one DEK per value
/// version, stays far below the 2^32-wraps bound. The DEK itself encrypts exactly one message, so it can never see a
/// repeated nonce.
/// </para>
/// <para>
/// <b>Rotation</b> (<see cref="Rewrap"/>) replaces only the KEK ID, wrap nonce, wrapped DEK and wrap tag; the data section is
/// copied byte for byte.
/// </para>
/// </remarks>
internal static class EnvelopeCipher
{
    public const byte Format1 = 1;

    private const int NonceSize = 12;

    private const int TagSize = 16;

    private const int DekSize = 32;

    private const int WrapOffset = 1 + Kek.IdLength;

    private const int WrappedDekOffset = WrapOffset + NonceSize;

    private const int WrapTagOffset = WrappedDekOffset + DekSize;

    private const int DataNonceOffset = WrapTagOffset + TagSize;

    private const int DataOffset = DataNonceOffset + NonceSize;

    /// <summary>Bytes a sealed value adds to its plaintext.</summary>
    public const int Overhead = DataOffset + TagSize;

    private const byte WrapDomain = (byte)'W';

    private const byte DataDomain = (byte)'D';

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Encrypts <paramref name="plaintext"/> under a fresh DEK, wrapped by <paramref name="kek"/>.</summary>
    public static byte[] Seal(Kek kek, ReadOnlySpan<byte> plaintext, in SealContext context)
    {
        var sealedValue = new byte[Overhead + plaintext.Length];
        sealedValue[0] = Format1;
        kek.Id.CopyTo(sealedValue.AsSpan(1, Kek.IdLength));

        Span<byte> dek = stackalloc byte[DekSize];
        try
        {
            RandomNumberGenerator.Fill(dek);
            Wrap(kek, dek, sealedValue, context);

            var dataNonce = sealedValue.AsSpan(DataNonceOffset, NonceSize);
            RandomNumberGenerator.Fill(dataNonce);
            using var aes = new AesGcm(dek, TagSize);
            aes.Encrypt(
                dataNonce,
                plaintext,
                sealedValue.AsSpan(DataOffset, plaintext.Length),
                sealedValue.AsSpan(DataOffset + plaintext.Length, TagSize),
                Aad(DataDomain, Format1, [], context));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }

        return sealedValue;
    }

    /// <summary>Decrypts a value made by <see cref="Seal"/> for the same <paramref name="context"/>.</summary>
    /// <exception cref="KekMismatchException">The value is wrapped under another KEK.</exception>
    /// <exception cref="CryptographicException">The value is malformed, of an unknown format, tampered with, or bound to another context.</exception>
    public static SecretBuffer Open(Kek kek, ReadOnlySpan<byte> sealedValue, in SealContext context)
    {
        Span<byte> dek = stackalloc byte[DekSize];
        try
        {
            Unwrap(kek, sealedValue, context, dek);
            var plaintext = SecretBuffer.Allocate(sealedValue.Length - Overhead);
            try
            {
                using var aes = new AesGcm(dek, TagSize);
                aes.Decrypt(
                    sealedValue.Slice(DataNonceOffset, NonceSize),
                    sealedValue.Slice(DataOffset, plaintext.Length),
                    sealedValue[(DataOffset + plaintext.Length)..],
                    plaintext.Fill,
                    Aad(DataDomain, Format1, [], context));
                return plaintext;
            }
            catch
            {
                plaintext.Dispose();
                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    /// <summary>Re-wraps the DEK of <paramref name="sealedValue"/> from <paramref name="oldKek"/> to <paramref name="newKek"/>, leaving the encrypted value untouched.</summary>
    public static byte[] Rewrap(Kek oldKek, Kek newKek, ReadOnlySpan<byte> sealedValue, in SealContext context)
    {
        Span<byte> dek = stackalloc byte[DekSize];
        try
        {
            Unwrap(oldKek, sealedValue, context, dek);
            var rewrapped = sealedValue.ToArray();
            newKek.Id.CopyTo(rewrapped.AsSpan(1, Kek.IdLength));
            Wrap(newKek, dek, rewrapped, context);
            return rewrapped;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    /// <summary>The ID of the KEK a sealed value is wrapped under, or an empty span if it is not a sealed value of a known format.</summary>
    public static ReadOnlySpan<byte> KekIdOf(ReadOnlySpan<byte> sealedValue) =>
        sealedValue.Length >= Overhead && sealedValue[0] == Format1 ? sealedValue.Slice(1, Kek.IdLength) : default;

    private static void Wrap(Kek kek, ReadOnlySpan<byte> dek, Span<byte> sealedValue, in SealContext context)
    {
        var nonce = sealedValue.Slice(WrapOffset, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(kek.Bytes, TagSize);
        aes.Encrypt(
            nonce,
            dek,
            sealedValue.Slice(WrappedDekOffset, DekSize),
            sealedValue.Slice(WrapTagOffset, TagSize),
            Aad(WrapDomain, Format1, kek.Id, context));
    }

    private static void Unwrap(Kek kek, ReadOnlySpan<byte> sealedValue, in SealContext context, Span<byte> dek)
    {
        if (sealedValue.Length < Overhead)
        {
            throw new CryptographicException("The sealed value is too short.");
        }

        if (sealedValue[0] != Format1)
        {
            throw new CryptographicException($"The sealed value has an unsupported format ({sealedValue[0]}).");
        }

        var kekId = sealedValue.Slice(1, Kek.IdLength);
        if (!CryptographicOperations.FixedTimeEquals(kekId, kek.Id))
        {
            throw new KekMismatchException();
        }

        using var aes = new AesGcm(kek.Bytes, TagSize);
        aes.Decrypt(
            sealedValue.Slice(WrapOffset, NonceSize),
            sealedValue.Slice(WrappedDekOffset, DekSize),
            sealedValue.Slice(WrapTagOffset, TagSize),
            dek,
            Aad(WrapDomain, Format1, kekId, context));
    }

    private static byte[] Aad(byte domain, byte format, ReadOnlySpan<byte> kekId, in SealContext context)
    {
        var idLength = StrictUtf8.GetByteCount(context.Id);
        var purposeLength = StrictUtf8.GetByteCount(context.Purpose);
        if (idLength > ushort.MaxValue || purposeLength > ushort.MaxValue)
        {
            throw new ArgumentException("The seal context is too long.", nameof(context));
        }

        var aad = new byte[2 + kekId.Length + 2 + idLength + 2 + purposeLength + 4];
        var span = aad.AsSpan();
        span[0] = domain;
        span[1] = format;
        kekId.CopyTo(span[2..]);
        span = span[(2 + kekId.Length)..];
        BinaryPrimitives.WriteUInt16BigEndian(span, (ushort)idLength);
        StrictUtf8.GetBytes(context.Id, span[2..]);
        span = span[(2 + idLength)..];
        BinaryPrimitives.WriteUInt16BigEndian(span, (ushort)purposeLength);
        StrictUtf8.GetBytes(context.Purpose, span[2..]);
        span = span[(2 + purposeLength)..];
        BinaryPrimitives.WriteInt32BigEndian(span, context.Version);
        return aad;
    }
}
