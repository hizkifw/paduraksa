using System.Security.Cryptography;

namespace Paduraksa.TypedIds;

/// <summary>RFC 9562 UUIDv4 from <see cref="RandomNumberGenerator"/> (122 random bits).</summary>
/// <remarks>
/// Generated <c>New()</c> for <see cref="IdKind.UuidV4"/> IDs uses this rather than <c>Guid.NewGuid()</c>, so the random source is
/// explicitly a CSPRNG and a project that bans bare GUIDs has nothing to suppress.
/// </remarks>
public static class UuidV4Generator
{
    /// <summary>Returns a new random UUIDv4.</summary>
    public static Guid Next()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        bytes[6] = (byte)(0x40 | (bytes[6] & 0x0F)); // version 4
        bytes[8] = (byte)(0x80 | (bytes[8] & 0x3F)); // variant 10xx
        return new Guid(bytes, bigEndian: true);
    }
}
