using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

namespace Paduraksa.Application.Config;

/// <summary>
/// Structural diff of two config documents: the object-level change list (<see cref="ConfigChange"/>)
/// behind the dashboard's review sheet, <c>dryRun</c> responses and revision summaries. Pure and deterministic.
/// </summary>
/// <remarks>
/// Both documents are canonicalised first, so re-spelling and reordering sets are no change, and every path indexes the
/// canonical document. Routes are the exception among lists: their order is config (first match wins), so when the
/// routes a host keeps change their relative order, the host lists its <c>routes</c> field. Fields are compared on
/// their canonical JSON, so a new field of the model is diffed without touching this class. Order: the document
/// (<c>apiVersion</c>), the settings, then profiles, upstreams (each followed by its destinations) and hosts (each
/// followed by its routes), each group by ID. Any document can be compared, valid or not.
/// </remarks>
public static class ConfigDiff
{
    private const string VersionProperty = "version";

    // The document's contract, but writing nulls: both sides of a comparison then list the same properties in the same
    // (contract) order, and a value set on one side only is a null on the other.
    private static readonly ConfigJsonContext Json =
        new(new JsonSerializerOptions(ConfigJsonContext.Default.Options) { DefaultIgnoreCondition = JsonIgnoreCondition.Never });

    /// <summary>What changes from <paramref name="before"/> to <paramref name="after"/>; empty when nothing does.</summary>
    public static IReadOnlyList<ConfigChange> Compare(ConfigDocument before, ConfigDocument after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ConfigDocument old = CanonicalConfigJson.Canonicalize(before);
        ConfigDocument @new = CanonicalConfigJson.Canonicalize(after);
        ConfigJsonContext json = Json;
        List<ConfigChange> changes = [];

        if (old.ApiVersion != @new.ApiVersion)
        {
            changes.Add(new DocumentChange(ConfigChangeKind.Modified, string.Empty, ["/apiVersion"]));
        }

        List<string> settings = Fields(old.Settings, @new.Settings, "/settings", json.GlobalSettings);
        if (settings.Count > 0)
        {
            changes.Add(new SettingsChange(ConfigChangeKind.Modified, "/settings", settings));
        }

        Objects(
            old.Profiles,
            @new.Profiles,
            p => p.Id,
            "/profiles",
            json.ProfileConfig,
            (kind, id, path, fields) => new ProfileChange(kind, id, path, fields),
            children: null,
            changes);

        Objects(
            old.Upstreams,
            @new.Upstreams,
            u => u.Id,
            "/upstreams",
            json.UpstreamConfig,
            (kind, id, path, fields) => new UpstreamChange(kind, id, path, fields),
            (upstreamId, oldUpstream, newUpstream, oldPath, newPath) => Objects(
                oldUpstream.Destinations,
                newUpstream.Destinations,
                d => d.Id,
                $"{newPath}/destinations",
                json.DestinationConfig,
                (kind, id, path, fields) => new DestinationChange(kind, upstreamId, id, path, fields),
                children: null,
                changes,
                $"{oldPath}/destinations"),
            changes,
            skip: "destinations");

        Objects(
            old.Hosts,
            @new.Hosts,
            h => h.Id,
            "/hosts",
            json.HostConfig,
            (kind, id, path, fields) => new HostChange(kind, id, path, fields),
            (hostId, oldHost, newHost, oldPath, newPath) => Objects(
                oldHost.Routes,
                newHost.Routes,
                r => r.Id,
                $"{newPath}/routes",
                json.RouteConfig,
                (kind, id, path, fields) => new RouteChange(kind, hostId, id, path, fields),
                children: null,
                changes,
                $"{oldPath}/routes"),
            changes,
            skip: "routes",
            childOrderChanged: (oldHost, newHost) => RelativeOrderChanged(oldHost.Routes, newHost.Routes));

        return changes;
    }

