using Paduraksa.Core.Hosting;
using Paduraksa.Core.Vault;

namespace Paduraksa.Server.Cli.Doctor;

/// <summary>Where the master key comes from and whether it opens the vault. Never prints the key.</summary>
internal sealed class MasterKeyCheck : IDoctorCheck
{
    private const string KeyFileVariable = "PADURAKSA_MASTER_KEY_FILE";

    private const string KeyVariable = "PADURAKSA_MASTER_KEY";

    public string Name => "master key";

    public bool NeedsOptions => true;

    public async Task<IReadOnlyList<CheckResult>> RunAsync(DoctorContext context, CancellationToken cancellationToken)
    {
        var inspection = await context.InspectVaultAsync(cancellationToken).ConfigureAwait(false);
        List<CheckResult> results = [Source(inspection, context.Env)];
        if (inspection.KekBesideDatabase)
        {
            results.Add(CheckResult.Warning("master-key-location", MasterKeyAdvice.BesideTitle + ". " + MasterKeyAdvice.BesideDetail, "Move the key out of the data directory: " + MasterKeyAdvice.BesideGuide));
        }

        return results;
    }

    private static CheckResult Source(VaultInspection inspection, Func<string, string?> env)
    {
        if (inspection.KekProblem is { } problem)
        {
            return CheckResult.Failure("master-key", problem, "The server will not start until the key is usable (exit code 2). See the master key page of the docs: " + MasterKeyAdvice.BesideGuide);
        }

        if (inspection.KekSource is null)
        {
            return CheckResult.Info("master-key", "There is no master key yet: the first start generates master.key in the data directory.");
        }

        var where = Describe(inspection, env);
        if (inspection.KeyCheck == VaultKeyCheck.DoesNotMatch)
        {
            return CheckResult.Failure(
                "master-key",
                $"The master key ({where}) is not the one vault.db was written under: the server will not start.",
                $"Use the original key ({KeyFileVariable}, {KeyVariable} or master.key in the data directory), or restore the vault from a backup.");
        }

        return CheckResult.Ok("master-key", inspection.KeyCheck == VaultKeyCheck.Matches ? $"The master key comes from {where} and opens vault.db." : $"The master key comes from {where}.");
    }

    private static string Describe(VaultInspection inspection, Func<string, string?> env) => inspection.KekSource switch
    {
        KekSourceKind.Environment => $"the {KeyVariable} variable",
        KekSourceKind.GeneratedFile => $"{inspection.KekFile} (generated in the data directory)",
        _ => $"{inspection.KekFile}{KindOfFile(inspection.KekFile, env)}",
    };

    /// <summary>A Docker secret is mounted under <c>/run/secrets</c>, a systemd credential under <c>$CREDENTIALS_DIRECTORY</c> (<c>/run/credentials/...</c>).</summary>
    private static string KindOfFile(string? file, Func<string, string?> env)
    {
        if (file is null)
        {
            return string.Empty;
        }

        if (file.StartsWith("/run/secrets/", StringComparison.Ordinal))
        {
            return " (a Docker secret)";
        }

        var credentials = env("CREDENTIALS_DIRECTORY");
        return file.StartsWith("/run/credentials/", StringComparison.Ordinal)
            || (!string.IsNullOrEmpty(credentials) && file.StartsWith(credentials.TrimEnd('/') + "/", StringComparison.Ordinal))
            ? " (a systemd credential)"
            : " (a file)";
    }
}
