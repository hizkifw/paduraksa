using Paduraksa.Contracts.Config;

namespace Paduraksa.Application.Yaml;

/// <summary>A document for the pipeline and, for each element of each of its lists, which entry of the YAML it came from (null: a stored object the import left alone).</summary>
/// <param name="Document">What the import commits.</param>
/// <param name="Origins">By list name (<c>hosts</c>), the YAML index of each element of that list in <paramref name="Document"/>; no entry for a list means the YAML's own order, element for element.</param>
internal sealed record ImportPlan(ConfigDocument Document, IReadOnlyDictionary<string, int?[]> Origins)
{
    /// <summary>The YAML pointer of a pointer into <see cref="Document"/>'s top-level lists (<c>/hosts/3/names</c>), or null when the object at it isn't the YAML's.</summary>
    public string? ToYaml(string pointer)
    {
        var parts = pointer.Split('/', 4);
        if (parts.Length < 3 || parts[0].Length != 0 || !Origins.TryGetValue(parts[1], out var origins))
        {
            return pointer;
        }

        if (!int.TryParse(parts[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index) || index >= origins.Length)
        {
            return pointer;
        }

        if (origins[index] is not { } yamlIndex)
        {
            return null;
        }

        var head = $"/{parts[1]}/{yamlIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        return parts.Length == 4 ? $"{head}/{parts[3]}" : head;
    }
}

/// <summary>What importing does to the stored config, as a document (PLAN §11.5): replace is the YAML, merge is the stored config with the YAML's objects added or updated.</summary>
internal static class ConfigImportMerge
{
    /// <summary>The config becomes the YAML: global settings it doesn't write are the defaults, a list it doesn't write is empty, so the stored objects it doesn't name are deleted.</summary>
    public static ImportPlan Replace(MappedYaml imported) => new(imported.Document, new Dictionary<string, int?[]>());

    /// <summary>
    /// The stored config with the YAML's objects in it: an object of the YAML replaces the stored one with its ID (as written, not field by field: an
    /// omitted key is gone), the others are added after the stored ones, and nothing is deleted. Global settings are replaced only if the YAML has them.
    /// </summary>
    public static ImportPlan Merge(ConfigDocument stored, MappedYaml imported)
    {
        var yaml = imported.Document;
        Dictionary<string, int?[]> origins = [];
        var document = stored with
        {
            Settings = imported.HasSettings ? yaml.Settings with { Version = stored.Settings.Version } : stored.Settings,
            Profiles = List("profiles", stored.Profiles, yaml.Profiles, static p => p.Id, static (p, v) => p with { Version = v }, static p => p.Version, origins),
            Upstreams = List("upstreams", stored.Upstreams, yaml.Upstreams, static u => u.Id, static (u, v) => u with { Version = v }, static u => u.Version, origins),
            RateLimitPolicies = List("rateLimitPolicies", stored.RateLimitPolicies, yaml.RateLimitPolicies, static p => p.Id, static (p, v) => p with { Version = v }, static p => p.Version, origins),
            AccessLists = List("accessLists", stored.AccessLists, yaml.AccessLists, static l => l.Id, static (l, v) => l with { Version = v }, static l => l.Version, origins),
            RuleSets = List("ruleSets", stored.RuleSets, yaml.RuleSets, static r => r.Id, static (r, v) => r with { Version = v }, static r => r.Version, origins),
            AuthProviders = List("authProviders", stored.AuthProviders, yaml.AuthProviders, static p => p.Id, static (p, v) => p with { Version = v }, static p => p.Version, origins),
            Hosts = List("hosts", stored.Hosts, yaml.Hosts, static h => h.Id, static (h, v) => h with { Version = v }, static h => h.Version, origins),
        };
        return new ImportPlan(document, origins);
    }

    private static List<T> List<T, TId>(
        string name, IReadOnlyList<T> stored, IReadOnlyList<T> imported, Func<T, TId> idOf, Func<T, long, T> withVersion, Func<T, long> versionOf, Dictionary<string, int?[]> origins)
        where TId : notnull
    {
        Dictionary<TId, int> yamlIndex = [];
        for (var i = 0; i < imported.Count; i++)
        {
            yamlIndex[idOf(imported[i])] = i;
        }

        List<T> result = [];
        List<int?> from = [];
        HashSet<TId> placed = [];
        foreach (var existing in stored)
        {
            if (yamlIndex.TryGetValue(idOf(existing), out var index))
            {
                result.Add(withVersion(imported[index], versionOf(existing)));
                from.Add(index);
                placed.Add(idOf(existing));
            }
            else
            {
                result.Add(existing);
                from.Add(null);
            }
        }

        for (var i = 0; i < imported.Count; i++)
        {
            if (placed.Add(idOf(imported[i])))
            {
                result.Add(imported[i]);
                from.Add(i);
            }
        }

        origins[name] = [.. from];
        return result;
    }
}
