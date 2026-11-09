using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Paduraksa.Application.Identity;

/// <summary>
/// Password hashing: PBKDF2-HMAC-SHA-512 through <see cref="Rfc2898DeriveBytes.Pbkdf2(byte[], byte[], int, HashAlgorithmName, int)"/>,
/// a random 128-bit salt per password, a 512-bit result, compared in constant time.
/// </summary>
/// <remarks>
/// <para>The stored form describes itself: <c>$pbkdf2-sha512$v1$i=&lt;iterations&gt;$&lt;salt&gt;$&lt;hash&gt;</c> (salt and hash in
/// unpadded standard base64). The algorithm and version let a later format sit beside this one; the iteration count lets the
/// cost rise without invalidating anything: <see cref="Verify"/> reports <see cref="PasswordCheck.NeedsRehash"/> when a hash
/// was made with a lower iteration count, and the login service stores a fresh one. (A new algorithm or layout would be a new
/// version tag in the stored form, and a hash with a tag this class does not know never matches.)</para>
/// <para><b>Unknown users cost the same.</b> <see cref="VerifyUnknown"/> does a full derivation against a throwaway hash, so
/// the time to reject a username that does not exist does not tell it apart from a wrong password.</para>
/// </remarks>
internal sealed class PasswordHasher
{
    public const string Algorithm = "pbkdf2-sha512";

    public const string Version = "v1";

    private const int SaltBytes = 16;

    private const int HashBytes = 64;

    private const int MinStoredIterations = 1_000;

    private const int MaxStoredIterations = 20_000_000;

    private readonly int _iterations;

    private readonly string _unknownUserHash;

    public PasswordHasher(int iterations)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, MinStoredIterations);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(iterations, MaxStoredIterations);
        _iterations = iterations;
        _unknownUserHash = Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
    }

    /// <summary>A new hash of <paramref name="password"/> at the current cost.</summary>
    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(password, salt, _iterations);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"${Algorithm}${Version}$i={_iterations}${Convert.ToBase64String(salt).TrimEnd('=')}${Convert.ToBase64String(hash).TrimEnd('=')}");
    }

    /// <summary>
    /// Checks <paramref name="password"/> against a stored hash. A value that is not a hash this class understands never
    /// matches (and still costs a derivation).
    /// </summary>
    public PasswordCheck Verify(string password, string stored)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (!TryParse(stored, out var iterations, out var salt, out var expected))
        {
            VerifyUnknown(password);
            return new PasswordCheck(false, false);
        }

        var actual = Derive(password, salt, iterations);
        var matches = CryptographicOperations.FixedTimeEquals(actual, expected);
        return new PasswordCheck(matches, matches && iterations < _iterations);
    }

    /// <summary>Spends the time of a verification and matches nothing.</summary>
    public void VerifyUnknown(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        Verify(password, _unknownUserHash);
    }

    private static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA512, HashBytes);

    private static bool TryParse(string stored, out int iterations, out byte[] salt, out byte[] hash)
    {
        iterations = 0;
        salt = [];
        hash = [];
        // "", "pbkdf2-sha512", "v1", "i=210000", salt, hash
        var parts = stored.Split('$');
        if (parts is not ["", Algorithm, Version, { } cost, { } saltText, { } hashText]
            || !cost.StartsWith("i=", StringComparison.Ordinal)
            || !int.TryParse(cost.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out iterations)
            || iterations is < MinStoredIterations or > MaxStoredIterations)
        {
            return false;
        }

        try
        {
            salt = Convert.FromBase64String(Pad(saltText));
            hash = Convert.FromBase64String(Pad(hashText));
        }
        catch (FormatException)
        {
            return false;
        }

        return salt.Length == SaltBytes && hash.Length == HashBytes;
    }

    private static string Pad(string base64) => base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');
}

/// <summary>The outcome of <see cref="PasswordHasher.Verify"/>.</summary>
internal readonly record struct PasswordCheck(bool Matches, bool NeedsRehash);
