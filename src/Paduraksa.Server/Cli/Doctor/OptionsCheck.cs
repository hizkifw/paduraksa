using Paduraksa.Contracts.Config;
using Paduraksa.Data;

namespace Paduraksa.Server.Cli.Doctor;

/// <summary>The effective configuration validates, and which settings the environment locks against the dashboard.</summary>
internal sealed class OptionsCheck : IDoctorCheck
{
    public string Name => "options";

    public bool NeedsOptions => false;

    public async Task<IReadOnlyList<CheckResult>> RunAsync(DoctorContext context, CancellationToken cancellationToken)
    {
        if (context.Options is not { } options)
        {
            return
            [
                .. context.OptionsProblems.Select(problem => CheckResult.Failure(
                    "options", problem, "Fix the setting (or unset its Paduraksa__ variable): the server refuses to start with it, exit code 3.")),
            ];
        }

        List<CheckResult> results = [CheckResult.Ok("options", "The configuration is valid.")];
        if (options.ServerSettingEnvironment.Count == 0)
        {
            return results;
        }

        var stored = await context.ReadStoredSettings(Path.Combine(options.DataDirectory, ConfigDatabase.FileName), cancellationToken).ConfigureAwait(false);
        foreach (var setting in ServerSettingCatalog.All)
        {
            if (!options.ServerSettingEnvironment.TryGetValue(setting.Key, out var value))
            {
                continue;
            }

            var source = context.Env(setting.EnvironmentVariable) is null ? $"the configuration ({setting.ConfigurationKey})" : setting.EnvironmentVariable;
            var locked = $"{setting.Title} is {value}, set by {source}: the dashboard cannot change it.";
            if (stored is not null && setting.IsSet(stored) && setting.TextOf(stored) is { } kept
                && !string.Equals(kept, value, StringComparison.Ordinal))
            {
                results.Add(CheckResult.Warning(
                    "setting-locked",
                    $"{locked} The dashboard stores {kept}, which is ignored.",
                    $"Remove {setting.EnvironmentVariable} to use the dashboard's value, or change the dashboard's to match."));
            }
            else
            {
                results.Add(CheckResult.Info("setting-locked", locked));
            }
        }

        return results;
    }
}
