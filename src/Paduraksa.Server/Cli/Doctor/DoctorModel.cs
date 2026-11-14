using System.Text.Json.Serialization;

namespace Paduraksa.Server.Cli.Doctor;

/// <summary>How one finding counts: <see cref="Failure"/> is the only one that fails the run.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CheckStatus>))]
internal enum CheckStatus
{
    /// <summary>Checked, and fine.</summary>
    [JsonStringEnumMemberName("ok")]
    Ok,

    /// <summary>Worth knowing, nothing to do (a pending migration before the first start after an upgrade, a check that was skipped).</summary>
    [JsonStringEnumMemberName("info")]
    Info,

    /// <summary>Works, but something should change.</summary>
    [JsonStringEnumMemberName("warning")]
    Warning,

    /// <summary>The server would not start, or would not work, as things are.</summary>
    [JsonStringEnumMemberName("failure")]
    Failure,
}

/// <summary>One line of the report.</summary>
/// <param name="Id">A stable name for the check (<c>master-key</c>, <c>listener-admin</c>), for scripts.</param>
/// <param name="Status">How it counts.</param>
/// <param name="Message">What was found, in one sentence.</param>
/// <param name="Fix">What to do about a warning or a failure; null otherwise.</param>
internal sealed record CheckResult(string Id, CheckStatus Status, string Message, string? Fix = null)
{
    public static CheckResult Ok(string id, string message) => new(id, CheckStatus.Ok, message);

    public static CheckResult Info(string id, string message) => new(id, CheckStatus.Info, message);

    public static CheckResult Warning(string id, string message, string fix) => new(id, CheckStatus.Warning, message, fix);

    public static CheckResult Failure(string id, string message, string fix) => new(id, CheckStatus.Failure, message, fix);
}

/// <summary>The whole report.</summary>
internal sealed record DoctorReport(string Version, IReadOnlyList<CheckResult> Checks)
{
    public int Count(CheckStatus status) => Checks.Count(c => c.Status == status);

    /// <summary>True unless a check failed: warnings and infos pass.</summary>
    public bool Passed => Count(CheckStatus.Failure) == 0;
}

/// <summary>One check of the doctor: a small unit that reads the host and reports lines, never changes anything.</summary>
internal interface IDoctorCheck
{
    /// <summary>What the report says when the check could not run (<c>listeners</c>).</summary>
    string Name { get; }

    /// <summary>True when the check reads the effective options: with an invalid configuration it is skipped, and the <c>options</c> check says why.</summary>
    bool NeedsOptions { get; }

    Task<IReadOnlyList<CheckResult>> RunAsync(DoctorContext context, CancellationToken cancellationToken);
}
