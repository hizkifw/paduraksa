using System.Security.Cryptography;

namespace Paduraksa.Core.Ids;

/// <summary>RFC 9562 UUIDv4 from <see cref="RandomNumberGenerator"/> (122 random bits).</summary>
/// <remarks>
/// <c>Guid.NewGuid()</c> is banned repo-wide so that bare GUIDs never get minted ad hoc. Generating v4
/// here keeps that ban free of suppressions (nothing to audit) and makes the CSPRNG source explicit.
/// </remarks>
public static class UuidV4Generator
{
    public static Guid Next()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        bytes[6] = (byte)(0x40 | (bytes[6] & 0x0F)); // version 4
        bytes[8] = (byte)(0x80 | (bytes[8] & 0x3F)); // variant 10xx
        return new Guid(bytes, bigEndian: true);
    }
}
