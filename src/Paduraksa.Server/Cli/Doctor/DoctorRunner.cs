using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Paduraksa.Server.Cli.Doctor;

/// <summary>Runs the checks in order and prints what they found.</summary>
internal static class DoctorRunner
{
    /// <summary>The checks, in the order the report lists them: the options first, since the others read them.</summary>
    internal static IReadOnlyList<IDoctorCheck> DefaultChecks { get; } =
    [
        new OptionsCheck(),
        new DataDirectoryCheck(),
        new MasterKeyCheck(),
        new DatabasesCheck(),
        new ListenersCheck(),
        new Http3Check(),
        new ClockCheck(),
        new RunningServerCheck(),
    ];

    public static async Task<DoctorReport> RunAsync(DoctorContext context, IEnumerable<IDoctorCheck> checks, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(checks);
        List<CheckResult> results = [];
        foreach (var check in checks)
        {
            if (check.NeedsOptions && context.Options is null)
            {
                results.Add(CheckResult.Info(Slug(check.Name), $"The {check.Name} was not checked: the configuration is invalid (see above)."));
                continue;
            }

            try
            {
                results.AddRange(await check.RunAsync(context, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One check that can't run must not hide the others' findings.
#pragma warning disable CA1031 // Deliberate: a diagnostic tool reports what it could not check instead of dying on it.
                results.Add(CheckResult.Warning(Slug(check.Name), $"The {check.Name} could not be checked: {ex.Message}", "Report this, with the message, if it persists."));
#pragma warning restore CA1031
            }
        }

        return new DoctorReport(context.Version, results);
    }

    private static string Slug(string name) => name.Replace(' ', '-').Replace("/", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

    /// <summary>
    /// One line per finding, then what to do, indented, under a warning or a failure. ASCII, like every other line the CLI prints: the tags stand
    /// in for the marks a terminal font may not have.
    /// </summary>
    public static async Task WriteTextAsync(DoctorReport report, TextWriter output)
    {
        foreach (var check in report.Checks)
        {
            await output.WriteLineAsync($"{Tag(check.Status)} {check.Id}: {check.Message}").ConfigureAwait(false);
            if (check.Fix is not null)
            {
                await output.WriteLineAsync($"       To fix: {check.Fix}").ConfigureAwait(false);
            }
        }

        var failures = report.Count(CheckStatus.Failure);
        var warnings = report.Count(CheckStatus.Warning);
        var summary = string.Create(
            CultureInfo.InvariantCulture,
            $"{report.Count(CheckStatus.Ok)} ok, {report.Count(CheckStatus.Info)} info, {warnings} warning(s), {failures} failure(s).");
        await output.WriteLineAsync(string.Empty).ConfigureAwait(false);
        await output.WriteLineAsync(failures > 0 ? "Not healthy: " + summary : warnings > 0 ? "Healthy, with warnings: " + summary : "Healthy: " + summary).ConfigureAwait(false);
    }

    public static Task WriteJsonAsync(DoctorReport report, TextWriter output) =>
        output.WriteLineAsync(JsonSerializer.Serialize(
            new JsonReport(
                report.Version,
                report.Passed,
                new JsonSummary(report.Count(CheckStatus.Ok), report.Count(CheckStatus.Info), report.Count(CheckStatus.Warning), report.Count(CheckStatus.Failure)),
                report.Checks),
            DoctorJsonContext.Default.JsonReport));

    private static string Tag(CheckStatus status) => status switch
    {
        CheckStatus.Ok => "[ ok ]",
        CheckStatus.Info => "[info]",
        CheckStatus.Warning => "[warn]",
        _ => "[FAIL]",
    };
}

/// <summary>The <c>--json</c> document: <c>passed</c> is false exactly when the exit code is 1.</summary>
internal sealed record JsonReport(string Version, bool Passed, JsonSummary Summary, IReadOnlyList<CheckResult> Checks);

internal sealed record JsonSummary(int Ok, int Info, int Warning, int Failure);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(JsonReport))]
internal sealed partial class DoctorJsonContext : JsonSerializerContext;
