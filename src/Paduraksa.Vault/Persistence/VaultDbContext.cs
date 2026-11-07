using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Paduraksa.Core.Vault;
using Paduraksa.Storage;
using Paduraksa.Storage.Ids;
using Paduraksa.Vault.Secrets;

namespace Paduraksa.Vault.Persistence;

/// <summary>
/// The vault database (<c>data/vault.db</c>, PLAN §5, §8): sealed secrets and sealed keys, and the KEK check value. It is
/// a database of its own, separate from the config database, and nothing outside the Vault assembly can see this type or
/// its rows. Every secret-bearing column holds the output of <see cref="Crypto.EnvelopeCipher.Seal"/>, never a plaintext.
/// The schema comes from migrations (applied by <see cref="VaultDatabase.InitializeAsync"/>).
/// </summary>
/// <remarks>
/// Adding something that stores a key: an entity with its own typed ID and a <c>Sealed</c> column next to its
/// metadata (never a generic "owner string" table: IDs stay typed), a <see cref="DbSet{TEntity}"/> here, a configuration
/// applied in <see cref="OnModelCreating"/>, then a migration (README, "Database migrations").
/// </remarks>
internal sealed class VaultDbContext(DbContextOptions<VaultDbContext> options) : DbContext(options)
{
    internal DbSet<VaultMetaRow> Meta => Set<VaultMetaRow>();

    internal DbSet<SecretRow> Secrets => Set<SecretRow>();

    internal DbSet<AccountKeyRow> AccountKeys => Set<AccountKeyRow>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.AddTypedIdConventions();
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcTimestampConverter>();
        configurationBuilder.Properties<SecretKind>().HaveConversion<SecretKindConverter>();
        configurationBuilder.Properties<MacAlgorithm>().HaveConversion<MacAlgorithmConverter>();
        configurationBuilder.Properties<JwsAlgorithm>().HaveConversion<JwsAlgorithmConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new VaultMetaRowConfiguration());
        modelBuilder.ApplyConfiguration(new SecretRowConfiguration());
        modelBuilder.ApplyConfiguration(new AccountKeyRowConfiguration());
    }
}

// The vault's enums are stored by their stable names (VaultNames, JwsAlgorithmNames), never by number.

internal sealed class SecretKindConverter()
    : ValueConverter<SecretKind, string>(value => value.ToName(), name => VaultNames.ParseSecretKind(name));

internal sealed class MacAlgorithmConverter()
    : ValueConverter<MacAlgorithm, string>(value => value.ToName(), name => VaultNames.ParseMacAlgorithm(name));

internal sealed class JwsAlgorithmConverter()
    : ValueConverter<JwsAlgorithm, string>(value => value.ToName(), name => JwsAlgorithmNames.Parse(name));

/// <summary>Check constraints that keep enum columns to their stable names, generated from the enums (a new name changes the constraint, so it comes with a migration).</summary>
internal static class NameChecks
{
    public static string SecretKind(string column) => In(column, Enum.GetValues<SecretKind>().Select(k => k.ToName()));

    public static string MacAlgorithm(string column) => In(column, Enum.GetValues<MacAlgorithm>().Select(a => a.ToName()));

    public static string JwsAlgorithm(string column) => In(column, Enum.GetValues<JwsAlgorithm>().Select(a => a.ToName()));

    /// <summary>The kinds that are MAC keys, as SQL, so that the algorithm column can be tied to them.</summary>
    public static string MacKinds(string column) => In(column, SecretKinds.MacKinds.Select(k => k.ToName()));

    private static string In(string column, IEnumerable<string> names) =>
        $"\"{column}\" IN ({string.Join(", ", names.Select(n => $"'{n}'"))})";
}
