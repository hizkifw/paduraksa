namespace Paduraksa.Core.Vault;

/// <summary>Whether the master key opens the vault database, as far as a read can tell.</summary>
public enum VaultKeyCheck
{
    /// <summary>Not compared: there is no key to try, or no database to compare with, or the database is not one this build can read.</summary>
    NotChecked = 0,

    /// <summary>The key is the one the database was written under.</summary>
    Matches = 1,

    /// <summary>The key is not the one the database was written under: the vault refuses to start.</summary>
    DoesNotMatch = 2,
}

/// <summary>
/// What a read-only look at the vault found. Nothing in it is secret: where the master key comes from, never the key.
/// </summary>
/// <param name="KekSource">Where the master key comes from, or null when none is set and none exists yet (a first start generates one).</param>
/// <param name="KekFile">The file the master key is read from (a variable-named file or the data directory's generated one); null for the environment variable or when there is none.</param>
/// <param name="KekBesideDatabase">The key file sits in the data directory, so a copy of the data is a copy of the key (see <see cref="VaultStatus.KekBesideDatabase"/>).</param>
/// <param name="KekProblem">Why the master key can't be used (missing next to a database that holds data, unreadable, malformed, a mode the vault refuses), the vault's own sentence; null when it is usable or not yet generated.</param>
/// <param name="DatabaseExists">Whether <c>vault.db</c> is there.</param>
/// <param name="AppliedMigrations">The migrations the database lists, oldest first.</param>
/// <param name="PendingMigrations">What this build has that the database lacks: applied at the next start.</param>
/// <param name="UnknownMigrations">What the database lists that this build lacks: a newer build wrote it.</param>
/// <param name="DatabaseProblem">Why the database could not be opened or read, or null.</param>
/// <param name="KeyCheck">Whether the master key opens the database.</param>
public sealed record VaultInspection(
    KekSourceKind? KekSource,
    string? KekFile,
    bool KekBesideDatabase,
    string? KekProblem,
    bool DatabaseExists,
    IReadOnlyList<string> AppliedMigrations,
    IReadOnlyList<string> PendingMigrations,
    IReadOnlyList<string> UnknownMigrations,
    string? DatabaseProblem,
    VaultKeyCheck KeyCheck);

/// <summary>
/// A look at the vault that changes nothing (<c>paduraksa doctor</c>): unlike <see cref="IVaultLifecycle.InitializeAsync"/> it never generates a key,
/// creates or migrates a database or stores a check value, so it is safe beside a running server.
/// </summary>
public interface IVaultInspection
{
    /// <summary>Finds the master key's source, reads the vault database's migrations and, when both are there, compares the key with the database.</summary>
    ValueTask<VaultInspection> InspectAsync(CancellationToken cancellationToken);
}
