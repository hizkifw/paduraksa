using System.Globalization;
using Paduraksa.Core.Vault;
using Paduraksa.Data;

namespace Paduraksa.Server.Cli.Doctor;

/// <summary>Both databases open read-only and their migrations match this binary's.</summary>
internal sealed class DatabasesCheck : IDoctorCheck
{
    public string Name => "databases";

    public bool NeedsOptions => true;

    public async Task<IReadOnlyList<CheckResult>> RunAsync(DoctorContext context, CancellationToken cancellationToken)
    {
        var directory = context.Options!.DataDirectory;
        var config = await context.InspectConfigDatabase(Path.Combine(directory, ConfigDatabase.FileName), cancellationToken).ConfigureAwait(false);
        var vault = await context.InspectVaultAsync(cancellationToken).ConfigureAwait(false);
        return
        [
            Report("config-database", ConfigDatabase.FileName, config.Exists, config.Problem, config.Migrations.Applied, config.Migrations.Pending, config.Migrations.Unknown),
            Report("vault-database", DataDirectoryCheck.VaultFileName, vault.DatabaseExists, vault.DatabaseProblem, vault.AppliedMigrations, vault.PendingMigrations, vault.UnknownMigrations),
        ];
    }

    internal static CheckResult Report(string id, string file, bool exists, string? problem, IReadOnlyList<string> applied, IReadOnlyList<string> pending, IReadOnlyList<string> unknown)
    {
        if (!exists)
        {
            return CheckResult.Info(id, $"{file} does not exist yet: the first start creates it.");
        }

        if (problem is not null)
        {
            return CheckResult.Failure(id, $"{file} could not be opened read-only: {problem}", "Check the file's owner and mode, and that the volume is mounted. If it is damaged, restore a backup (paduraksa restore).");
        }

        if (unknown.Count > 0)
        {
            return CheckResult.Failure(
                id,
                $"{file} was written by a newer Paduraksa than this one (it has {Plural(unknown.Count)} this binary does not know: {string.Join(", ", unknown)}). This looks like a downgrade, and an older build can misread or damage a newer schema.",
                "Run the newer version again, or restore a backup made before the upgrade (paduraksa restore).");
        }

        if (pending.Count > 0)
        {
            return CheckResult.Info(id, $"{file} needs {Plural(pending.Count)} ({string.Join(", ", pending)}): the next start applies them, as after any upgrade.");
        }

        return CheckResult.Ok(id, $"{file} opens read-only and its schema is current ({applied.Count.ToString(CultureInfo.InvariantCulture)} migrations).");
    }

    private static string Plural(int count) => count == 1 ? "1 migration" : $"{count.ToString(CultureInfo.InvariantCulture)} migrations";
}
