using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using Paduraksa.Core.Model;

namespace Paduraksa.Application.Config;

/// <summary>
/// Reads the inline <c>ipAllowList</c> that settings carried before access lists existed, and turns it into the one way a
/// document says it now. It works on the JSON, before the document is read into the current model, because the contract
/// no longer has the property: every layer that set one (the global defaults, a profile, a host, a route) gets an
/// <c>accessList</c> reference instead, and the lists it names are added to <c>accessLists</c>.
/// </summary>
/// <remarks>
/// <para><b>Same behaviour.</b> A layer's list replaced the inherited one as a whole, and an empty list lifted it. The
/// reference says both: a list is <c>{ "enabled": true, "accessListId": ... }</c> (both fields, so an inherited
/// <c>enabled: false</c> can't switch it off), and an empty one is <c>{ "enabled": false }</c>. Denial stays 403, or a
/// dropped connection where the list said so.</para>
/// <para><b>One list per content.</b> Lists with the same addresses and the same drop setting are one access list. It is
/// named after the first place that used it in document order (<c>host-example-com</c>, <c>route-example-com-admin</c>,
/// <c>profile-lan-only</c>, <c>global-defaults</c>), de-duplicated with a numeric suffix, and its ID is derived from the
/// content, so converting the same document twice gives the same result byte for byte.</para>
/// </remarks>
internal static class LegacyAccessListUpgrade
{
    private const string Property = "ipAllowList";

    /// <summary>The upgraded JSON, or null when the document has no inline allow list. Throws
    /// <see cref="System.Text.Json.JsonException"/> for JSON that isn't a document.</summary>
    public static byte[]? Upgrade(ReadOnlySpan<byte> utf8Json)
    {
        // The property name is quoted in the bytes whatever the list holds: no scan of the tree for a current document.
        if (utf8Json.IndexOf("\"ipAllowList\""u8) < 0)
        {
            return null;
        }

        if (JsonNode.Parse(utf8Json) is not JsonObject root)
        {
            return null;
        }

        Conversion conversion = new();
        var changed = false;
        if (root["settings"] is JsonObject { } settings && settings["defaults"] is JsonObject defaults)
        {
            changed |= conversion.Layer(defaults, "global-defaults");
        }

        if (root["profiles"] is JsonArray profiles)
        {
            foreach (var profile in profiles.OfType<JsonObject>())
            {
                if (profile["settings"] is JsonObject layer)
                {
                    changed |= conversion.Layer(layer, "profile-" + Slug(profile["name"]?.GetValue<string>() ?? string.Empty, "profile"));
                }
            }
        }

        if (root["hosts"] is JsonArray hosts)
        {
            foreach (var host in hosts.OfType<JsonObject>())
            {
                var hostName = FirstName(host);
                if (host["settings"] is JsonObject layer)
                {
                    changed |= conversion.Layer(layer, "host-" + Slug(hostName, "host"));
                }

                if (host["routes"] is JsonArray routes)
                {
                    foreach (var route in routes.OfType<JsonObject>())
                    {
                        if (route["settings"] is JsonObject routeLayer)
                        {
                            var path = route["path"]?.GetValue<string>() ?? string.Empty;
                            changed |= conversion.Layer(routeLayer, $"route-{Slug(hostName, "host")}-{Slug(path, "root")}");
                        }
                    }
                }
            }
        }

        if (!changed)
        {
            return null;
        }

        var lists = root["accessLists"] as JsonArray ?? [];
        root["accessLists"] = lists;
        foreach (var list in conversion.Lists)
        {
            lists.Add(list);
        }

        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    private static string FirstName(JsonObject host) =>
        host["names"] is JsonArray { Count: > 0 } names ? names[0]?.GetValue<string>() ?? string.Empty : string.Empty;

    // Lowercase letters and digits, one hyphen between runs: a name that reads well and is a valid label.
    private static string Slug(string text, string fallback)
    {
        StringBuilder slug = new();
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                slug.Append(c);
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        var trimmed = slug.ToString().Trim('-');
        return trimmed.Length == 0 ? fallback : trimmed;
    }

    private sealed class Conversion
    {
        private readonly Dictionary<string, string> _byContent = [];
        private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);

        public List<JsonObject> Lists { get; } = [];

        /// <summary>Replaces <paramref name="settings"/>'s <c>ipAllowList</c> with the reference; false when it has none.</summary>
        public bool Layer(JsonObject settings, string source)
        {
            if (!settings.TryGetPropertyValue(Property, out var legacy))
            {
                return false;
            }

            settings.Remove(Property);
            if (legacy is null)
            {
                // An explicit null reads as absent: the layer inherits, so it gets no reference (and the key is gone, which the
                // strict reader would refuse).
                return true;
            }

            string[] addresses = [.. (legacy?["addresses"] as JsonArray ?? [])
                .Select(static a => ConfigNormalization.CanonicalAddress(a?.GetValue<string>()))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)];
            var drop = legacy?["dropDenied"]?.GetValue<bool>() ?? false;
            if (addresses.Length == 0)
            {
                // An empty list restricted nothing: the way a layer lifted what it inherited.
                settings["accessList"] = new JsonObject { ["enabled"] = false };
                return true;
            }

            var content = $"{(drop ? "drop" : "403")}\n{string.Join('\n', addresses)}";
            if (!_byContent.TryGetValue(content, out var id))
            {
                id = IdOf(content);
                _byContent.Add(content, id);
                Lists.Add(new JsonObject
                {
                    ["id"] = id,
                    ["name"] = Unique(source),
                    ["version"] = 1,
                    ["ip"] = drop
                        ? new JsonObject { ["allow"] = new JsonArray([.. addresses.Select(static a => (JsonNode)JsonValue.Create(a)!)]), ["dropDenied"] = true }
                        : new JsonObject { ["allow"] = new JsonArray([.. addresses.Select(static a => (JsonNode)JsonValue.Create(a)!)]) },
                });
            }

            settings["accessList"] = new JsonObject { ["enabled"] = true, ["accessListId"] = id };
            return true;
        }

        private string Unique(string name)
        {
            var stem = name.Length > ConfigNormalization.MaxNameLength - 6 ? name[..(ConfigNormalization.MaxNameLength - 6)].TrimEnd('-') : name;
            var candidate = stem;
            for (var n = 2; !_names.Add(candidate); n++)
            {
                candidate = $"{stem}-{n}";
            }

            return candidate;
        }

        // A UUID taken from the content's hash (version 8, the vendor-defined one), so the same list is the same ID on
        // every read and in every process.
        private static string IdOf(string content)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes("paduraksa:access-list-upgrade:v1\n" + content));
            Span<byte> bytes = hash.AsSpan(0, 16);
            bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
            bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
            return AccessListId.FromValue(new Guid(bytes, bigEndian: true)).ToString();
        }
    }
}
