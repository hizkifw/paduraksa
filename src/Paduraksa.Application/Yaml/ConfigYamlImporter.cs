using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

namespace Paduraksa.Application.Yaml;

/// <summary>How an import treats the stored objects the YAML doesn't name.</summary>
public enum ConfigImportMode
{
    /// <summary>Objects are matched by ID, else by name (a host by its first host name); the YAML's are added or updated, nothing is deleted.</summary>
    Merge,

    /// <summary>The config becomes the YAML: a stored object it doesn't name is deleted.</summary>
    Replace,
}

/// <summary>Reads YAML text into a <see cref="MappedYaml"/>: the guarded parse (<see cref="YamlTreeReader"/>), then the mapping (<see cref="ConfigYamlMapper"/>).</summary>
internal static class ConfigYamlImporter
{
    private static readonly Dictionary<Type, Func<string>> NewIds = new()
    {
        [typeof(ProfileId)] = static () => ProfileId.New().ToString(),
        [typeof(UpstreamId)] = static () => UpstreamId.New().ToString(),
        [typeof(RateLimitPolicyId)] = static () => RateLimitPolicyId.New().ToString(),
        [typeof(AccessListId)] = static () => AccessListId.New().ToString(),
        [typeof(RuleSetId)] = static () => RuleSetId.New().ToString(),
        [typeof(AuthProviderId)] = static () => AuthProviderId.New().ToString(),
        [typeof(HostId)] = static () => HostId.New().ToString(),
        [typeof(RouteId)] = static () => RouteId.New().ToString(),
        [typeof(DestinationId)] = static () => DestinationId.New().ToString(),
    };

    /// <summary>New IDs: random, one per object that the YAML names without an <c>id</c> and that matches no stored object.</summary>
    public static Func<Type, string> RandomIds { get; } = static type => NewIds[type]();

    public static YamlMapResult Read(ReadOnlySpan<byte> yaml, ConfigDocument? stored, ConfigImportMode mode, Func<Type, string>? newId = null, YamlLimits? limits = null)
    {
        if (!YamlTreeReader.TryRead(yaml, limits ?? YamlLimits.Default, out var tree, out var problem))
        {
            return new YamlMapResult(null, [new ConfigError(string.Empty, problem.Code, problem.Located)]);
        }

        return ConfigYamlMapper.Map(tree!, stored, mode == ConfigImportMode.Merge, newId ?? RandomIds);
    }
}
