using System.Text;

namespace Paduraksa.Vault.Totp;

/// <summary>RFC 4648 base32 without padding: the spelling of the shared secret in an <c>otpauth://</c> URI.</summary>
internal static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var result = new StringBuilder((data.Length * 8 / 5) + 1);
        var buffer = 0;
        var bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                result.Append(Alphabet[(buffer >> (bits - 5)) & 0x1F]);
                bits -= 5;
            }

            buffer &= (1 << bits) - 1;
        }

        if (bits > 0)
        {
            result.Append(Alphabet[(buffer << (5 - bits)) & 0x1F]);
        }

        return result.ToString();
    }
}