    // Matches objects by ID (the first of duplicates; valid documents have none) and lists added, removed and
    // modified ones in ID order, each followed by its children's changes when it exists on both sides.
    private static void Objects<T, TId>(
        IReadOnlyList<T> before,
        IReadOnlyList<T> after,
        Func<T, TId> id,
        string newPath,
        JsonTypeInfo<T> typeInfo,
        Func<ConfigChangeKind, TId, string, IReadOnlyList<string>, ConfigChange> change,
        Action<TId, T, T, string, string>? children,
        List<ConfigChange> changes,
        string? oldPath = null,
        string? skip = null,
        Func<T, T, bool>? childOrderChanged = null)
        where TId : struct, IComparable<TId>
    {
        oldPath ??= newPath;
        Dictionary<TId, int> oldIndex = Index(before, id);
        Dictionary<TId, int> newIndex = Index(after, id);
        foreach (TId key in oldIndex.Keys.Union(newIndex.Keys).Order())
        {
            bool inOld = oldIndex.TryGetValue(key, out int o);
            bool inNew = newIndex.TryGetValue(key, out int n);
            if (!inOld)
            {
                changes.Add(change(ConfigChangeKind.Added, key, $"{newPath}/{n}", []));
            }
            else if (!inNew)
            {
                changes.Add(change(ConfigChangeKind.Removed, key, $"{oldPath}/{o}", []));
            }
            else
            {
                bool reordered = childOrderChanged?.Invoke(before[o], after[n]) ?? false;
                List<string> fields = Fields(before[o], after[n], $"{newPath}/{n}", typeInfo, skip, reordered);
                if (fields.Count > 0)
                {
                    changes.Add(change(ConfigChangeKind.Modified, key, $"{newPath}/{n}", fields));
                }

                children?.Invoke(key, before[o], after[n], $"{oldPath}/{o}", $"{newPath}/{n}");
            }
        }
    }

    private static Dictionary<TId, int> Index<T, TId>(IReadOnlyList<T> items, Func<T, TId> id)
        where TId : struct
    {
        Dictionary<TId, int> index = [];
        for (int i = 0; i < items.Count; i++)
        {
            index.TryAdd(id(items[i]), i);
        }

        return index;
    }

    // Whether the routes on both sides come in a different relative order (added and removed ones aside).
    private static bool RelativeOrderChanged(IReadOnlyList<RouteConfig> before, IReadOnlyList<RouteConfig> after)
    {
        HashSet<RouteId> inBefore = [.. before.Select(r => r.Id)];
        HashSet<RouteId> inAfter = [.. after.Select(r => r.Id)];
        return !before.Select(r => r.Id).Where(inAfter.Contains).SequenceEqual(after.Select(r => r.Id).Where(inBefore.Contains));
    }

    // Pointers of the fields that differ: the object's own version doesn't count, and the skipped child list only when
    // its children were reordered.
    private static List<string> Fields<T>(T before, T after, string path, JsonTypeInfo<T> typeInfo, string? skip = null, bool reordered = false)
    {
        List<string> fields = [];
        Compare(
            JsonSerializer.SerializeToElement(before, typeInfo),
            JsonSerializer.SerializeToElement(after, typeInfo),
            path,
            fields,
            topLevel: true,
            skip,
            reordered);
        return fields;
    }

    private static void Compare(JsonElement before, JsonElement after, string path, List<string> fields, bool topLevel, string? skip, bool reordered)
    {
        if (before.ValueKind != JsonValueKind.Object || after.ValueKind != JsonValueKind.Object)
        {
            if (!JsonElement.DeepEquals(before, after))
            {
                fields.Add(path);
            }

            return;
        }

        // Same contract, nulls written: both sides have the same properties in the same order.
        foreach (JsonProperty property in before.EnumerateObject())
        {
            string name = property.Name;
            if (topLevel && name == skip)
            {
                if (reordered)
                {
                    fields.Add($"{path}/{name}");
                }
            }
            else if (!(topLevel && name == VersionProperty))
            {
                Compare(property.Value, after.GetProperty(name), $"{path}/{name}", fields, topLevel: false, skip, reordered);
            }
        }
    }
}
