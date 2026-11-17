namespace Paduraksa.Tests.Architecture;

/// <summary>
/// The passphrase of a backup and the key it derives never leave the vault (docs/design/vault-and-certificates.md "Backup and restore"): the Argon2id library is
/// referenced by <c>Paduraksa.Vault</c> alone, so no other project can derive a key from a passphrase on its own.
/// </summary>
public sealed class BackupBoundaryTests
{
    private const string Argon2Package = "Konscious.Security.Cryptography";

    [Fact]
    public void OnlyTheVaultReferencesTheArgon2Library()
    {
        var referencing = Directory.EnumerateFiles(Repository.Root, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains(Argon2Package, StringComparison.Ordinal))
            .Select(Path.GetFileNameWithoutExtension)
            .ToList();

        Assert.Equal(["Paduraksa.Vault"], referencing);
    }
}
