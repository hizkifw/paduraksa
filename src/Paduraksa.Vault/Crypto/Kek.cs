using System.Security.Cryptography;
using Paduraksa.Core;
using Paduraksa.Core.Vault;

namespace Paduraksa.Vault.Crypto;

/// <summary>
/// The key-encryption key (PLAN §8.3): 32 random bytes that wrap every data-encryption key. Held in a pinned array for
/// the life of the process (every secret operation needs it) and zeroed on disposal.
/// </summary>
/// <remarks>
/// <para>
/// <b>Text form.</b> The KEK is written and read as standard base64 of exactly 32 bytes (44 characters with padding),
/// whether it comes from <c>PADURAKSA_MASTER_KEY</c>, a file or <c>master.key</c>. Anything else (base64url, hex, a wrong
/// length, stray characters, non-canonical padding, an all-zero key) is refused rather than guessed at: a KEK that parses
/// differently than its owner meant would only show up as a vault nobody can open.
/// </para>
/// <para>
/// <b>Identity.</b> <see cref="Id"/> (8 bytes, stored in every sealed value) and <see cref="CheckValue"/> (32 bytes, stored
/// once in the database) are HMAC-SHA-256 outputs of the KEK over two fixed labels. A keyed hash reveals nothing about the
/// key, and comparing it at startup tells a wrong KEK from a right one without decrypting anything.
/// </para>
/// </remarks>
internal sealed class Kek : IDisposable
{
    public const int Length = 32;

    public const int IdLength = 8;

    /// <summary>The length of the text form: standard base64 of 32 bytes.</summary>
    public const int TextLength = 44;

    /// <summary>The size of a key file: the <see cref="TextLength"/> characters and a newline.</summary>
    public const int FileLength = TextLength + 1;

    private static readonly byte[] CheckLabel = "paduraksa/vault/kek-check/v1"u8.ToArray();

    private static readonly byte[] IdLabel = "paduraksa/vault/kek-id/v1"u8.ToArray();

    private readonly byte[] _key;

    private Kek(byte[] key)
    {
        _key = key;
        Id = HMACSHA256.HashData(key, IdLabel)[..IdLength];
        CheckValue = HMACSHA256.HashData(key, CheckLabel);
    }

    /// <summary>The first <see cref="IdLength"/> bytes of the key's identity hash; stored in every sealed value.</summary>
    public byte[] Id { get; }

    /// <summary>The 32-byte key-confirmation value the database stores to recognise this KEK at startup.</summary>
    public byte[] CheckValue { get; }

    internal ReadOnlySpan<byte> Bytes => _key;

    public static Kek Generate()
    {
        var key = GC.AllocateArray<byte>(Length, pinned: true);
        RandomNumberGenerator.Fill(key);
        return new Kek(key);
    }

    /// <summary>Wraps 32 bytes (copied into the pinned array).</summary>
    public static Kek FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Length)
        {
            throw new FormatException($"A master key is exactly {Length} bytes.");
        }

        var key = GC.AllocateArray<byte>(Length, pinned: true);
        bytes.CopyTo(key);
        return new Kek(key);
    }

    /// <summary>Reads the text form (surrounding whitespace is ignored). Never echoes the text in the error.</summary>
    /// <exception cref="FormatException">It is not base64 of exactly 32 bytes, or it is all zeros.</exception>
    public static Kek Parse(ReadOnlySpan<char> text)
    {
        Span<byte> bytes = stackalloc byte[Length];
        try
        {
            return MasterKeyFormat.TryDecode(text.Trim(), bytes) ? FromBytes(bytes) : throw NotAKey();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>
    /// Writes the text form (<see cref="TextLength"/> ASCII characters) for <c>master.key</c> into <paramref name="destination"/>.
    /// It never becomes a string, so the caller can zero what it wrote (a string could not be).
    /// </summary>
    public void WriteEncoded(Span<byte> destination)
    {
        if (System.Buffers.Text.Base64.EncodeToUtf8(_key, destination, out _, out var written) != System.Buffers.OperationStatus.Done || written != TextLength)
        {
            throw new ArgumentException($"The destination must hold {TextLength} bytes.", nameof(destination));
        }
    }

    public void Dispose() => CryptographicOperations.ZeroMemory(_key);

    private static FormatException NotAKey() =>
        new("The master key must be standard base64 of exactly 32 random bytes (44 characters, e.g. from `openssl rand -base64 32`), and not all zeros.");

    /// <summary>Whether <paramref name="checkValue"/> (as stored in the database) is this KEK's, compared in constant time.</summary>
    public bool Matches(ReadOnlySpan<byte> checkValue) => CryptographicOperations.FixedTimeEquals(CheckValue, checkValue);

    /// <summary>Only the identity hash, never the key (a logged KEK must not leak).</summary>
    public override string ToString() => $"Kek({Convert.ToHexStringLower(Id)})";
}
