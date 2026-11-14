using Paduraksa.Contracts.Config;

namespace Paduraksa.Application.Settings;

/// <summary>What a change does to the server settings, compared setting by setting through the catalogue.</summary>
public static class ServerSettingsChange
{
    /// <summary>Whether any setting is stored differently (set, cleared or changed) between the two.</summary>
    public static bool Differs(ServerSettings? before, ServerSettings? after)
    {
        before ??= new();
        after ??= new();
        foreach (var setting in ServerSettingCatalog.All)
        {
            if (!string.Equals(setting.TextOf(before), setting.TextOf(after), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The settings whose stored value differs between the two.</summary>
    public static IReadOnlyList<ServerSettingDefinition> Changed(ServerSettings? before, ServerSettings? after)
    {
        before ??= new();
        after ??= new();
        return [.. ServerSettingCatalog.All.Where(s => !string.Equals(s.TextOf(before), s.TextOf(after), StringComparison.Ordinal))];
    }
}
