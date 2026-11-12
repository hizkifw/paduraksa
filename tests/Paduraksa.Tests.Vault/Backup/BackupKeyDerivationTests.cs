using Paduraksa.Vault.Backup;

namespace Paduraksa.Tests.Vault.Backup;

public sealed class BackupKeyDerivationTests
{
    [Fact]
    public void MatchesTheArgon2idTestVectorOfRfc9106()
    {
        // RFC 9106 section 5.3: password 32 x 0x01, salt 16 x 0x02, secret 8 x 0x03, associated data 12 x 0x04,
        // 32 KiB, 3 passes, 4 lanes, version 0x13, a 32-byte tag.
        var password = new string('\u0001', 32);

        var key = BackupKeyDerivation.Derive(
            password, new byte[16].Select(_ => (byte)2).ToArray(), memoryKiB: 32, iterations: 3, parallelism: 4,
            associatedData: new byte[12].Select(_ => (byte)4).ToArray(), secret: new byte[8].Select(_ => (byte)3).ToArray());

        Assert.Equal("0d640df58d78766c08c037a34a8b53c9d01ef0452d75b65eb52520e96b01e659", Convert.ToHexStringLower(key));
    }

    [Fact]
    public void ASaltOrAPassphraseChangeGivesAnotherKey()
    {
        byte[] salt = [.. Enumerable.Range(0, 16).Select(i => (byte)i)];
        var first = BackupKeyDerivation.Derive("correct horse battery", salt, 64, 1, 1);

        Assert.NotEqual(first, BackupKeyDerivation.Derive("correct horse batterz", salt, 64, 1, 1));
        salt[0] ^= 1;
        Assert.NotEqual(first, BackupKeyDerivation.Derive("correct horse battery", salt, 64, 1, 1));
    }

    [Fact]
    public void TheDefaultCostIsTheOneThePlanRecords()
    {
        Assert.Equal(64 * 1024, BackupHeader.DefaultMemoryKiB);
        Assert.Equal(3, BackupHeader.DefaultIterations);
        Assert.Equal(4, BackupHeader.DefaultParallelism);
    }
}
