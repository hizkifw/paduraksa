using System.Text.Json;

using Paduraksa.Application.Config;
using Paduraksa.Contracts.Config;

namespace Paduraksa.Application.Yaml;

/// <summary>
/// Writes a config document as YAML that <see cref="ConfigYamlMapper"/> reads back to the same document, IDs included (docs/design/admin-surfaces.md "YAML import and export": export is the current revision as YAML).
/// The YAML is the document's own canonical JSON with the three differences of <see cref="ConfigYamlSchema"/>, so a new field of the document is exported with no change here;
/// secrets are not in the document, and a user's password has no key (<see cref="ConfigYamlSchema.IsPassword"/>), so none can be exported.
/// </summary>
/// <remarks>
/// <para><b>What is left out.</b> Versions (the pipeline's), and every empty list and empty mapping that reads back from absent as the same empty one, so the file shows only what is set. An
/// empty mapping where absent means something else (<c>passiveHealth: {}</c>) stays, and so does a required list (a host's <c>names: []</c>, which no valid document has but the YAML may say).</para>
/// <para><b>References are names when a name says it.</b> A reference is written as the object's name when exactly one object of that kind has the name (ignoring case, as the
/// import looks it up; the built-in rule-set presets count), else as its ID, so the file never says something ambiguous. It is also written as the ID when the object isn't in
/// the document, which only a document that doesn't validate has.</para>
/// </remarks>
public static class ConfigYamlExporter
{
    // The sections in the order a person reads them: what the rest refers to first, the hosts last.
    private static readonly string[] RootOrder = ["apiVersion", "settings", "profiles", "upstreams", "rateLimitPolicies", "accessLists", "ruleSets", "authProviders", "hosts"];

    /// <summary>The YAML of <paramref name="document"/>, which should be canonical (<see cref="Config.CanonicalConfigJson.Canonicalize"/>): lists then come out in the order revisions store them.</summary>
    public static string Export(ConfigDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        using var json = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(document, ConfigJsonContext.Default.ConfigDocument));
        var names = NamesOf(document);
        YamlMapping root = new(0, 0);
        foreach (var key in RootOrder)
        {
            if (!json.RootElement.TryGetProperty(key, out var value) || ConfigYamlSchema.Document.ByYamlName(key) is not { } property)
            {
                continue;
            }

            if (Convert(value, property.Node, names, property) is { } node)
            {
                root.Entries.Add(new YamlEntry(key, 0, 0, node));
                if (key == "apiVersion")
                {
                    root.Entries.Add(new YamlEntry(ConfigYamlMapper.KindKey, 0, 0, new YamlScalar(ConfigYamlMapper.KindValue, plain: false, 0, 0)));
                }
            }
        }

        return YamlTreeWriter.Write(root);
    }

    // The node for a value, or null when there is nothing to write: an empty list or mapping.
    private static YamlNode? Convert(JsonElement value, SchemaNode schema, Names names, SchemaProperty? property)
    {
        switch (schema.Kind)
        {
            case SchemaKind.Object:
                YamlMapping mapping = new(0, 0);
                foreach (var member in value.EnumerateObject())
                {
                    if (schema.Properties.FirstOrDefault(p => p.JsonName == member.Name) is not { } child || member.Value.ValueKind == JsonValueKind.Null)
                    {
                        continue;
                    }

                    var converted = Convert(member.Value, child.Node, names, child);
                    if (converted is null && (child.Nullable || child.Required) && child.Node.Kind is SchemaKind.Object or SchemaKind.List)
                    {
                        converted = child.Node.Kind == SchemaKind.Object ? new YamlMapping(0, 0) : new YamlSequence(0, 0);
                    }

                    if (converted is not null)
                    {
                        mapping.Entries.Add(new YamlEntry(child.YamlName, 0, 0, child.Reference is { } kind ? Reference(member.Value, kind, names, converted) : converted));
                    }
                }

                return mapping.Entries.Count == 0 ? null : mapping;
            case SchemaKind.List:
                YamlSequence sequence = new(0, 0);
                foreach (var item in value.EnumerateArray())
                {
                    if (Convert(item, schema.Element!, names, null) is { } converted)
                    {
                        sequence.Items.Add(converted);
                    }
                    else if (schema.Element!.Kind == SchemaKind.Object)
                    {
                        // An empty object is still an entry: {} in a list is the thing itself (a rule with no condition fails validation, not export).
                        sequence.Items.Add(new YamlMapping(0, 0));
                    }
                }

                return sequence.Items.Count == 0 ? null : sequence;
            case SchemaKind.Boolean:
                return new YamlScalar(value.GetBoolean() ? "true" : "false", plain: true, 0, 0);
            case SchemaKind.Integer or SchemaKind.Number:
                return new YamlScalar(value.GetRawText(), plain: true, 0, 0);
            default:
                return new YamlScalar(value.GetString() ?? string.Empty, plain: false, 0, 0);
        }
    }

    private static YamlNode Reference(JsonElement id, ReferenceKind kind, Names names, YamlNode fallback) =>
        id.GetString() is { } text && names.NameFor(kind, text) is { } name ? new YamlScalar(name, plain: false, 0, 0) : fallback;

    private static Names NamesOf(ConfigDocument document)
    {
        Names names = new();
        foreach (var profile in document.Profiles)
        {
            names.Add(ReferenceKind.Profile, profile.Id.ToString(), profile.Name);
        }

        foreach (var upstream in document.Upstreams)
        {
            names.Add(ReferenceKind.Upstream, upstream.Id.ToString(), upstream.Name);
        }

        foreach (var policy in document.RateLimitPolicies)
        {
            names.Add(ReferenceKind.RateLimitPolicy, policy.Id.ToString(), policy.Name);
        }

        foreach (var list in document.AccessLists)
        {
            names.Add(ReferenceKind.AccessList, list.Id.ToString(), list.Name);
        }

        foreach (var set in document.RuleSets.Concat(RuleSetPresets.All))
        {
            names.Add(ReferenceKind.RuleSet, set.Id.ToString(), set.Name);
        }

        foreach (var provider in document.AuthProviders)
        {
            names.Add(ReferenceKind.AuthProvider, provider.Id.ToString(), provider.Name);
        }

        return names;
    }

    private sealed class Names
    {
        private readonly Dictionary<(ReferenceKind, string), string> _nameOfId = [];
        private readonly Dictionary<(ReferenceKind, string), int> _uses = [];

        public void Add(ReferenceKind kind, string id, string name)
        {
            var key = ConfigNormalization.NormalizeName(name);
            _nameOfId.TryAdd((kind, id), key);
            _uses[(kind, key.ToUpperInvariant())] = _uses.GetValueOrDefault((kind, key.ToUpperInvariant())) + 1;
        }

        public string? NameFor(ReferenceKind kind, string id) =>
            _nameOfId.TryGetValue((kind, id), out var name) && name.Length > 0 && _uses[(kind, name.ToUpperInvariant())] == 1 ? name : null;
    }
}
