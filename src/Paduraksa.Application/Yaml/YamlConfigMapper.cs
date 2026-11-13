using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Paduraksa.Application.Commands;
using Paduraksa.Application.Config;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Ids;
using Paduraksa.Core.Model;

namespace Paduraksa.Application.Yaml;

/// <summary>
/// What a YAML document maps to: the document it describes (only what the YAML names; see <see cref="ConfigYamlMapper"/>), where each of its pieces sits in the
/// text, and which top-level sections it wrote.
/// </summary>
/// <param name="Document">The objects the YAML names, with IDs assigned or matched and every reference resolved; lists the YAML doesn't have are empty and global settings it doesn't have are the defaults, so <paramref name="HasSettings"/> says which.</param>
/// <param name="HasSettings">The YAML has a <c>settings</c> key.</param>
/// <param name="Positions">The line and column of every node, by its YAML pointer.</param>
internal sealed record MappedYaml(ConfigDocument Document, bool HasSettings, IReadOnlyDictionary<string, (long Line, long Column)> Positions);

/// <summary>Everything a YAML document can fail on before the config validator sees it, with pointers into the YAML.</summary>
internal sealed record YamlMapResult(MappedYaml? Mapped, IReadOnlyList<ConfigError> Errors)
{
    public bool Succeeded => Mapped is not null;
}

/// <summary>
/// Maps a YAML document onto the canonical <see cref="ConfigDocument"/> (docs/design/admin-surfaces.md "YAML import and export"). It reads the tree against <see cref="ConfigYamlSchema"/> (unknown keys, wrong shapes and
/// missing keys are errors with a pointer and a line), gives every object that has no <c>id</c> the ID of the stored object it names (so importing the same file twice is
/// no change) or a new one, resolves each reference by name to that object's ID, and hands the JSON it built to the document's own strict reader for the values themselves
/// (enum names, ID prefixes, time spans, number ranges). Nothing is ignored: what the YAML says is what the document gets.
/// </summary>
/// <remarks>
/// <para><b>Identity.</b> An object with an <c>id</c> is that object. Without one it is the stored object with the same name (a host: any stored host that serves the first
/// name it lists; a route: the same match and path on the stored host; a destination: the same address on the stored upstream), unless another entry claims that
/// object by <c>id</c> or came first. Anything else is new. A rename therefore needs the <c>id</c>.</para>
/// <para><b>References.</b> The value is the referenced object's name (ignoring case), else its ID; a name is looked up among the objects the YAML names and, when the import
/// merges, the stored objects it leaves alone. The built-in rule-set presets can be named too. A name two objects share is ambiguous, not a guess. An ID is taken as it is even when no
/// object has it: a valid document can name an object that isn't there in a layer nothing uses, and exporting it must read back; the validator refuses one that matters.</para>
/// </remarks>
internal static class ConfigYamlMapper
{
    public const string KindKey = "kind";
    public const string KindValue = "Config";
    private const int MaxErrors = 100;

    // The top-level lists of objects with an identity, by the document's JSON name.
    private static readonly ObjectList[] Lists =
    [
        new("profiles", ReferenceKind.Profile, typeof(ProfileId), static d => d.Profiles.Select(p => new StoredObject(p.Id.ToString(), p.Name, [p.Name]))),
        new("upstreams", ReferenceKind.Upstream, typeof(UpstreamId), static d => d.Upstreams.Select(u => new StoredObject(u.Id.ToString(), u.Name, [u.Name]))),
        new("rateLimitPolicies", ReferenceKind.RateLimitPolicy, typeof(RateLimitPolicyId), static d => d.RateLimitPolicies.Select(p => new StoredObject(p.Id.ToString(), p.Name, [p.Name]))),
        new("accessLists", ReferenceKind.AccessList, typeof(AccessListId), static d => d.AccessLists.Select(l => new StoredObject(l.Id.ToString(), l.Name, [l.Name]))),
        new("ruleSets", ReferenceKind.RuleSet, typeof(RuleSetId), static d => d.RuleSets.Select(r => new StoredObject(r.Id.ToString(), r.Name, [r.Name]))),
        new("authProviders", ReferenceKind.AuthProvider, typeof(AuthProviderId), static d => d.AuthProviders.Select(p => new StoredObject(p.Id.ToString(), p.Name, [p.Name]))),
        new("hosts", null, typeof(HostId), static d => d.Hosts.Select(h => new StoredObject(h.Id.ToString(), h.Names.Count > 0 ? h.Names[0] : string.Empty, h.Names))),
    ];

