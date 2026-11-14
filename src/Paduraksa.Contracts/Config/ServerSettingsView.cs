namespace Paduraksa.Contracts.Config;

/// <summary>The server settings as the API and the dashboard show them: every declared setting with its value in force and where it comes from.</summary>
/// <param name="Version">The global settings' version: the <c>If-Match</c> a change needs.</param>
/// <param name="Stored">What the config stores (null fields are left to the default or the environment): the body a change replaces.</param>
/// <param name="Settings">Every setting, in the page's order.</param>
public sealed record ServerSettingsView(long Version, ServerSettings Stored, IReadOnlyList<ServerSettingView> Settings);

/// <summary>One server setting. Values are text in the form the config writes them (<c>90.00:00:00</c>, <c>1073741824</c>, <c>true</c>).</summary>
/// <param name="Key">Its member in <see cref="ServerSettings"/>.</param>
/// <param name="Group">Where the page shows it.</param>
/// <param name="Section">Its heading within the group.</param>
/// <param name="Kind">The kind of value.</param>
/// <param name="Apply">Whether a change applies live or after a restart.</param>
/// <param name="Title">Its name.</param>
/// <param name="Description">What it does.</param>
/// <param name="ConfigurationKey">The appsettings key (<c>Paduraksa:Metrics:Retention</c>).</param>
/// <param name="EnvironmentVariable">The environment variable that sets it, and then wins over the config.</param>
/// <param name="Default">The built-in default.</param>
/// <param name="Min">The least value accepted, for an ordered kind.</param>
/// <param name="Max">The largest value accepted, for an ordered kind.</param>
/// <param name="Stored">What the config stores; null when it leaves the setting to the default.</param>
/// <param name="Value">The value in force.</param>
/// <param name="Source">Where <paramref name="Value"/> comes from.</param>
/// <param name="Running">For a setting that applies after a restart: the value the process runs with.</param>
/// <param name="RestartPending">The value in force differs from <paramref name="Running"/>: a restart applies it.</param>
public sealed record ServerSettingView(
    string Key,
    ServerSettingGroup Group,
    string Section,
    ServerSettingKind Kind,
    ServerSettingApply Apply,
    string Title,
    string Description,
    string ConfigurationKey,
    string EnvironmentVariable,
    string Default,
    string Value,
    ServerSettingSource Source,
    bool RestartPending,
    string? Min = null,
    string? Max = null,
    string? Stored = null,
    string? Running = null);
