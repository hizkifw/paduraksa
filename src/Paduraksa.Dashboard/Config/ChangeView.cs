using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

using Paduraksa.Application.Config;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

namespace Paduraksa.Dashboard.Config;

public enum ChangedObject
{
    Document,
    Settings,
    Profile,
    Upstream,
    Destination,
    Host,
    Route,
    RateLimitPolicy,
    AccessList,
    RuleSet,
    AuthProvider,
}

/// <summary>One field of a changed object, as the diff shows it: its label and its value before and after (null: not set).</summary>
/// <param name="Path">The field's JSON pointer, relative to its object (<c>/settings/forceHttps</c>).</param>
/// <param name="Label">What to call it (<c>HTTPS redirect</c>).</param>
/// <param name="Before">The old value, formatted; null when absent (an added object or a field set only now).</param>
/// <param name="After">The new value, formatted; null when absent (a removed object or a field no longer set).</param>
public sealed record FieldChange(string Path, string Label, string? Before, string? After);

/// <summary>A changed object with the fields that changed (every field it has, for one added or removed).</summary>
/// <param name="Kind">What happened to it; null for a parent that is listed only because a child of it changed.</param>
/// <param name="What">What it is.</param>
/// <param name="Title">How to call it: a host's first name, a route's path, an upstream's name.</param>
/// <param name="Fields">The fields, in document order.</param>
/// <param name="Key">The object's ID (<c>settings</c>, <c>document</c> for those two), to keep its place in a list that changes.</param>
public sealed record ObjectChange(ConfigChangeKind? Kind, ChangedObject What, string Title, IReadOnlyList<FieldChange> Fields, string Key = "");

/// <summary>A top-level object's change with its routes' or destinations' changes under it: one card of the diff.</summary>
public sealed record ChangeGroup(ObjectChange Head, IReadOnlyList<ObjectChange> Children);

/// <summary>
/// Turns a change list (<see cref="ConfigChange"/>, from a dry run or a revision) into what the review drawer and the history show:
/// changes grouped by top-level object, each field with its value on both sides, read from the two documents. Display only: the change
/// list itself is the Application's (<c>ConfigDiff</c>).
/// </summary>
public static class ChangeView
{
    private static readonly JsonSerializerOptions Json = ConfigJsonContext.Default.Options;

    /// <summary>The groups for <paramref name="changes"/> between <paramref name="before"/> and <paramref name="after"/>.</summary>
    /// <param name="before">The config before the change.</param>
    /// <param name="after">The config after it.</param>
    /// <param name="changes">The change list between them.</param>
    /// <param name="format">How values a person reads are spelt (a server setting's span, size or count); the invariant form when null.</param>
    public static IReadOnlyList<ChangeGroup> Build(ConfigDocument before, ConfigDocument after, IReadOnlyList<ConfigChange> changes, DisplayFormat? format = null)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(changes);
        Names names = new(before, after, format ?? DisplayFormat.Invariant);
        List<ChangeGroup> groups = [];
        List<ObjectChange>? children = null;
        ObjectChange? head = null;
        string? headKey = null;

        foreach (var change in changes)
        {
            var (parentKey, parent) = change switch
            {
                RouteChange route => (route.HostId.ToString(), (ObjectChange?)Untouched(ChangedObject.Host, names.Host(route.HostId), route.HostId.ToString())),
                DestinationChange destination => (destination.UpstreamId.ToString(), Untouched(ChangedObject.Upstream, names.Upstream(destination.UpstreamId), destination.UpstreamId.ToString())),
                _ => (null, null),
            };

            var item = Describe(change, before, after, names);
            if (parentKey is null)
            {
                Flush();
                head = item;
                headKey = KeyOf(change);
                children = [.. Carried(change, before, after, names)];
                continue;
            }

            if (headKey != parentKey)
            {
                Flush();
                head = parent;
                headKey = parentKey;
                children = [];
            }

            children!.Add(item);
        }

        Flush();
        return groups;

