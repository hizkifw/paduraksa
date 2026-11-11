using MudBlazor;

using Paduraksa.Contracts.Config;

namespace Paduraksa.Dashboard.Config;

/// <summary>Tones as the theme's CSS classes and MudBlazor colours, and the tones of what a change does.</summary>
public static class Tones
{
    public static string Css(this Tone tone) => tone switch
    {
        Tone.Primary => "primary",
        Tone.Success => "success",
        Tone.Info => "info",
        Tone.Warning => "warning",
        Tone.Error => "error",
        _ => "default",
    };

    public static Color Color(this Tone tone) => tone switch
    {
        Tone.Primary => MudBlazor.Color.Primary,
        Tone.Success => MudBlazor.Color.Success,
        Tone.Info => MudBlazor.Color.Info,
        Tone.Warning => MudBlazor.Color.Warning,
        Tone.Error => MudBlazor.Color.Error,
        _ => MudBlazor.Color.Default,
    };

    /// <summary>Added is green, removed red, modified amber; an untouched parent is neutral.</summary>
    public static Tone Of(ConfigChangeKind? kind) => kind switch
    {
        ConfigChangeKind.Added => Tone.Success,
        ConfigChangeKind.Removed => Tone.Error,
        ConfigChangeKind.Modified => Tone.Warning,
        _ => Tone.Default,
    };

    public static string Word(ConfigChangeKind? kind) => kind switch
    {
        ConfigChangeKind.Added => "Added",
        ConfigChangeKind.Removed => "Removed",
        ConfigChangeKind.Modified => "Changed",
        _ => "Unchanged",
    };

    public static string IconOf(ChangedObject what) => what switch
    {
        ChangedObject.Host => Icons.Material.Outlined.Language,
        ChangedObject.Route => Icons.Material.Outlined.AltRoute,
        ChangedObject.Upstream => Icons.Material.Outlined.Hub,
        ChangedObject.Destination => Icons.Material.Outlined.Dns,
        ChangedObject.Profile => Icons.Material.Outlined.Badge,
        ChangedObject.RateLimitPolicy => Icons.Material.Outlined.Speed,
        ChangedObject.AccessList => Icons.Material.Outlined.Lock,
        ChangedObject.RuleSet => Icons.Material.Outlined.Rule,
        ChangedObject.Settings => Icons.Material.Outlined.Tune,
        _ => Icons.Material.Outlined.Description,
    };
}
