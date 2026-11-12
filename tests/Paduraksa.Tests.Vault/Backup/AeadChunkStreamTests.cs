using System.Security.Cryptography;
using Paduraksa.Core.Vault;
using Paduraksa.Vault.Backup;

namespace Paduraksa.Tests.Vault.Backup;

public sealed class AeadChunkStreamTests
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    private static readonly BackupHeader Header = BackupHeader.Create(64, 1, 1, RandomNumberGenerator.GetBytes(BackupHeader.SaltLength));

    private static byte[] Seal(byte[] plaintext, int writeSize = 7919)
    {
        using MemoryStream destination = new();
        using (var encrypt = new AeadEncryptStream(destination, Key, Header))
        {
            for (var offset = 0; offset < plaintext.Length; offset += writeSize)
            {
                encrypt.Write(plaintext.AsSpan(offset, Math.Min(writeSize, plaintext.Length - offset)));
            }

            encrypt.Complete();
        }

        return destination.ToArray();
    }

    private static byte[] Open(byte[] sealedBytes, byte[]? key = null)
    {
        using MemoryStream source = new(sealedBytes, BackupHeader.Length, sealedBytes.Length - BackupHeader.Length);
        using var decrypt = new AeadDecryptStream(source, key ?? Key, Header.ToBytes());
        using MemoryStream plain = new();
        decrypt.CopyTo(plain);
        return plain.ToArray();
    }

    private static BackupFailure FailureOf(Func<byte[]> open) => Assert.Throws<BackupException>(open).Reason;

    public static TheoryData<int> Sizes => [0, 1, 15, 16, BackupHeader.ChunkSize - 1, BackupHeader.ChunkSize, BackupHeader.ChunkSize + 1, (2 * BackupHeader.ChunkSize), (3 * BackupHeader.ChunkSize) + 5];

    [Theory]
    [MemberData(nameof(Sizes))]
    public void EverySizeRoundTripsIncludingTheExactMultiplesOfTheChunk(int size)
    {
        var plaintext = RandomNumberGenerator.GetBytes(size);

        var sealedBytes = Seal(plaintext);

        Assert.Equal(plaintext, Open(sealedBytes));
        // The header is first and in the clear; the rest is chunks of at most 64 KiB and a tag.
        Assert.Equal(Header.ToBytes(), sealedBytes[..BackupHeader.Length]);
    }

    [Fact]
    public void AWrongKeyOnTheFirstChunkIsAWrongPassphrase() =>
        Assert.Equal(BackupFailure.WrongPassphrase, FailureOf(() => Open(Seal(RandomNumberGenerator.GetBytes(1000)), RandomNumberGenerator.GetBytes(32))));

    [Theory]
    [InlineData(BackupHeader.Length)]
    [InlineData(BackupHeader.Length + 500)]
    public void AChangedByteInTheFirstChunkIsAWrongPassphraseOrDamage(int position)
    {
        var sealedBytes = Seal(RandomNumberGenerator.GetBytes(1000));
        sealedBytes[position] ^= 1;

        Assert.Equal(BackupFailure.WrongPassphrase, FailureOf(() => Open(sealedBytes)));
    }

    [Fact]
    public void AChangedByteInALaterChunkIsDamage()
    {
        var sealedBytes = Seal(RandomNumberGenerator.GetBytes((2 * BackupHeader.ChunkSize) + 100));
        sealedBytes[BackupHeader.Length + BackupHeader.ChunkSize + AeadChunk.TagLength + 10] ^= 1;

        Assert.Equal(BackupFailure.Damaged, FailureOf(() => Open(sealedBytes)));
    }

    [Fact]
    public void ACutInsideAChunkFailsItsTag()
    {
        var sealedBytes = Seal(RandomNumberGenerator.GetBytes(BackupHeader.ChunkSize + 1000));

        Assert.Equal(BackupFailure.Damaged, FailureOf(() => Open(sealedBytes[..^5])));
    }

    [Fact]
    public void ACutAtAChunkBoundaryLeavesNoLastChunkAndIsRefused()
    {
        var sealedBytes = Seal(RandomNumberGenerator.GetBytes((2 * BackupHeader.ChunkSize) + 100));
        var oneChunk = BackupHeader.Length + BackupHeader.ChunkSize + AeadChunk.TagLength;

        Assert.Equal(BackupFailure.Damaged, FailureOf(() => Open(sealedBytes[..(oneChunk + BackupHeader.ChunkSize + AeadChunk.TagLength)])));
        Assert.Equal(BackupFailure.Damaged, FailureOf(() => Open(sealedBytes[..oneChunk])));
    }

    [Fact]
    public void BytesAppendedAfterTheLastChunkAreRefused()
    {
        var sealedBytes = Seal(RandomNumberGenerator.GetBytes((2 * BackupHeader.ChunkSize) + 100));

        // The last chunk is read as longer than it was sealed, so its tag no longer matches.
        Assert.Equal(BackupFailure.Damaged, FailureOf(() => Open([.. sealedBytes, .. RandomNumberGenerator.GetBytes(40)])));
    }

    [Fact]
    public void SwappedChunksFailTheirCounters()
    {
        var plaintext = RandomNumberGenerator.GetBytes((3 * BackupHeader.ChunkSize) + 10);
        var sealedBytes = Seal(plaintext);
        var size = BackupHeader.ChunkSize + AeadChunk.TagLength;
        var first = sealedBytes.AsSpan(BackupHeader.Length, size).ToArray();
        var second = sealedBytes.AsSpan(BackupHeader.Length + size, size).ToArray();
        second.CopyTo(sealedBytes.AsSpan(BackupHeader.Length));
        first.CopyTo(sealedBytes.AsSpan(BackupHeader.Length + size));

        Assert.Equal(BackupFailure.WrongPassphrase, FailureOf(() => Open(sealedBytes)));
    }

    [Fact]
    public void ADroppedMiddleChunkIsDamage()
    {
        var sealedBytes = Seal(RandomNumberGenerator.GetBytes((3 * BackupHeader.ChunkSize) + 10));
        var size = BackupHeader.ChunkSize + AeadChunk.TagLength;
        byte[] without = [.. sealedBytes[..(BackupHeader.Length + size)], .. sealedBytes[(BackupHeader.Length + (2 * size))..]];

        Assert.Equal(BackupFailure.Damaged, FailureOf(() => Open(without)));
    }

    [Fact]
    public void ATamperedHeaderFailsEveryChunkBecauseItIsAuthenticated()
    {
        var plaintext = RandomNumberGenerator.GetBytes(100);
        var sealedBytes = Seal(plaintext);

        // The reader is given a header with one costs byte changed: the same ciphertext no longer authenticates.
        var changed = BackupHeader.Create(65, 1, 1, Header.Salt).ToBytes();
        using MemoryStream source = new(sealedBytes, BackupHeader.Length, sealedBytes.Length - BackupHeader.Length);
        using var decrypt = new AeadDecryptStream(source, Key, changed);

        Assert.Equal(BackupFailure.WrongPassphrase, Assert.Throws<BackupException>(() => decrypt.CopyTo(Stream.Null)).Reason);
    }
}