        void Flush()
        {
            if (head is not null)
            {
                groups.Add(new ChangeGroup(head, children ?? []));
            }

            head = null;
            headKey = null;
            children = null;
        }
    }

    /// <summary>
    /// The document a command would leave, for showing its diff: the current one with the object replaced, added or removed, in canonical
    /// form. Null for a whole-document command (the caller has that document: a rollback's target revision).
    /// </summary>
    public static ConfigDocument? Apply(ConfigDocument current, ConfigCommand command)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(command);
        var next = command switch
        {
            UpsertHostCommand c => current with { Hosts = [.. current.Hosts.Where(h => h.Id != c.Host.Id), c.Host] },
            DeleteHostCommand c => current with { Hosts = [.. current.Hosts.Where(h => h.Id != c.HostId)] },
            UpsertUpstreamCommand c => current with { Upstreams = [.. current.Upstreams.Where(u => u.Id != c.Upstream.Id), c.Upstream] },
            DeleteUpstreamCommand c => current with { Upstreams = [.. current.Upstreams.Where(u => u.Id != c.UpstreamId)] },
            UpsertProfileCommand c => current with { Profiles = [.. current.Profiles.Where(p => p.Id != c.Profile.Id), c.Profile] },
            DeleteProfileCommand c => current with { Profiles = [.. current.Profiles.Where(p => p.Id != c.ProfileId)] },
            UpsertRateLimitPolicyCommand c => current with { RateLimitPolicies = [.. current.RateLimitPolicies.Where(p => p.Id != c.Policy.Id), c.Policy] },
            DeleteRateLimitPolicyCommand c => current with { RateLimitPolicies = [.. current.RateLimitPolicies.Where(p => p.Id != c.RateLimitPolicyId)] },
            UpsertAccessListCommand c => current with { AccessLists = [.. current.AccessLists.Where(l => l.Id != c.AccessList.Id), c.AccessList] },
            DeleteAccessListCommand c => current with { AccessLists = [.. current.AccessLists.Where(l => l.Id != c.AccessListId)] },
            UpsertRuleSetCommand c => current with { RuleSets = [.. current.RuleSets.Where(r => r.Id != c.RuleSet.Id), c.RuleSet] },
            DeleteRuleSetCommand c => current with { RuleSets = [.. current.RuleSets.Where(r => r.Id != c.RuleSetId)] },
            UpsertAuthProviderCommand c => current with { AuthProviders = [.. current.AuthProviders.Where(p => p.Id != c.AuthProvider.Id), c.AuthProvider] },
            DeleteAuthProviderCommand c => current with { AuthProviders = [.. current.AuthProviders.Where(p => p.Id != c.AuthProviderId)] },
            UpdateSettingsCommand c => current with { Settings = c.Settings },
            ReplaceConfigCommand c => c.Document,
            _ => null,
        };
        return next is null ? null : WithPendingPasswords(CanonicalConfigJson.Canonicalize(next), command);
    }

    /// <summary>What stands in a user's <see cref="AccessListUserConfig.Password"/> in the document a review shows, once canonical form has
    /// dropped the real one: it says "a password is being set" and is never printed.</summary>
    public const string PendingPassword = "(pending)";

    // Canonical form drops a typed password, which is also what tells a review that a user's password is changing; put the fact back.
    private static ConfigDocument WithPendingPasswords(ConfigDocument canonical, ConfigCommand command)
    {
        IEnumerable<AccessListConfig> sent = command switch
        {
            UpsertAccessListCommand c => [c.AccessList],
            ReplaceConfigCommand c => c.Document.AccessLists,
            _ => [],
        };
        Dictionary<(AccessListId, string), bool> pending = [];
        foreach (var list in sent.Where(static l => l is not null))
        {
            foreach (var user in list.BasicAuth?.Users ?? [])
            {
                if (user is { Password.Length: > 0 })
                {
                    pending[(list.Id, user.Username)] = true;
                }
            }
        }

        return pending.Count == 0
            ? canonical
            : canonical with
            {
                AccessLists =
                [
                    .. canonical.AccessLists.Select(list => list.BasicAuth is { } auth
                        ? list with
                        {
                            BasicAuth = auth with
                            {
                                Users = [.. auth.Users.Select(u => pending.ContainsKey((list.Id, u.Username)) ? u with { Password = PendingPassword } : u)],
                            },
                        }
                        : list),
                ],
            };
    }

    /// <summary>
    /// Says where a config error or warning points, in words: a document pointer (<c>/hosts/2/routes/0/path</c>) becomes
    /// "app.example.com > Route / > Path"; an object-relative one (<c>/names/1</c>) becomes "Hostname 2".
    /// </summary>
    public static string Locate(string path, ConfigDocument? document)
    {
        ArgumentNullException.ThrowIfNull(path);
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return "This change";
        }

        List<string> parts = [];
        var i = 0;
        if (document is not null && segments.Length >= 2 && int.TryParse(segments[1], CultureInfo.InvariantCulture, out var index))
        {
            switch (segments[0])
            {
                case "hosts" when index < document.Hosts.Count:
                    var host = document.Hosts[index];
                    parts.Add(HostTitle(host));
                    i = 2;
                    if (segments.Length >= 4 && segments[2] == "routes" && int.TryParse(segments[3], CultureInfo.InvariantCulture, out var route) && route < host.Routes.Count)
                    {
                        parts.Add($"Route {host.Routes[route].Path}");
                        i = 4;
                    }

                    break;
                case "upstreams" when index < document.Upstreams.Count:
                    var upstream = document.Upstreams[index];
                    parts.Add(upstream.Name);
                    i = 2;
                    if (segments.Length >= 4 && segments[2] == "destinations" && int.TryParse(segments[3], CultureInfo.InvariantCulture, out var destination) && destination < upstream.Destinations.Count)
                    {
                        parts.Add(upstream.Destinations[destination].Address);
                        i = 4;
                    }

                    break;
                case "profiles" when index < document.Profiles.Count:
                    parts.Add($"Profile {document.Profiles[index].Name}");
                    i = 2;
                    break;
                case "rateLimitPolicies" when index < document.RateLimitPolicies.Count:
                    parts.Add($"Rate limit {document.RateLimitPolicies[index].Name}");
                    i = 2;
                    break;
                case "accessLists" when index < document.AccessLists.Count:
                    parts.Add($"Access list {document.AccessLists[index].Name}");
                    i = 2;
                    break;
                case "ruleSets" when index < document.RuleSets.Count:
                    parts.Add($"Rule set {document.RuleSets[index].Name}");
                    i = 2;
                    break;
                case "authProviders" when index < document.AuthProviders.Count:
                    parts.Add($"Auth provider {document.AuthProviders[index].Name}");
                    i = 2;
                    break;
            }
        }

        for (; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (int.TryParse(segment, CultureInfo.InvariantCulture, out var position) && parts.Count > 0)
            {
                parts[^1] = $"{Singular(parts[^1])} {position + 1}";
                continue;
            }

            parts.Add(segment == "settings" && i == 0 && document is not null ? "Global settings" : FieldLabels.Of(segment));
        }

        return string.Join(" > ", parts);
    }

    /// <summary>A host as one line: its first hostname, and how many more it has.</summary>
    public static string HostTitle(HostConfig host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return host.Names.Count switch
        {
            0 => "Unnamed host",
            1 => host.Names[0],
            _ => $"{host.Names[0]} +{host.Names.Count - 1}",
        };
    }

    /// <summary>
    /// An added or removed host carries its routes, an added or removed upstream its destinations: the change list doesn't list them
    /// apart (<see cref="ConfigChange"/>), so they are shown under it from the object itself.
    /// </summary>
    private static IEnumerable<ObjectChange> Carried(ConfigChange change, ConfigDocument before, ConfigDocument after, Names names)
    {
        if (change.Kind is not (ConfigChangeKind.Added or ConfigChangeKind.Removed))
        {
            yield break;
        }

        var side = change.Kind == ConfigChangeKind.Added ? after : before;
        switch (change)
        {
            case HostChange host when side.Hosts.FirstOrDefault(h => h.Id == host.HostId) is { } config:
                foreach (var route in config.Routes)
                {
                    yield return Describe(new RouteChange(change.Kind, host.HostId, route.Id, change.Path, []), before, after, names);
                }

                break;
            case UpstreamChange upstream when side.Upstreams.FirstOrDefault(u => u.Id == upstream.UpstreamId) is { } config:
                foreach (var destination in config.Destinations)
                {
                    yield return Describe(new DestinationChange(change.Kind, upstream.UpstreamId, destination.Id, change.Path, []), before, after, names);
                }

                break;
        }
    }

    private static string Singular(string label) => label switch
    {
        "Hostnames" => "Hostname",
        "Routes" => "Route",
        "Rules" => "Rule",
        "Destinations" => "Destination",
        "Tags" => "Tag",
        "Addresses" => "Address",
        "Proxies" => "Proxy",
        _ => label,
    };

    private static ObjectChange Untouched(ChangedObject kind, string title, string key) => new(null, kind, title, [], key);

    private static string KeyOf(ConfigChange change) => change switch
    {
        HostChange c => c.HostId.ToString(),
        RouteChange c => c.RouteId.ToString(),
        UpstreamChange c => c.UpstreamId.ToString(),
        DestinationChange c => c.DestinationId.ToString(),
        ProfileChange c => c.ProfileId.ToString(),
        RateLimitPolicyChange c => c.RateLimitPolicyId.ToString(),
        AccessListChange c => c.AccessListId.ToString(),
        RuleSetChange c => c.RuleSetId.ToString(),
        AuthProviderChange c => c.AuthProviderId.ToString(),
        SettingsChange => "settings",
        _ => "document",
    };

    private static ObjectChange Describe(ConfigChange change, ConfigDocument before, ConfigDocument after, Names names)
    {
        (var kind, var title, var old, var @new, string[] skip) = change switch
        {
            HostChange c => (ChangedObject.Host, names.Host(c.HostId),
                Node(before.Hosts.FirstOrDefault(h => h.Id == c.HostId), ConfigJsonContext.Default.HostConfig),
                Node(after.Hosts.FirstOrDefault(h => h.Id == c.HostId), ConfigJsonContext.Default.HostConfig),
                (string[])["id", "version", "routes"]),
            RouteChange c => (ChangedObject.Route, names.Route(c.HostId, c.RouteId),
                Node(before.Hosts.FirstOrDefault(h => h.Id == c.HostId)?.Routes.FirstOrDefault(r => r.Id == c.RouteId), ConfigJsonContext.Default.RouteConfig),
                Node(after.Hosts.FirstOrDefault(h => h.Id == c.HostId)?.Routes.FirstOrDefault(r => r.Id == c.RouteId), ConfigJsonContext.Default.RouteConfig),
                ["id"]),
            UpstreamChange c => (ChangedObject.Upstream, names.Upstream(c.UpstreamId),
                Node(before.Upstreams.FirstOrDefault(u => u.Id == c.UpstreamId), ConfigJsonContext.Default.UpstreamConfig),
                Node(after.Upstreams.FirstOrDefault(u => u.Id == c.UpstreamId), ConfigJsonContext.Default.UpstreamConfig),
                ["id", "version", "destinations"]),
            DestinationChange c => (ChangedObject.Destination, names.Destination(c.UpstreamId, c.DestinationId),
                Node(before.Upstreams.FirstOrDefault(u => u.Id == c.UpstreamId)?.Destinations.FirstOrDefault(d => d.Id == c.DestinationId), ConfigJsonContext.Default.DestinationConfig),
                Node(after.Upstreams.FirstOrDefault(u => u.Id == c.UpstreamId)?.Destinations.FirstOrDefault(d => d.Id == c.DestinationId), ConfigJsonContext.Default.DestinationConfig),
                ["id"]),
            ProfileChange c => (ChangedObject.Profile, names.Profile(c.ProfileId),
                Node(before.Profiles.FirstOrDefault(p => p.Id == c.ProfileId), ConfigJsonContext.Default.ProfileConfig),
                Node(after.Profiles.FirstOrDefault(p => p.Id == c.ProfileId), ConfigJsonContext.Default.ProfileConfig),
                ["id", "version"]),
            RateLimitPolicyChange c => (ChangedObject.RateLimitPolicy, names.RateLimitPolicy(c.RateLimitPolicyId),
                Node(before.RateLimitPolicies.FirstOrDefault(p => p.Id == c.RateLimitPolicyId), ConfigJsonContext.Default.RateLimitPolicyConfig),
                Node(after.RateLimitPolicies.FirstOrDefault(p => p.Id == c.RateLimitPolicyId), ConfigJsonContext.Default.RateLimitPolicyConfig),
                ["id", "version"]),
            AccessListChange c => (ChangedObject.AccessList, names.AccessList(c.AccessListId),
                Node(before.AccessLists.FirstOrDefault(l => l.Id == c.AccessListId), ConfigJsonContext.Default.AccessListConfig),
                Node(after.AccessLists.FirstOrDefault(l => l.Id == c.AccessListId), ConfigJsonContext.Default.AccessListConfig),
                ["id", "version"]),
            RuleSetChange c => (ChangedObject.RuleSet, names.RuleSet(c.RuleSetId),
                Node(before.RuleSets.FirstOrDefault(r => r.Id == c.RuleSetId), ConfigJsonContext.Default.RuleSetConfig),
                Node(after.RuleSets.FirstOrDefault(r => r.Id == c.RuleSetId), ConfigJsonContext.Default.RuleSetConfig),
                ["id", "version"]),
            AuthProviderChange c => (ChangedObject.AuthProvider, names.AuthProvider(c.AuthProviderId),
                Node(before.AuthProviders.FirstOrDefault(p => p.Id == c.AuthProviderId), ConfigJsonContext.Default.AuthProviderConfig),
                Node(after.AuthProviders.FirstOrDefault(p => p.Id == c.AuthProviderId), ConfigJsonContext.Default.AuthProviderConfig),
                ["id", "version"]),
            SettingsChange => (ChangedObject.Settings, "Global settings",
                Node(before.Settings, ConfigJsonContext.Default.GlobalSettings),
                Node(after.Settings, ConfigJsonContext.Default.GlobalSettings),
                ["version"]),
            _ => (ChangedObject.Document, "Config format", null, null, []),
        };

        List<FieldChange> fields = [];
        if (change.Kind == ConfigChangeKind.Modified)
        {
            foreach (var field in change.Fields)
            {
                var relative = field.StartsWith(change.Path, StringComparison.Ordinal) ? field[change.Path.Length..] : field;
                if (relative.Length == 0 || (skip.Contains(relative.TrimStart('/'), StringComparer.Ordinal) && relative != "/routes"))
                {
                    continue;
                }

                if (change is SettingsChange && (relative == "/server" || relative.StartsWith("/server/", StringComparison.Ordinal)))
                {
                    // One row per server setting, as its kind reads, with the value in force on each side (a cleared one is its default).
                    if (!fields.Any(f => f.Path.StartsWith("/server/", StringComparison.Ordinal)))
                    {
                        fields.AddRange(names.ServerSettings(before.Settings.Server, after.Settings.Server));
                    }

                    continue;
                }

                if (AccessListUsers.Handles(relative))
                {
                    var (was, now) = AccessListUsers.Describe(relative, old, @new);
                    fields.Add(new FieldChange(relative, LabelOf(relative), was, now));
                    continue;
                }

                fields.Add(new FieldChange(relative, LabelOf(relative), names.Format(relative, Resolve(old, relative)), names.Format(relative, Resolve(@new, relative))));
            }
        }
        else
        {
            var side = (change.Kind == ConfigChangeKind.Added ? @new : old) ?? [];
            foreach (var (key, value) in side)
            {
                // What an added or removed object sets: its defaults (a switch left off, an empty list) say nothing.
                if (skip.Contains(key, StringComparer.Ordinal) || value is null or JsonArray { Count: 0 } or JsonObject { Count: 0 }
                    || (value is JsonValue flag && flag.TryGetValue(out bool set) && !set)
                    || (value is JsonValue number && number.TryGetValue(out int zero) && zero == 0))
                {
                    continue;
                }

                var path = "/" + key;
                var formatted = names.Format(path, value);
                fields.Add(change.Kind == ConfigChangeKind.Added
                    ? new FieldChange(path, LabelOf(path), null, formatted)
                    : new FieldChange(path, LabelOf(path), formatted, null));
            }
        }

        return new ObjectChange(change.Kind, kind, title, fields, KeyOf(change));
    }

    private static string LabelOf(string relative)
    {
        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments is ["routes"])
        {
            return "Route order";
        }

        return string.Join(" > ", segments.Where(s => s != "settings" || segments.Length == 1).Select(FieldLabels.Of));
    }

    private static JsonObject? Node<T>(T? value, JsonTypeInfo<T> type)
        where T : class =>
        value is null ? null : JsonSerializer.SerializeToNode(value, type) as JsonObject;

    private static JsonNode? Resolve(JsonObject? root, string pointer)
    {
        JsonNode? node = root;
        foreach (var segment in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            node = node switch
            {
                JsonObject o => o[segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)],
                JsonArray a when int.TryParse(segment, CultureInfo.InvariantCulture, out var i) && i < a.Count => a[i],
                _ => null,
            };
            if (node is null)
            {
                return null;
            }
        }

        return node;
    }

    private static RuleConfig? TryRule(JsonObject node)
    {
        try
        {
            return node.Deserialize(ConfigJsonContext.Default.RuleConfig);
        }
        catch (JsonException)
        {
            // Not a rule after all (some other object with these two keys): shown as any object is.
            return null;
        }
    }

    /// <summary>Names of the objects either document has, to show references (an upstream ID) and titles by name.</summary>
    private sealed class Names(ConfigDocument before, ConfigDocument after, DisplayFormat format)
    {
        /// <summary>The server settings whose stored value differs, each read through its declaration: "Traffic and security history: 90 days to 7 days".</summary>
        public IEnumerable<FieldChange> ServerSettings(ServerSettings? old, ServerSettings? @new)
        {
            old ??= new();
            @new ??= new();
            foreach (var setting in Application.Settings.ServerSettingsChange.Changed(old, @new))
            {
                var was = format.ServerSetting(setting, setting.ResolvedText(old));
                var now = format.ServerSetting(setting, setting.ResolvedText(@new));
                if (was == now)
                {
                    // Pinned to its default or left to it again: the same words on both sides, so the side that leaves it unset says so.
                    was = setting.IsSet(old) ? was : was + " (default)";
                    now = setting.IsSet(@new) ? now : now + " (default)";
                }

                yield return new FieldChange("/server/" + setting.Key, setting.Title, was, now);
            }
        }

        public string Host(HostId id) =>
            after.Hosts.FirstOrDefault(h => h.Id == id) is { } host ? HostTitle(host)
            : before.Hosts.FirstOrDefault(h => h.Id == id) is { } old ? HostTitle(old)
            : id.ToString();

        public string Route(HostId host, RouteId id) =>
            (after.Hosts.FirstOrDefault(h => h.Id == host)?.Routes.FirstOrDefault(r => r.Id == id)
                ?? before.Hosts.FirstOrDefault(h => h.Id == host)?.Routes.FirstOrDefault(r => r.Id == id)) is { } route
                ? $"Route {route.Path}"
                : "Route";

        public string Upstream(UpstreamId id) =>
            (after.Upstreams.FirstOrDefault(u => u.Id == id) ?? before.Upstreams.FirstOrDefault(u => u.Id == id))?.Name ?? id.ToString();

        public string Destination(UpstreamId upstream, DestinationId id) =>
            (after.Upstreams.FirstOrDefault(u => u.Id == upstream)?.Destinations.FirstOrDefault(d => d.Id == id)
                ?? before.Upstreams.FirstOrDefault(u => u.Id == upstream)?.Destinations.FirstOrDefault(d => d.Id == id))?.Address ?? "Destination";

        public string Profile(ProfileId id) =>
            (after.Profiles.FirstOrDefault(p => p.Id == id) ?? before.Profiles.FirstOrDefault(p => p.Id == id))?.Name ?? id.ToString();

        public string AccessList(AccessListId id) =>
            (after.AccessLists.FirstOrDefault(l => l.Id == id) ?? before.AccessLists.FirstOrDefault(l => l.Id == id))?.Name ?? "a list that isn't here";

        public string RuleSet(RuleSetId id) =>
            (after.RuleSets.FirstOrDefault(r => r.Id == id) ?? before.RuleSets.FirstOrDefault(r => r.Id == id))?.Name
            ?? (RuleSetPresets.TryGet(id, out var preset) ? preset.Name : "a set that isn't here");

        public string AuthProvider(AuthProviderId id) =>
            (after.AuthProviders.FirstOrDefault(p => p.Id == id) ?? before.AuthProviders.FirstOrDefault(p => p.Id == id))?.Name ?? "a provider that isn't here";

        public string RateLimitPolicy(RateLimitPolicyId id) =>
            (after.RateLimitPolicies.FirstOrDefault(p => p.Id == id) ?? before.RateLimitPolicies.FirstOrDefault(p => p.Id == id))?.Name ?? "a policy that isn't here";

        /// <summary>A value as one line: references by name, switches as on/off, lists comma-separated, nested settings as key: value.</summary>
        public string? Format(string path, JsonNode? value)
        {
            switch (value)
            {
                case null:
                    return null;
                case JsonArray rules when rules.Count > 0 && rules.All(static item => item is JsonObject rule && rule.ContainsKey("action") && rule.ContainsKey("match")):
                    // A rule set's rules, numbered the way the editor numbers them: a rule's own sentence has commas, so they can't be
                    // told apart by a comma between them.
                    return string.Join("; ", rules.Select((item, index) => $"{index + 1}. {Format(path, item)}"));
                case JsonArray array:
                    return array.Count == 0 ? "none" : string.Join(", ", array.Select(item => Format(path, item) ?? "-"));
                case JsonObject rule when rule.ContainsKey("action") && rule.ContainsKey("match") && TryRule(rule) is { } parsed:
                    // A rule as the sentence the editor shows: "Deny when path starts with /admin and method is POST".
                    return RuleSetText.Describe(parsed);
                case JsonObject auth when path.EndsWith("/basicAuth", StringComparison.Ordinal) && auth.ContainsKey("users"):
                    // An access list's passwords as an object of its own (the list added or removed): the realm, and the users by name.
                    return AccessListUsers.BasicAuth(auth, null, isAfter: true);
                case JsonObject user when user.ContainsKey("username") && (user.ContainsKey("credentialId") || user.ContainsKey("password")):
                    // A user of an access list: the name, and that a password is being set. The typed password is never shown, in a
                    // review or in the history, and the vault's credential ID says nothing a reader needs.
                    return string.Concat(user["username"]?.ToString() ?? "-", user.ContainsKey("password") ? " (new password)" : string.Empty);
                case JsonValue word when path.EndsWith("/satisfy", StringComparison.Ordinal) && word.TryGetValue(out string? satisfy):
                    return satisfy == "any" ? "Address or password" : "Address and password";
                case JsonObject obj:
                    StringBuilder text = new();
                    foreach (var (key, item) in obj)
                    {
                        if (item is null)
                        {
                            continue;
                        }

                        if (text.Length > 0)
                        {
                            text.Append(" · ");
                        }

                        text.Append(FieldLabels.Of(key)).Append(": ").Append(Format(path + "/" + key, item));
                    }

                    return text.Length == 0 ? "set" : text.ToString();
                case JsonValue scalar when scalar.TryGetValue(out bool flag):
                    return flag ? "on" : "off";
                case JsonValue scalar when scalar.TryGetValue(out string? word):
                    return Reference(word) ?? FieldLabels.Value(word);
                default:
                    return value.ToJsonString(Json);
            }
        }

        private string? Reference(string text)
        {
            if (UpstreamId.TryParse(text, out var upstream))
            {
                return Upstream(upstream);
            }

            if (ProfileId.TryParse(text, out var profile))
            {
                return $"Profile {Profile(profile)}";
            }

            if (RateLimitPolicyId.TryParse(text, out var policy))
            {
                return RateLimitPolicy(policy);
            }

            if (AccessListId.TryParse(text, out var list))
            {
                return AccessList(list);
            }

            if (RuleSetId.TryParse(text, out var set))
            {
                return RuleSet(set);
            }

            if (AuthProviderId.TryParse(text, out var provider))
            {
                return AuthProvider(provider);
            }

            return null;
        }
    }
}
