using Paduraksa.Contracts.Config;

namespace Paduraksa.Server.Configuration;

/// <summary>
/// Which server settings the configuration sets explicitly (docs/design/config.md "Server settings", precedence): a key that any provider gives a
/// value (appsettings, an environment variable, a test's in-memory source) is the environment's, even when that value equals the default.
/// </summary>
internal static class ServerSettingEnvironment
{
    /// <summary>The catalogue keys <paramref name="section"/> (<c>Paduraksa</c>) sets, with their values as the config writes them.</summary>
    public static IReadOnlyDictionary<string, string> Read(IConfiguration section)
    {
        ArgumentNullException.ThrowIfNull(section);
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (var setting in ServerSettingCatalog.All)
        {
            // An empty value is a set value for text (no admin address) and unreadable for the rest, which the binder refuses first.
            if (section[setting.ConfigurationKey] is { } raw && setting.FromConfiguration(raw) is { } text)
            {
                values[setting.Key] = text;
            }
        }

        return values;
    }
}
