using System.Buffers.Binary;
using Paduraksa.Core.Vault;
using Paduraksa.Vault.Backup;

namespace Paduraksa.Tests.Vault.Backup;

public sealed class BackupHeaderTests
{
    private static byte[] Valid() => BackupHeader.Create(65536, 3, 4, new byte[BackupHeader.SaltLength]).ToBytes();

    private static BackupFailure FailureOf(byte[] bytes) => Assert.Throws<BackupException>(() => BackupHeader.Parse(bytes)).Reason;

    [Fact]
    public void AHeaderRoundTrips()
    {
        var header = BackupHeader.Create(65536, 3, 4, [.. Enumerable.Range(0, 16).Select(i => (byte)i)]);

        var parsed = BackupHeader.Parse(header.ToBytes());

        Assert.Equal((1, 65536, 3, 4), (parsed.Format, parsed.MemoryKiB, parsed.Iterations, parsed.Parallelism));
        Assert.Equal(header.Salt, parsed.Salt);
        Assert.Equal(new BackupKdf("argon2id", 19, 65536, 3, 4), parsed.Kdf);
    }

    [Fact]
    public void AFileThatIsNotABackupIsSaidSo()
    {
        Assert.Equal(BackupFailure.NotABackup, FailureOf("SQLite format 3\0 and then some more bytes to be long enough....."u8.ToArray()));
        Assert.Equal(BackupFailure.NotABackup, FailureOf(Valid()[..20]));
        var wrongMagic = Valid();
        wrongMagic[0] = (byte)'X';
        Assert.Equal(BackupFailure.NotABackup, FailureOf(wrongMagic));
    }

    [Fact]
    public void ANewerFormatIsSaidToBeNewerAndNeverGuessedAt()
    {
        var bytes = Valid();
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(8), 2);

        var ex = Assert.Throws<BackupException>(() => BackupHeader.Parse(bytes));

        Assert.Equal(BackupFailure.UnsupportedFormat, ex.Reason);
        Assert.Contains("newer", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(10, 2)]
    [InlineData(11, 2)]
    [InlineData(21, 1)]
    [InlineData(47, 1)]
    public void UnknownAlgorithmsAndNonZeroReservedBytesAreRefused(int offset, byte value)
    {
        var bytes = Valid();
        bytes[offset] = value;

        Assert.Equal(BackupFailure.UnsupportedFormat, FailureOf(bytes));
    }

    [Theory]
    [InlineData(0u, 3, 4)]
    [InlineData(65536u, 0, 4)]
    [InlineData(65536u, 17, 4)]
    [InlineData(65536u, 3, 0)]
    [InlineData(65536u, 3, 17)]
    [InlineData(31u, 3, 4)]
    [InlineData(1048577u, 3, 4)]
    public void CostsOutsideTheSaneRangeAreRefusedSoAHeaderCantMakeARestoreSpendAGigabyte(uint memory, int iterations, int parallelism)
    {
        var bytes = Valid();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12), memory);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), (uint)iterations);
        bytes[20] = (byte)parallelism;

        Assert.Equal(BackupFailure.UnsupportedFormat, FailureOf(bytes));
    }
}
