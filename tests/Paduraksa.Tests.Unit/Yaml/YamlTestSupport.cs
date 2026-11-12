using System.Text;

using Paduraksa.Application.Config;
using Paduraksa.Application.Yaml;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Ids;
using Paduraksa.Core.Model;
using Paduraksa.Tests.Unit.Config;

namespace Paduraksa.Tests.Unit.Yaml;

internal static class YamlTestSupport
{
    /// <summary>The document as an import reads it back: canonical, with every version 0, because the YAML carries none.</summary>
    public static ConfigDocument WithoutVersions(ConfigDocument document) =>
        CanonicalConfigJson.Canonicalize(document with
        {
            Settings = document.Settings with { Version = 0 },
            Profiles = [.. document.Profiles.Select(static p => p with { Version = 0 })],
            Upstreams = [.. document.Upstreams.Select(static u => u with { Version = 0 })],
            RateLimitPolicies = [.. document.RateLimitPolicies.Select(static p => p with { Version = 0 })],
            AccessLists = [.. document.AccessLists.Select(static l => l with { Version = 0 })],
            RuleSets = [.. document.RuleSets.Select(static r => r with { Version = 0 })],
            AuthProviders = [.. document.AuthProviders.Select(static p => p with { Version = 0 })],
            Hosts = [.. document.Hosts.Select(static h => h with { Version = 0 })],
        });

    public static YamlMapResult Read(string yaml, ConfigDocument? stored = null, ConfigImportMode mode = ConfigImportMode.Replace, Func<Type, string>? newId = null) =>
        ConfigYamlImporter.Read(Encoding.UTF8.GetBytes(yaml), stored, mode, newId ?? CountingIds());

    /// <summary>Valid IDs that are the same every run: the n-th object asked for gets the n-th.</summary>
    public static Func<Type, string> CountingIds()
    {
        var next = 0;
        return type => typeof(ConfigSamples).GetMethod(nameof(ConfigSamples.Id))!.MakeGenericMethod(type).Invoke(null, [50_000 + next++])!.ToString()!;
    }

    public static string Errors(YamlMapResult result) => string.Join("\n", result.Errors.Select(static e => $"{e.Path} [{e.Code}] {e.Message}"));

    public static ConfigDocument Mapped(string yaml, ConfigDocument? stored = null, ConfigImportMode mode = ConfigImportMode.Replace)
    {
        var result = Read(yaml, stored, mode);
        Xunit.Assert.True(result.Succeeded, Errors(result));
        return result.Mapped!.Document;
    }
}
