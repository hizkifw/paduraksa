using Microsoft.EntityFrameworkCore;
using Paduraksa.Vault.Backup;
using Paduraksa.Vault.Persistence;

namespace Paduraksa.Tests.Vault.Backup;

/// <summary>
/// A table that holds a sealed value must be re-wrapped by <c>KekRotation.RewrapAllAsync</c>, which a KEK rotation, a backup and a restore
/// all go through: a table it forgets would keep values under a key nobody holds any more. This fails when a table with a sealed column
/// is added, so adding one means adding it there.
/// </summary>
public sealed class RewrapCoverageTests
{
    [Fact]
    public void EveryTableWithASealedColumnIsOneTheRewrapKnows()
    {
        using VaultDbContext context = new(VaultBackup.ScratchOptions(Path.Combine(Path.GetTempPath(), "never-opened.db")));

        var sealedTables = context.Model.GetEntityTypes()
            .Where(entity => entity.GetProperties().Any(p => p.ClrType == typeof(byte[]) && p.Name.Contains("Sealed", StringComparison.Ordinal)))
            .Select(entity => entity.ClrType.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            ["AccountKeyRow", "CertificateKeyRow", "DataProtectionKeyRow", "PasswordCredentialRow", "SecretRow", "StagedKeyRow"],
            sealedTables);
    }
}
