using System.Collections.Frozen;

using Paduraksa.Application.Commands;
using Paduraksa.Contracts.Config;

namespace Paduraksa.Application.Settings;

/// <summary>
/// The server settings in force (docs/design/config.md "Server settings"): for each setting, the value the server's environment sets explicitly,
/// else the one the live config stores, else the built-in default. One per process; the consumers are handed what changed through
/// <see cref="Changed"/>, and the ones that read a value once at startup are compared against <see cref="Startup"/> for the page's
/// pending-restart marker.
/// </summary>
/// <remarks>
/// The validator checks the rules between settings (<see cref="ServerSettingCatalog.Rules"/>) on the config's values over the defaults. A value
/// the environment sets is outside its view, so a pair the environment makes impossible (an idle timeout stored longer than the session lifetime
/// the environment sets) is settled here: every stored setting of a broken rule falls back to its default, until no rule changes (<see cref="Settle"/>).
/// </remarks>
public sealed class ServerSettingsState
{
    private readonly FrozenDictionary<string, string> _environment;
    private readonly Lock _lock = new();
    private ServerSettings? _config;
    private volatile Snapshot _current;

    /// <param name="environment">The settings the environment sets explicitly: <see cref="ServerSettingDefinition.Key"/> to the value as the config
    /// writes it (<see cref="ServerSettingDefinition.FromConfiguration"/>). Keys the catalogue doesn't declare are ignored.</param>
    public ServerSettingsState(IReadOnlyDictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _environment = environment
            .Where(pair => ServerSettingCatalog.Find(pair.Key) is not null)
            .ToFrozenDictionary(StringComparer.Ordinal);
        _current = Resolve(null);
        Startup = _current.Effective;
    }

    /// <summary>The values in force, every setting resolved: read one with the declaration's <c>Resolve</c>.</summary>
    public ServerSettings Current => _current.Effective;

    /// <summary>The values in force when the process first took them (<see cref="Seal"/>): what a setting that applies after a restart is running with.</summary>
    public ServerSettings Startup { get; private set; }

    /// <summary>Raised with the new values whenever one in force changes; on the thread that changed them (the config's writer), so a handler only hands values on.</summary>
    public event Action<ServerSettings>? Changed;

    /// <summary>Where <paramref name="setting"/>'s value in force comes from.</summary>
    public ServerSettingSource SourceOf(ServerSettingDefinition setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        return _current.Sources[setting.Key];
    }

    /// <summary>The environment's value of <paramref name="setting"/> as the config writes it, or null when the environment leaves it to the config.</summary>
    public string? EnvironmentValue(ServerSettingDefinition setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        return _environment.GetValueOrDefault(setting.Key);
    }

    /// <summary>Whether a setting read once at startup now resolves to something else than it runs with.</summary>
    public bool RestartPending(ServerSettingDefinition setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        return setting.Apply == ServerSettingApply.Restart && setting.Differs(Startup, Current);
    }

    /// <summary>Follows <paramref name="config"/>'s queued snapshots from now on, starting with the one it has.</summary>
    public void Follow(ConfigService config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.ServerSettingsChanged += Take;
        Take(config.ServerSettings);
    }

    /// <summary>Takes the config's stored server settings (null: it sets none).</summary>
    public void Take(ServerSettings? config)
    {
        Snapshot next;
        lock (_lock)
        {
            _config = config;
            var previous = _current;
            next = Resolve(config);
            if (!ServerSettingsChange.Differs(previous.Effective, next.Effective) && SameSources(previous, next))
            {
                return;
            }

            _current = next;
        }

        Changed?.Invoke(next.Effective);
    }

    /// <summary>
    /// The values the restart-only consumers start with are the ones in force now: called once the startup config is known and before the
    /// listeners bind. Until then <see cref="Startup"/> is the environment over the defaults.
    /// </summary>
    public void Seal()
    {
        lock (_lock)
        {
            Startup = _current.Effective;
        }
    }

    /// <summary>The config's stored values, or null.</summary>
    public ServerSettings? Stored
    {
        get
        {
            lock (_lock)
            {
                return _config;
            }
        }
    }

    private Snapshot Resolve(ServerSettings? config)
    {
        config ??= new();
        ServerSettings effective = new();
        Dictionary<string, ServerSettingSource> sources = new(StringComparer.Ordinal);
        foreach (var setting in ServerSettingCatalog.All)
        {
            if (_environment.TryGetValue(setting.Key, out var text))
            {
                effective = setting.WithText(effective, text).Settings;
                sources[setting.Key] = ServerSettingSource.Environment;
            }
            else if (setting.IsSet(config) && setting.Problem(config) is null)
            {
                effective = setting.CopyFrom(config, effective);
                sources[setting.Key] = ServerSettingSource.Config;
            }
            else
            {
                sources[setting.Key] = ServerSettingSource.Default;
            }
        }

        effective = Settle(effective, sources);

        // Every setting resolved, so a reader never needs the default again.
        foreach (var setting in ServerSettingCatalog.All)
        {
            effective = setting.WithText(effective, setting.ResolvedText(effective)).Settings;
        }

        return new Snapshot(effective, sources.ToFrozenDictionary(StringComparer.Ordinal));
    }

    /// <summary>
    /// Drops the stored side of every rule the mix of environment and stored values breaks: each setting of the rule that comes from the config
    /// falls back to its default, and the rules are checked again, since a default can break another. Each pass drops at least one stored value
    /// or stops, so it ends within as many passes as there are settings. A rule still broken with no stored value in it is the environment's
    /// against a default; the options validator refuses that at startup, so it is left as it is.
    /// </summary>
    internal static ServerSettings Settle(ServerSettings effective, Dictionary<string, ServerSettingSource> sources)
    {
        for (var pass = 0; pass <= ServerSettingCatalog.All.Count; pass++)
        {
            var dropped = false;
            foreach (var rule in ServerSettingCatalog.Rules)
            {
                if (rule.Check(effective) is null)
                {
                    continue;
                }

                foreach (var setting in rule.Involves.Where(s => sources[s.Key] == ServerSettingSource.Config))
                {
                    effective = setting.CopyFrom(new ServerSettings(), effective);
                    sources[setting.Key] = ServerSettingSource.Default;
                    dropped = true;
                }
            }

            if (!dropped)
            {
                break;
            }
        }

        return effective;
    }

    private static bool SameSources(Snapshot a, Snapshot b) => a.Sources.All(pair => b.Sources[pair.Key] == pair.Value);

    private sealed record Snapshot(ServerSettings Effective, FrozenDictionary<string, ServerSettingSource> Sources);
}
