using System.Text.Json;

using Jint;
using Jint.Native;

namespace Paduraksa.Mcp;

/// <summary>
/// The API description as the <c>spec</c> a search script reads (PLAN §11.2): every <c>$ref</c> replaced by what it points to, built as script values
/// in the call's own engine. A target is built once and every use shares it, so the graph costs the size of the document and not the size of its
/// expansion (resolved to text, the generated description grows from half a megabyte to twelve). A schema that refers back to one being expanded
/// (a recursive type) is cut there as <c>{ "x-recursive-ref": "Name" }</c>, so what a script walks is a finite tree.
/// </summary>
internal sealed class SpecGraph(JsonDocument document)
{
    private const string SchemasPrefix = "#/components/schemas/";

    private readonly JsonElement _root = document.RootElement;

    public static SpecGraph Parse(ReadOnlySpan<byte> openApiJson) => new(JsonDocument.Parse(openApiJson.ToArray()));

    /// <summary>The operations as the request policy reads them: raw, with parameter references followed.</summary>
    public JsonElement Root => _root;

    public JsValue Build(Engine engine)
    {
        Builder builder = new(engine, _root);
        return builder.Root();
    }

    /// <summary>Follows a local reference (<c>#/components/parameters/Id</c>) in the document.</summary>
    internal static bool TryFind(JsonElement root, string pointer, out JsonElement found)
    {
        found = root;
        if (!pointer.StartsWith("#/", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var raw in pointer[2..].Split('/'))
        {
            var segment = Uri.UnescapeDataString(raw).Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (found.ValueKind != JsonValueKind.Object || !found.TryGetProperty(segment, out found))
            {
                return false;
            }
        }

        return true;
    }

    private sealed class Builder(Engine engine, JsonElement root)
    {
        private const int NoCut = int.MaxValue;

        private readonly Dictionary<string, JsValue> _built = new(StringComparer.Ordinal);
        private readonly List<string> _expanding = [];

        public JsValue Root() => Convert(root, isRoot: true, out _);

        // lowestCut: the shallowest reference being expanded that a cut below points back to, or NoCut. It decides whether a subtree is the same wherever it is reached.
        private JsValue Convert(JsonElement node, bool isRoot, out int lowestCut)
        {
            lowestCut = NoCut;
            switch (node.ValueKind)
            {
                case JsonValueKind.Object:
                    return ConvertObject(node, isRoot, out lowestCut);
                case JsonValueKind.Array:
                    List<JsValue> items = [];
                    foreach (var item in node.EnumerateArray())
                    {
                        items.Add(Convert(item, isRoot: false, out var itemCut));
                        lowestCut = Math.Min(lowestCut, itemCut);
                    }

                    return new JsArray(engine, [.. items]);
                case JsonValueKind.String:
                    return node.GetString()!;
                case JsonValueKind.Number:
                    return node.GetDouble();
                case JsonValueKind.True:
                    return JsBoolean.True;
                case JsonValueKind.False:
                    return JsBoolean.False;
                default:
                    return JsValue.Null;
            }
        }

        private JsValue ConvertObject(JsonElement node, bool isRoot, out int lowestCut)
        {
            lowestCut = NoCut;
            if (node.TryGetProperty("$ref", out var reference) && reference.ValueKind == JsonValueKind.String && reference.GetString() is { } pointer)
            {
                return Reference(node, pointer, out lowestCut);
            }

            var result = new JsObject(engine);
            foreach (var property in node.EnumerateObject())
            {
                if (isRoot && property.Name == "components" && property.Value.ValueKind == JsonValueKind.Object)
                {
                    result.Set("components", Components(property.Value), true);
                    continue;
                }

                result.Set(property.Name, Convert(property.Value, isRoot: false, out var childCut), true);
                lowestCut = Math.Min(lowestCut, childCut);
            }

            return result;
        }

        /// <summary>Every named component is read through the shared target of its pointer, so what a script finds by name is what the operations use.</summary>
        private JsObject Components(JsonElement components)
        {
            var result = new JsObject(engine);
            foreach (var group in components.EnumerateObject())
            {
                var members = new JsObject(engine);
                foreach (var member in group.Value.ValueKind == JsonValueKind.Object ? group.Value.EnumerateObject() : default)
                {
                    members.Set(member.Name, Target($"#/components/{group.Name}/{member.Name}", out _), true);
                }

                result.Set(group.Name, members, true);
            }

            return result;
        }

        private JsValue Reference(JsonElement node, string pointer, out int lowestCut)
        {
            var target = Target(pointer, out lowestCut);

            // What stands beside a reference (a description, in 3.1) applies on top of its target, as a copy: the shared target stays as it is.
            var siblings = node.EnumerateObject().Where(p => p.Name != "$ref").ToList();
            if (siblings.Count == 0 || !target.IsObject())
            {
                return target;
            }

            var merged = new JsObject(engine);
            foreach (var key in target.AsObject().GetOwnPropertyKeys(Jint.Runtime.Types.String))
            {
                merged.Set(key, target.AsObject().Get(key), true);
            }

            foreach (var sibling in siblings)
            {
                merged.Set(sibling.Name, Convert(sibling.Value, isRoot: false, out var siblingCut), true);
                lowestCut = Math.Min(lowestCut, siblingCut);
            }

            return merged;
        }

        private JsValue Target(string pointer, out int lowestCut)
        {
            lowestCut = NoCut;
            JsValue target;
            var ancestor = _expanding.IndexOf(pointer);
            if (ancestor >= 0)
            {
                var marker = new JsObject(engine);
                marker.Set("x-recursive-ref", pointer.StartsWith(SchemasPrefix, StringComparison.Ordinal) ? pointer[SchemasPrefix.Length..] : pointer, true);
                target = marker;
                lowestCut = ancestor;
            }
            else if (_built.TryGetValue(pointer, out var built))
            {
                target = built;
            }
            else if (!TryFind(root, pointer, out var found))
            {
                var marker = new JsObject(engine);
                marker.Set("x-unresolved-ref", pointer, true);
                target = marker;
            }
            else
            {
                var depth = _expanding.Count;
                _expanding.Add(pointer);
                target = Convert(found, isRoot: false, out lowestCut);
                _expanding.RemoveAt(_expanding.Count - 1);

                // A cut that points back into this subtree (Node inside Node) is part of it: the subtree is the same wherever it is reached. One that
                // points above it depends on what was being expanded around it, so it is built again where it is used.
                if (lowestCut >= depth)
                {
                    _built[pointer] = target;
                    lowestCut = NoCut;
                }
            }

            return target;
        }
    }
}