    /// <param name="yaml">The tree to map.</param>
    /// <param name="stored">The config the import is applied to, for identities and (when <paramref name="merging"/>) references; null: none (the empty config).</param>
    /// <param name="merging">The stored objects the YAML doesn't name stay, so references may name them.</param>
    /// <param name="newId">A new ID for an object of the given ID type.</param>
    public static YamlMapResult Map(YamlNode yaml, ConfigDocument? stored, bool merging, Func<Type, string> newId)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentNullException.ThrowIfNull(newId);
        Mapping mapping = new(stored ?? ConfigService.InitialDocument, merging, newId);
        return mapping.Run(yaml);
    }

    private sealed record ObjectList(string Key, ReferenceKind? Reference, Type IdType, Func<ConfigDocument, IEnumerable<StoredObject>> StoredOf);

    private sealed record StoredObject(string Id, string Name, IReadOnlyList<string> Keys);

    private sealed record ReferenceSite(JsonObject Container, string JsonName, ReferenceKind Kind, Type IdType, string Text, string Pointer);

    private sealed class Mapping(ConfigDocument stored, bool merging, Func<Type, string> newId)
    {
        private readonly List<ConfigError> _errors = [];
        private readonly Dictionary<string, (long Line, long Column)> _positions = new(StringComparer.Ordinal);
        private readonly List<ReferenceSite> _references = [];
        private bool _truncated;

        public YamlMapResult Run(YamlNode root)
        {
            if (root is not YamlMapping map)
            {
                Error(string.Empty, root, YamlErrorCodes.TypeMismatch, "The YAML must be a mapping with apiVersion, kind and the config's sections.");
                return Finish(null);
            }

            Position(string.Empty, map);
            CheckHeader(map);
            var json = ConvertObject(map, ConfigYamlSchema.Document, string.Empty, skipKeys: [KindKey]) ?? new JsonObject();
            if (_errors.Count > 0)
            {
                return Finish(null);
            }

            AssignIdentities(json);
            if (_errors.Count > 0)
            {
                return Finish(null);
            }

            Resolve(json);
            if (_errors.Count > 0)
            {
                return Finish(null);
            }

            ConfigDocument document;
            try
            {
                document = JsonSerializer.Deserialize(json.ToJsonString(), ConfigJsonContext.Default.ConfigDocument)
                           ?? throw new JsonException("empty");
            }
            catch (JsonException ex)
            {
                var pointer = CanonicalConfigJson.ToJsonPointer(ex.Path);
                Error(pointer, null, YamlErrorCodes.ValueInvalid, "The value isn't valid here: an enum name, an ID, a time span or a number out of range.");
                return Finish(null);
            }

            // A null (or empty) value is no key, as everywhere else: a merge must not reset the settings for `settings: null`.
            var settings = map.Find("settings")?.Value;
            return Finish(new MappedYaml(document, settings is not null and not YamlScalar { Plain: true, Text: "" or "~" or "null" or "Null" or "NULL" }, _positions));
        }

        private YamlMapResult Finish(MappedYaml? mapped)
        {
            if (_truncated)
            {
                _errors.Add(new ConfigError(string.Empty, YamlErrorCodes.ValueInvalid, $"There are more errors; only the first {MaxErrors} are shown."));
            }

            return new YamlMapResult(_errors.Count > 0 ? null : mapped, _errors);
        }

        private void CheckHeader(YamlMapping map)
        {
            if (map.Find(KindKey) is not { Value: YamlScalar kind } || !string.Equals(kind.Text, KindValue, StringComparison.Ordinal))
            {
                Error("/" + KindKey, map.Find(KindKey)?.Value ?? map, YamlErrorCodes.Header, $"The YAML must say 'kind: {KindValue}'.");
            }

            if (map.Find("apiVersion") is not { Value: YamlScalar version } || !string.Equals(version.Text, ConfigDocument.V1, StringComparison.Ordinal))
            {
                Error("/apiVersion", map.Find("apiVersion")?.Value ?? map, ConfigErrorCodes.ApiVersionUnsupported, $"The YAML must say 'apiVersion: {ConfigDocument.V1}'.");
            }
        }

        // ---- The tree against the schema ----

        private JsonNode? Convert(YamlNode node, SchemaNode schema, string pointer)
        {
            Position(pointer, node);
            switch (schema.Kind)
            {
                case SchemaKind.Object:
                    return ConvertObject(node, schema, pointer, skipKeys: []);
                case SchemaKind.List:
                    return ConvertList(node, schema, pointer);
                default:
                    break;
            }

            if (node is not YamlScalar scalar)
            {
                Error(pointer, node, YamlErrorCodes.TypeMismatch, $"Expected {Describe(schema)}, found a {(node is YamlMapping ? "mapping" : "list")}.");
                return null;
            }

            switch (schema.Kind)
            {
                case SchemaKind.Text:
                    return JsonValue.Create(scalar.Text);
                case SchemaKind.Boolean when YamlScalars.TryBool(scalar, out var flag):
                    return JsonValue.Create(flag);
                case SchemaKind.Integer when YamlScalars.TryInteger(scalar, out var integer):
                    return JsonValue.Create(integer);
                case SchemaKind.Number when YamlScalars.TryNumber(scalar, out var number):
                    return JsonValue.Create(number);
                default:
                    Error(pointer, node, YamlErrorCodes.TypeMismatch, $"Expected {Describe(schema)}, found '{Abbreviate(scalar.Text)}'.");
                    return null;
            }
        }

        private JsonObject? ConvertObject(YamlNode node, SchemaNode schema, string pointer, string[] skipKeys)
        {
            if (node is not YamlMapping mapping)
            {
                Error(pointer, node, YamlErrorCodes.TypeMismatch, $"Expected a mapping ({KeysOf(schema)}), found {(node is YamlSequence ? "a list" : "a value")}.");
                return null;
            }

            Position(pointer, node);
            JsonObject result = new();
            HashSet<string> present = new(StringComparer.Ordinal);
            foreach (var entry in mapping.Entries)
            {
                if (Array.IndexOf(skipKeys, entry.Key) >= 0)
                {
                    continue;
                }

                var childPointer = pointer + "/" + Escape(entry.Key);
                _positions[childPointer] = (entry.KeyLine, entry.KeyColumn);
                if (schema.ByYamlName(entry.Key) is not { } property)
                {
                    Error(childPointer, null, YamlErrorCodes.KeyUnknown, UnknownKey(schema, entry.Key));
                    continue;
                }

                if (entry.Value is YamlScalar scalar && YamlScalars.IsNull(scalar))
                {
                    continue;
                }

                present.Add(property.YamlName);
                if (property.Reference is { } kind)
                {
                    Position(childPointer, entry.Value);
                    if (entry.Value is YamlScalar reference)
                    {
                        _references.Add(new ReferenceSite(result, property.JsonName, kind, property.Node.Type, reference.Text.Trim(), childPointer));
                        result[property.JsonName] = null;
                    }
                    else
                    {
                        Error(childPointer, entry.Value, YamlErrorCodes.TypeMismatch, $"Expected the name of a {Label(kind)}.");
                    }

                    continue;
                }

                if (property.IsId)
                {
                    if (entry.Value is YamlScalar id && IsValidId(property.Node.Type, id.Text))
                    {
                        Position(childPointer, entry.Value);
                        result[property.JsonName] = JsonValue.Create(id.Text);
                    }
                    else
                    {
                        Error(childPointer, entry.Value, YamlErrorCodes.IdInvalid, $"Not a valid ID here (it starts with '{IdPrefix(property.Node.Type)}_'); leave 'id' out to have one assigned.");
                    }

                    continue;
                }

                if (Convert(entry.Value, property.Node, childPointer) is { } child)
                {
                    result[property.JsonName] = child;
                }
            }

            foreach (var property in schema.Properties)
            {
                if (property.Required && !present.Contains(property.YamlName) && (pointer.Length > 0 || property.YamlName != "apiVersion"))
                {
                    Error(pointer, node, YamlErrorCodes.KeyRequired, $"Missing '{property.YamlName}'.");
                }
            }

            return result;
        }

        private JsonArray? ConvertList(YamlNode node, SchemaNode schema, string pointer)
        {
            if (node is not YamlSequence sequence)
            {
                Error(pointer, node, YamlErrorCodes.TypeMismatch, $"Expected a list, found {(node is YamlMapping ? "a mapping" : "a value")}.");
                return null;
            }

            Position(pointer, node);
            JsonArray result = new();
            for (var i = 0; i < sequence.Items.Count; i++)
            {
                var itemPointer = pointer + "/" + i.ToString(CultureInfo.InvariantCulture);
                if (sequence.Items[i] is YamlScalar scalar && YamlScalars.IsNull(scalar))
                {
                    Error(itemPointer, scalar, YamlErrorCodes.TypeMismatch, "A list entry can't be empty.");
                    continue;
                }

                result.Add(Convert(sequence.Items[i], schema.Element!, itemPointer));
            }

            return result;
        }

        // ---- Identity: which stored object each entry is ----

        private void AssignIdentities(JsonObject root)
        {
            foreach (var list in Lists)
            {
                if (root[list.Key] is not JsonArray array)
                {
                    continue;
                }

                var storedByKey = IndexByKey(list, list.StoredOf(stored));
                var claimed = new Dictionary<string, int>(StringComparer.Ordinal);
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonObject { } entry && entry["id"]?.GetValue<string>() is { } id && !claimed.TryAdd(id, i))
                    {
                        Error($"/{list.Key}/{i}/id", null, YamlErrorCodes.Duplicate, $"The id is already used by entry {claimed[id]}.");
                    }
                }

                var taken = new HashSet<string>(claimed.Keys, StringComparer.Ordinal);
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is not JsonObject entry || entry["id"] is not null)
                    {
                        continue;
                    }

                    var key = NaturalKey(list, entry);
                    var candidates = key.Length > 0 && storedByKey.TryGetValue(key, out var named) ? named.Where(o => !taken.Contains(o.Id)).ToList() : [];
                    if (candidates.Count > 1)
                    {
                        // A disabled host may share a name with an enabled one: which of them this entry is can't be guessed.
                        Error($"/{list.Key}/{i}", null, YamlErrorCodes.ReferenceAmbiguous, $"More than one stored {(list.Key == "hosts" ? "host serves" : "object is named")} '{Abbreviate(key)}'; add the id of the one this entry is.");
                        continue;
                    }

                    var match = candidates.Count == 1 ? candidates[0] : null;
                    var id = match?.Id ?? newId(list.IdType);
                    taken.Add(id);
                    entry["id"] = id;
                }
            }

            AssignChildIdentities(root);
        }

        // Routes by (match, path) on the stored host, destinations by address on the stored upstream; the IDs of all routes and of all destinations are unique across the document.
        private void AssignChildIdentities(JsonObject root)
        {
            AssignChildren(root, "hosts", "routes", typeof(RouteId), static d => d.Hosts.ToDictionary(static h => h.Id.ToString(), static h => h.Routes.Select(static r => (r.Id.ToString(), RouteKey(r.Match, r.Path))).ToList()), RouteKey);
            AssignChildren(root, "upstreams", "destinations", typeof(DestinationId), static d => d.Upstreams.ToDictionary(static u => u.Id.ToString(), static u => u.Destinations.Select(static dest => (dest.Id.ToString(), AddressKey(dest.Address))).ToList()), static c => AddressKey(c["address"]?.GetValue<string>()));
        }

        private void AssignChildren(
            JsonObject root, string listKey, string childKey, Type childIdType,
            Func<ConfigDocument, Dictionary<string, List<(string Id, string Key)>>> storedChildren, Func<JsonObject, string> keyOf)
        {
            if (root[listKey] is not JsonArray array)
            {
                return;
            }

            var storedByParent = storedChildren(stored);
            var seen = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < array.Count; i++)
            {
                if (array[i] is not JsonObject { } parent || parent[childKey] is not JsonArray children)
                {
                    continue;
                }

                for (var j = 0; j < children.Count; j++)
                {
                    if (children[j] is JsonObject child && child["id"]?.GetValue<string>() is { } id && !seen.TryAdd(id, $"/{listKey}/{i}/{childKey}/{j}"))
                    {
                        Error($"/{listKey}/{i}/{childKey}/{j}/id", null, YamlErrorCodes.Duplicate, $"The id is already used at {seen[id]}.");
                    }
                }
            }

            for (var i = 0; i < array.Count; i++)
            {
                if (array[i] is not JsonObject { } parent || parent[childKey] is not JsonArray children)
                {
                    continue;
                }

                var storedChildrenOfParent = parent["id"]?.GetValue<string>() is { } parentId && storedByParent.TryGetValue(parentId, out var known) ? known : [];
                for (var j = 0; j < children.Count; j++)
                {
                    if (children[j] is not JsonObject child || child["id"] is not null)
                    {
                        continue;
                    }

                    var key = keyOf(child);
                    var match = storedChildrenOfParent.FirstOrDefault(c => !seen.ContainsKey(c.Id) && string.Equals(c.Key, key, StringComparison.Ordinal));
                    var id = match.Id ?? newId(childIdType);
                    seen[id] = $"/{listKey}/{i}/{childKey}/{j}";
                    child["id"] = id;
                }
            }
        }

        private static string RouteKey(JsonObject route) =>
            RouteKey(
                route["match"]?.GetValue<string>() is { } match && Enum.TryParse<RouteMatchKind>(match, ignoreCase: true, out var kind) ? kind : RouteMatchKind.Prefix,
                route["path"]?.GetValue<string>());

        private static string RouteKey(RouteMatchKind match, string? path) => (match == RouteMatchKind.Exact ? "exact " : "prefix ") + (path ?? "/").ToLowerInvariant();

        private static string AddressKey(string? address) => (address ?? string.Empty).Trim().ToLowerInvariant();

        // What identifies an entry: a host by its first name, anything else by its name; compared canonical for a host name and ignoring case for a name.
        private static string NaturalKey(ObjectList list, JsonObject entry) =>
            list.Key == "hosts"
                ? entry["names"] is JsonArray { Count: > 0 } names && names[0]?.GetValue<string>() is { } first ? Keyed(list, first) : string.Empty
                : Keyed(list, entry["name"]?.GetValue<string>());

        private static string Keyed(ObjectList list, string? text) =>
            list.Key == "hosts" ? ConfigNormalization.CanonicalHostName(text) : ConfigNormalization.NormalizeName(text).ToUpperInvariant();

        // The stored objects by every key they answer to, in stored order, so matching an entry is a lookup and not a scan of the stored list.
        private static Dictionary<string, List<StoredObject>> IndexByKey(ObjectList list, IEnumerable<StoredObject> stored)
        {
            Dictionary<string, List<StoredObject>> index = new(StringComparer.Ordinal);
            foreach (var storedObject in stored)
            {
                foreach (var key in storedObject.Keys.Select(k => Keyed(list, k)).Distinct(StringComparer.Ordinal))
                {
                    if (!index.TryGetValue(key, out var objects))
                    {
                        index[key] = objects = [];
                    }

                    objects.Add(storedObject);
                }
            }

            return index;
        }

        // ---- References: a name becomes the ID of the object it names ----

        private void Resolve(JsonObject root)
        {
            if (_references.Count == 0)
            {
                return;
            }

            Dictionary<ReferenceKind, Dictionary<string, List<string>>> byName = [];
            Dictionary<ReferenceKind, HashSet<string>> ids = [];
            foreach (var kind in Enum.GetValues<ReferenceKind>())
            {
                byName[kind] = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                ids[kind] = new HashSet<string>(StringComparer.Ordinal);
            }

            void Add(ReferenceKind kind, string id, string name)
            {
                if (!ids[kind].Add(id))
                {
                    return;
                }

                var key = ConfigNormalization.NormalizeName(name);
                if (!byName[kind].TryGetValue(key, out var known))
                {
                    byName[kind][key] = known = [];
                }

                known.Add(id);
            }

            var imported = new HashSet<string>(StringComparer.Ordinal);
            foreach (var list in Lists.Where(static l => l.Reference is not null))
            {
                if (root[list.Key] is JsonArray array)
                {
                    foreach (var entry in array.OfType<JsonObject>())
                    {
                        var id = entry["id"]!.GetValue<string>();
                        imported.Add(id);
                        Add(list.Reference!.Value, id, entry["name"]?.GetValue<string>() ?? string.Empty);
                    }
                }

                if (merging)
                {
                    foreach (var storedObject in list.StoredOf(stored).Where(o => !imported.Contains(o.Id)))
                    {
                        Add(list.Reference!.Value, storedObject.Id, storedObject.Name);
                    }
                }
            }

            foreach (var preset in RuleSetPresets.All)
            {
                Add(ReferenceKind.RuleSet, preset.Id.ToString(), preset.Name);
            }

            foreach (var site in _references)
            {
                if (site.Text.Length > 0 && byName[site.Kind].TryGetValue(site.Text, out var named))
                {
                    if (named.Count > 1)
                    {
                        Error(site.Pointer, null, YamlErrorCodes.ReferenceAmbiguous, $"More than one {Label(site.Kind)} is named '{Abbreviate(site.Text)}'; name it by its id.");
                        continue;
                    }

                    site.Container[site.JsonName] = named[0];
                }
                else if (ids[site.Kind].Contains(site.Text) || IsValidId(site.IdType, site.Text))
                {
                    // An ID: of an object in scope, or of none (a layer nothing uses can name an object that is gone; the validator judges one that is used).
                    site.Container[site.JsonName] = site.Text;
                }
                else
                {
                    Error(site.Pointer, null, YamlErrorCodes.ReferenceUnknown, $"There is no {Label(site.Kind)} named '{Abbreviate(site.Text)}'" + (merging ? " in the YAML or the stored config." : " in the YAML."));
                }
            }
        }

        // ---- Helpers ----

        private void Position(string pointer, YamlNode node) => _positions[pointer] = (node.Line, node.Column);

        private void Error(string pointer, YamlNode? at, string code, string message)
        {
            if (_errors.Count >= MaxErrors)
            {
                _truncated = true;
                return;
            }

            var (line, column) = at is not null ? (at.Line, at.Column) : _positions.TryGetValue(pointer, out var known) ? known : (0, 0);
            _errors.Add(new ConfigError(pointer, code, line > 0 ? $"{message} (line {line}, column {column})" : message));
        }

        private static string UnknownKey(SchemaNode schema, string key)
        {
            if (string.Equals(key, ConfigYamlSchema.VersionKey, StringComparison.Ordinal))
            {
                return "'version' isn't a key: Paduraksa assigns versions on every change. Remove it.";
            }

            if (ConfigYamlSchema.IsPassword(schema.Type, key))
            {
                return "A password never travels in YAML. Set it in the dashboard and name the stored one with 'credentialId'.";
            }

            if (string.Equals(key, KindKey, StringComparison.Ordinal))
            {
                return "'kind' is only the top-level key.";
            }

            foreach (var property in schema.Properties)
            {
                if (string.Equals(property.JsonName, key, StringComparison.Ordinal) && property.YamlName != key)
                {
                    return $"Write '{property.YamlName}' with the name of the object, not its ID.";
                }
            }

            var near = schema.Properties.FirstOrDefault(p => string.Equals(p.YamlName, key, StringComparison.OrdinalIgnoreCase))
                       ?? schema.Properties.FirstOrDefault(p => Distance(p.YamlName, key) <= 2);
            return near is not null
                ? $"Unknown key '{Abbreviate(key)}'; did you mean '{near.YamlName}'?"
                : $"Unknown key '{Abbreviate(key)}' ({KeysOf(schema)}).";
        }

        private static string KeysOf(SchemaNode schema) =>
            schema.Properties.Count <= 14 ? "keys: " + string.Join(", ", schema.Properties.Select(static p => p.YamlName)) : "see the config reference for its keys";

        private static string Describe(SchemaNode schema) => schema.Kind switch
        {
            SchemaKind.Boolean => "true or false",
            SchemaKind.Integer => "a whole number",
            SchemaKind.Number => "a number",
            SchemaKind.Text => "text",
            SchemaKind.List => "a list",
            _ => "a mapping",
        };

        private static string Label(ReferenceKind kind) => kind switch
        {
            ReferenceKind.Profile => "profile",
            ReferenceKind.Upstream => "upstream",
            ReferenceKind.RateLimitPolicy => "rate-limit policy",
            ReferenceKind.AccessList => "access list",
            ReferenceKind.RuleSet => "rule set",
            _ => "auth provider",
        };

        private static string IdPrefix(Type idType) =>
            idType.GetCustomAttributes(typeof(TypedIdAttribute), inherit: false) is [TypedIdAttribute attribute] ? attribute.Prefix : idType.Name;

        private static string Abbreviate(string text) => text.Length <= 60 ? text : text[..57] + "...";

        private static string Escape(string key) => key.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

        private static bool IsValidId(Type idType, string text)
        {
            try
            {
                return ConfigJsonContext.Default.GetTypeInfo(idType) is { } info
                       && JsonSerializer.Deserialize(JsonSerializer.Serialize(text), info) is not null;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        // A small edit distance, for "did you mean": two edits cover a typo or a transposition in a key of this vocabulary.
        private static int Distance(string a, string b)
        {
            if (Math.Abs(a.Length - b.Length) > 2)
            {
                return int.MaxValue;
            }

            var previous = new int[b.Length + 1];
            var current = new int[b.Length + 1];
            for (var j = 0; j <= b.Length; j++)
            {
                previous[j] = j;
            }

            for (var i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                for (var j = 1; j <= b.Length; j++)
                {
                    var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                }

                (previous, current) = (current, previous);
            }

            return previous[b.Length];
        }
    }
}
