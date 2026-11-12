using System.Buffers.Binary;
using Paduraksa.Core.Vault;

namespace Paduraksa.Vault.Backup;

/// <summary>
/// The plain, fixed 48-byte start of an archive: what is needed before anything can be decrypted (the key derivation's
/// parameters and salt), and what every chunk authenticates as its associated data, so none of it can be changed unnoticed.
/// </summary>
/// <remarks>
/// <code>
/// offset  size  field
///      0     8  magic: "PDKBAK" 1A 0A (the 1A 0A catch a text-mode transfer, as PNG's does)
///      8     2  format version, big-endian (1)
///     10     1  key derivation (1 = Argon2id, version 0x13)
///     11     1  cipher (1 = AES-256-GCM in 64 KiB chunks, see AeadChunkStream)
///     12     4  Argon2 memory in KiB
///     16     4  Argon2 passes
///     20     1  Argon2 lanes
///     21     3  zero
///     24    16  Argon2 salt, random per archive
///     40     4  chunk size (65536)
///     44     4  zero
/// </code>
/// Every field a parser reads is checked: unknown versions and algorithms, non-zero reserved bytes and key-derivation costs
/// outside a sane range (a header can't make a restore spend a gigabyte of memory twice over) are refused.
/// </remarks>
internal sealed record BackupHeader(int Format, int MemoryKiB, int Iterations, int Parallelism, byte[] Salt)
{
    public const int Length = 48;

    public const int CurrentFormat = 1;

    public const int SaltLength = 16;

    public const int ChunkSize = 64 * 1024;

    public const int Argon2Version = 0x13;

    /// <summary>RFC 9106's second recommended option (64 MiB, three passes, four lanes): about half a second on a homelab box.</summary>
    public const int DefaultMemoryKiB = 64 * 1024;

    public const int DefaultIterations = 3;

    public const int DefaultParallelism = 4;

    public const int MaxMemoryKiB = 1024 * 1024;

    public const int MaxIterations = 16;

    public const int MaxParallelism = 16;

    private static readonly byte[] Magic = [(byte)'P', (byte)'D', (byte)'K', (byte)'B', (byte)'A', (byte)'K', 0x1A, 0x0A];

    public static BackupHeader Create(int memoryKiB, int iterations, int parallelism, byte[] salt) =>
        new(CurrentFormat, memoryKiB, iterations, parallelism, salt);

    public BackupKdf Kdf => new("argon2id", Argon2Version, MemoryKiB, Iterations, Parallelism);

    public byte[] ToBytes()
    {
        var bytes = new byte[Length];
        Magic.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(8), (ushort)Format);
        bytes[10] = 1;
        bytes[11] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12), (uint)MemoryKiB);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), (uint)Iterations);
        bytes[20] = (byte)Parallelism;
        Salt.CopyTo(bytes, 24);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(40), ChunkSize);
        return bytes;
    }

    /// <exception cref="BackupException"><see cref="BackupFailure.NotABackup"/> or <see cref="BackupFailure.UnsupportedFormat"/>.</exception>
    public static BackupHeader Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < Length || !bytes[..Magic.Length].SequenceEqual(Magic))
        {
            throw new BackupException(BackupFailure.NotABackup, "This file is not a Paduraksa backup.");
        }

        var format = BinaryPrimitives.ReadUInt16BigEndian(bytes[8..]);
        if (format != CurrentFormat)
        {
            throw new BackupException(
                BackupFailure.UnsupportedFormat,
                format > CurrentFormat
                    ? $"This backup is format {format}, newer than this Paduraksa reads ({CurrentFormat}). Upgrade Paduraksa to restore it."
                    : $"This backup is format {format}, which this Paduraksa does not read.");
        }

        var memory = BinaryPrimitives.ReadUInt32BigEndian(bytes[12..]);
        var iterations = BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]);
        int parallelism = bytes[20];
        if (bytes[10] != 1 || bytes[11] != 1 || !bytes[21..24].SequenceEqual(new byte[3]) || BinaryPrimitives.ReadUInt32BigEndian(bytes[40..]) != ChunkSize || BinaryPrimitives.ReadUInt32BigEndian(bytes[44..]) != 0)
        {
            throw new BackupException(BackupFailure.UnsupportedFormat, "This backup uses an algorithm this Paduraksa does not read.");
        }

        if (parallelism is < 1 or > MaxParallelism || iterations is < 1 or > MaxIterations || memory < 8u * (uint)parallelism || memory > MaxMemoryKiB)
        {
            throw new BackupException(BackupFailure.UnsupportedFormat, "This backup's key-derivation cost is outside what this Paduraksa accepts.");
        }

        return new BackupHeader(format, (int)memory, (int)iterations, parallelism, bytes.Slice(24, SaltLength).ToArray());
    }
}
