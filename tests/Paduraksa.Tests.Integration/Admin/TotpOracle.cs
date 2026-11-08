#pragma warning disable RS0030, CA5350 // The oracle below is the RFC 6238 algorithm, written out to check the vault's TOTP from the outside; it holds only the secret the test was just handed.
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;

namespace Paduraksa.Tests.Integration.Admin;

/// <summary>RFC 6238 (SHA-1, 6 digits, 30 s) computed from the <c>otpauth://</c> URI the enrolment returned.</summary>
internal static class TotpOracle
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>The code at <paramref name="stepOffset"/> steps from now.</summary>
    public static string CodeFor(string provisioningUri, int stepOffset = 0)
    {
        string secret = Uri.UnescapeDataString(provisioningUri.Split("secret=")[1].Split('&')[0]);
        long step = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30) + stepOffset;
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        byte[] hash = HMACSHA1.HashData(Decode(secret), counter);
        int offset = hash[^1] & 0x0F;
        int binary = BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan(offset, 4)) & 0x7FFFFFFF;
        return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    private static byte[] Decode(string text)
    {
        List<byte> bytes = [];
        int buffer = 0;
        int bits = 0;
        foreach (char c in text.TrimEnd('=').ToUpperInvariant())
        {
            buffer = (buffer << 5) | Alphabet.IndexOf(c, StringComparison.Ordinal);
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return [.. bytes];
    }
}
