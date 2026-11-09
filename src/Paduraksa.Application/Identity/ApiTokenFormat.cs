using System.Text;

using Paduraksa.Core.Ids;
using Paduraksa.Core.Model;

namespace Paduraksa.Application.Identity;

/// <summary>
/// The spelling of an API token: <c>pdk_&lt;token ID base62&gt;_&lt;32 random bytes base62&gt;_&lt;CRC-32 base62&gt;</c>.
/// The fixed prefix and the checksum let secret scanners find a leaked token and let the server reject a mistyped one without
/// touching the database; the checksum is not a security measure. The secret is two 128-bit halves, each in the 22-character
/// base62 form of the typed IDs (44 characters), and the checksum covers everything before it, in six characters.
/// </summary>
internal static class ApiTokenFormat
{
    public const string Prefix = "pdk_";

    public const int SecretBytes = 32;

    private const int ChecksumWidth = 6;

    /// <summary>The token string for <paramref name="id"/> and <paramref name="secret"/> (32 bytes).</summary>
    public static string Format(ApiTokenId id, ReadOnlySpan<byte> secret)
    {
        if (secret.Length != SecretBytes)
        {
            throw new ArgumentException($"A token secret is {SecretBytes} bytes.", nameof(secret));
        }

        var body = $"{Prefix}{Base62.Encode(id.Value)}_{Base62.Encode(ReadHalf(secret[..16]))}{Base62.Encode(ReadHalf(secret[16..]))}";
        return $"{body}_{Checksum(body)}";
    }

    /// <summary>Splits a presented token. False for anything that is not exactly the format, including a wrong checksum.</summary>
    public static bool TryParse(string? token, out ApiTokenId id, out byte[] secret)
    {
        id = default;
        secret = [];

        // pdk_ + 22 + _ + 44 + _ + 6
        const int Length = 4 + 22 + 1 + 44 + 1 + ChecksumWidth;
        if (token is not { Length: Length } || !token.StartsWith(Prefix, StringComparison.Ordinal) || token[26] != '_' || token[71] != '_')
        {
            return false;
        }

        var body = token[..71];
        if (!string.Equals(Checksum(body), token[72..], StringComparison.Ordinal)
            || !Base62.TryDecodeGuid(token.AsSpan(4, 22), out var guid)
            || !Base62.TryDecodeUInt128(token.AsSpan(27, 22), out var first)
            || !Base62.TryDecodeUInt128(token.AsSpan(49, 22), out var second))
        {
            return false;
        }

        id = ApiTokenId.FromValue(guid);
        if (id.IsEmpty)
        {
            return false;
        }

        secret = new byte[SecretBytes];
        WriteHalf(first, secret.AsSpan(0, 16));
        WriteHalf(second, secret.AsSpan(16, 16));
        return true;
    }

    private static string Checksum(string body)
    {
        var crc = Crc32.Compute(Encoding.ASCII.GetBytes(body));
        // 62^6 > 2^32, so the 11-character form of a 32-bit value always starts with five zeros.
        return Base62.Encode((ulong)crc)[^ChecksumWidth..];
    }

    private static UInt128 ReadHalf(ReadOnlySpan<byte> bytes)
    {
        UInt128 value = 0;
        foreach (var b in bytes)
        {
            value = (value << 8) | b;
        }

        return value;
    }

    private static void WriteHalf(UInt128 value, Span<byte> destination)
    {
        for (var i = destination.Length - 1; i >= 0; i--)
        {
            destination[i] = (byte)(value & 0xFF);
            value >>= 8;
        }
    }
}

/// <summary>CRC-32 (IEEE 802.3, as in zip and gzip), for the token checksum.</summary>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
