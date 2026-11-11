using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

using Paduraksa.Core.Model;

namespace Paduraksa.Vault.Passwords;

/// <summary>
/// A password verifier in a PHC-style string, <c>$pbkdf2-sha256$i=210000$salt$hash</c> (unpadded standard base64), so the
/// cost is written next to the hash and a later build can raise <see cref="DefaultIterations"/> without invalidating
/// what is stored. Only the verifier is ever kept; a password is hashed and then forgotten. Internal to the vault: no other
/// assembly ever holds a verifier.
/// </summary>
internal static class PasswordHash
{
    /// <summary>What <see cref="Create(string, int)"/> uses: a few tens of milliseconds a check on a current CPU, and the
    /// verification cache is what keeps that off the request path.</summary>
    public const int DefaultIterations = 210_000;

    /// <summary>A stored hash below this is refused: it would be cheap to brute-force.</summary>
    public const int MinIterations = 100_000;

    /// <summary>A stored hash above this is refused: one check would stall a gate thread for seconds.</summary>
    public const int MaxIterations = 10_000_000;

    public const int SaltLength = 16;

    public const int HashLength = 32;

    private const string Prefix = "$pbkdf2-sha256$i=";
    private const int MinSaltLength = 8;
    private const int MaxSaltLength = 64;
    private const int MinHashLength = 16;
    private const int MaxHashLength = 64;

    /// <summary>Hashes <paramref name="password"/> with a fresh random salt.</summary>
    public static string Create(string password, int iterations = DefaultIterations)
    {
        ArgumentNullException.ThrowIfNull(password);
        var bytes = Encoding.UTF8.GetBytes(password);
        try
        {
            return Create(bytes, iterations);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary><see cref="Create(string, int)"/> over the password's UTF-8 bytes.</summary>
    public static string Create(ReadOnlySpan<byte> utf8Password, int iterations = DefaultIterations)
    {
        if (utf8Password.Length is 0 or > AccessListLimits.MaxPasswordBytes)
        {
            throw new ArgumentException($"A password is 1 to {AccessListLimits.MaxPasswordBytes} bytes in UTF-8.", nameof(utf8Password));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, MinIterations);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(iterations, MaxIterations);
        Span<byte> salt = stackalloc byte[SaltLength];
        RandomNumberGenerator.Fill(salt);
        Span<byte> hash = stackalloc byte[HashLength];
        Rfc2898DeriveBytes.Pbkdf2(utf8Password, salt, hash, iterations, HashAlgorithmName.SHA256);
        return Format(iterations, salt, hash);
    }

    /// <summary>Reads a stored hash; false for anything this build wouldn't have written, a cost outside the accepted
    /// range and a non-canonical encoding included, so the string round-trips byte for byte.</summary>
    public static bool TryParse([NotNullWhen(true)] string? stored, [NotNullWhen(true)] out ParsedPasswordHash? parsed)
    {
        parsed = null;
        if (stored is null || !stored.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = stored.AsSpan(Prefix.Length);
        var cost = rest.IndexOf('$');
        if (cost <= 0 || cost > 8 || !int.TryParse(rest[..cost], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var iterations)
            || iterations is < MinIterations or > MaxIterations)
        {
            return false;
        }

        rest = rest[(cost + 1)..];
        var split = rest.IndexOf('$');
        if (split < 0 || rest[(split + 1)..].Contains('$'))
        {
            return false;
        }

        var salt = Decode(rest[..split], MinSaltLength, MaxSaltLength);
        var hash = Decode(rest[(split + 1)..], MinHashLength, MaxHashLength);
        if (salt is null || hash is null)
        {
            return false;
        }

        parsed = new ParsedPasswordHash(iterations, salt, hash);
        return true;
    }

    /// <summary>Whether <paramref name="stored"/> is a hash this build reads.</summary>
    public static bool IsValid([NotNullWhen(true)] string? stored) => TryParse(stored, out _);

    /// <summary>Whether <paramref name="utf8Password"/> is the password <paramref name="hash"/> was made from. PBKDF2 over
    /// the stored cost, then a fixed-time comparison. </summary>
    public static bool Verify(ReadOnlySpan<byte> utf8Password, ParsedPasswordHash hash)
    {
        ArgumentNullException.ThrowIfNull(hash);
        Span<byte> derived = stackalloc byte[MaxHashLength];
        derived = derived[..hash.Hash.Length];
        Rfc2898DeriveBytes.Pbkdf2(utf8Password, hash.Salt, derived, hash.Iterations, HashAlgorithmName.SHA256);
        return CryptographicOperations.FixedTimeEquals(derived, hash.Hash);
    }

    private static string Format(int iterations, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> hash) =>
        string.Concat(Prefix, iterations.ToString(System.Globalization.CultureInfo.InvariantCulture), "$", Encode(salt), "$", Encode(hash));

    private static string Encode(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=');

    // Null unless the text is canonical unpadded base64 of an in-range length: a second spelling of one hash would be a
    // second identity for the same credential.
    private static byte[]? Decode(ReadOnlySpan<char> text, int minLength, int maxLength)
    {
        if (text.Length == 0 || text.Length > 128 || text.Contains('='))
        {
            return null;
        }

        Span<char> padded = stackalloc char[text.Length + 3];
        text.CopyTo(padded);
        var length = text.Length;
        while (length % 4 != 0)
        {
            padded[length++] = '=';
        }

        Span<byte> buffer = stackalloc byte[96];
        if (!Convert.TryFromBase64Chars(padded[..length], buffer, out var written) || written < minLength || written > maxLength)
        {
            return null;
        }

        var bytes = buffer[..written].ToArray();
        return Encode(bytes).AsSpan().SequenceEqual(text) ? bytes : null;
    }
}

/// <summary>A stored hash taken apart once, so a check doesn't parse it again.</summary>
internal sealed class ParsedPasswordHash
{
    internal ParsedPasswordHash(int iterations, byte[] salt, byte[] hash)
    {
        Iterations = iterations;
        Salt = salt;
        Hash = hash;
        Stamp = SHA256.HashData([.. salt, .. hash])[..16];
    }

    public int Iterations { get; }

    public byte[] Salt { get; }

    public byte[] Hash { get; }

    /// <summary>16 bytes that change with the salt or the hash, for the cache's fingerprint: the same password hashed again has
    /// another salt, so every cached entry of the old one misses.</summary>
    public byte[] Stamp { get; }
}
