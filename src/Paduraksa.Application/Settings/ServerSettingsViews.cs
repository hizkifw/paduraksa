using Paduraksa.Contracts.Config;

namespace Paduraksa.Application.Settings;

/// <summary>Builds the <see cref="ServerSettingsView"/> the API and the dashboard show, from the settings in force and a config's stored values.</summary>
public static class ServerSettingsViews
{
    /// <param name="state">The values in force.</param>
    /// <param name="settings">The global settings of the config the view is of (the head, or the head with a draft applied).</param>
    public static ServerSettingsView Of(ServerSettingsState state, GlobalSettings settings)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(settings);
        var stored = settings.Server ?? new ServerSettings();
        var current = state.Current;
        List<ServerSettingView> views = [];
        foreach (var setting in ServerSettingCatalog.All)
        {
            var restart = setting.Apply == ServerSettingApply.Restart;
            views.Add(new ServerSettingView(
                setting.Key,
                setting.Group,
                setting.Section,
                setting.Kind,
                setting.Apply,
                setting.Title,
                setting.Help,
                "Paduraksa:" + setting.ConfigurationKey,
                setting.EnvironmentVariable,
                setting.DefaultText,
                setting.ResolvedText(current),
                state.SourceOf(setting),
                state.RestartPending(setting),
                setting.MinText,
                setting.MaxText,
                setting.TextOf(stored),
                restart ? setting.ResolvedText(state.Startup) : null));
        }

        return new ServerSettingsView(settings.Version, stored, views);
    }
}
