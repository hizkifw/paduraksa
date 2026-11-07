namespace Paduraksa.Core.Vault;

/// <summary>Where the vault's key-encryption key (KEK, PLAN §8.3) came from. Listed in priority order.</summary>
public enum KekSourceKind
{
    /// <summary>The file named by <c>PADURAKSA_MASTER_KEY_FILE</c> (Docker secrets, systemd <c>LoadCredential</c>).</summary>
    File = 0,

    /// <summary>The <c>PADURAKSA_MASTER_KEY</c> environment variable.</summary>
    Environment = 1,

    /// <summary><c>master.key</c> in the data directory, generated on first start.</summary>
    GeneratedFile = 2,
}

/// <summary>What the vault tells about its own state: nothing secret.</summary>
/// <param name="KekSource">Where the KEK came from.</param>
/// <param name="KekBesideDatabase">
/// The KEK file sits in the same directory as <c>vault.db</c> (or below it), so a copy of the data directory is a copy of
/// both. The dashboard warns about it (PLAN §8.3). Always true for <see cref="KekSourceKind.GeneratedFile"/>, never for
/// <see cref="KekSourceKind.Environment"/>.
/// </param>
public sealed record VaultStatus(KekSourceKind KekSource, bool KekBesideDatabase);

/// <summary>
/// Brings the vault up at startup, so a problem with it stops the process then and not at the first secret that is used.
/// </summary>
/// <remarks>
/// <see cref="InitializeAsync"/> resolves the KEK, migrates <c>vault.db</c> and checks the KEK against the database's check
/// value: a wrong KEK throws <see cref="InvalidOperationException"/> naming the problem (never the key). It is idempotent, and
/// every capability waits for it, so calling it once from the composition root is enough, early.
/// </remarks>
public interface IVaultLifecycle
{
    /// <summary>Resolves the KEK, migrates the database and verifies the KEK. Safe to call more than once.</summary>
    /// <exception cref="InvalidOperationException">The KEK is missing, malformed, unsafe to use or does not belong to this database.</exception>
    ValueTask<VaultStatus> InitializeAsync(CancellationToken cancellationToken);
}
