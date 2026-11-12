using System.Collections.Concurrent;
using System.Text.Json.Serialization.Metadata;

using Paduraksa.Contracts.Config;
using Paduraksa.Core.Ids;
using Paduraksa.Core.Model;

namespace Paduraksa.Application.Yaml;

/// <summary>What kind of value a schema node is, in the YAML's own terms.</summary>
internal enum SchemaKind
{
    /// <summary>Text, whatever a plain scalar looks like: a name, an enum member, a typed ID, a time span.</summary>
    Text,
    Boolean,
    Integer,
    Number,
    Object,
    List,
}

/// <summary>The kinds of object a reference by name can point at.</summary>
internal enum ReferenceKind
{
    Profile,
    Upstream,
    RateLimitPolicy,
    AccessList,
    RuleSet,
    AuthProvider,
}

internal sealed class SchemaNode
{
    public SchemaNode(SchemaKind kind, Type type)
    {
        Kind = kind;
        Type = type;
    }

    public SchemaKind Kind { get; }

    public Type Type { get; }

    /// <summary>The element of a list.</summary>
    public SchemaNode? Element { get; internal set; }

    public IReadOnlyList<SchemaProperty> Properties { get; internal set; } = [];

    public SchemaProperty? ByYamlName(string name)
    {
        foreach (var property in Properties)
        {
            if (string.Equals(property.YamlName, name, StringComparison.Ordinal))
            {
                return property;
            }
        }

        return null;
    }
}

/// <param name="JsonName">The property's name in the canonical JSON.</param>
/// <param name="YamlName">Its key in the YAML: the same, but a reference to another object drops the <c>Id</c> (<c>profileId</c> is <c>profile</c>, because the value is a name).</param>
/// <param name="Node">What the value is.</param>
/// <param name="Required">The canonical document needs it. An object's own <c>id</c> is the one required key the YAML may leave out.</param>
/// <param name="Reference">Set when the value is a reference to another object of the document.</param>
/// <param name="IsId">The object's own ID.</param>
/// <param name="Nullable">Absent is a value of its own: an empty mapping or list there is not the same as none (<c>passiveHealth: {}</c> switches passive health on with its defaults).</param>
internal sealed record SchemaProperty(string JsonName, string YamlName, SchemaNode Node, bool Required, ReferenceKind? Reference, bool IsId, bool Nullable);

/// <summary>
/// The shape of the YAML, read from the metadata the config document's JSON source generator already holds (<see cref="ConfigJsonContext"/>), so the YAML
/// accepts exactly the keys the document has, with nothing to keep in step: a field added to the document is in the YAML the same day. Three differences
/// from the canonical JSON, all of them in this class: <c>version</c> is not a key (the pipeline assigns versions), a reference to another object is written by name
/// under the key without <c>Id</c>, and an object's <c>id</c> is optional.
/// </summary>
internal static class ConfigYamlSchema
{
    public const string VersionKey = "version";

    private static readonly ConcurrentDictionary<Type, SchemaNode> Cache = new();

    private static readonly Dictionary<Type, ReferenceKind> References = new()
    {
        [typeof(ProfileId)] = ReferenceKind.Profile,
        [typeof(UpstreamId)] = ReferenceKind.Upstream,
        [typeof(RateLimitPolicyId)] = ReferenceKind.RateLimitPolicy,
        [typeof(AccessListId)] = ReferenceKind.AccessList,
        [typeof(RuleSetId)] = ReferenceKind.RuleSet,
        [typeof(AuthProviderId)] = ReferenceKind.AuthProvider,
    };

    public static SchemaNode Document { get; } = Of(typeof(ConfigDocument));

    public static SchemaNode Of(Type type)
    {
        if (Cache.TryGetValue(type, out var known))
        {
            return known;
        }

        var node = Build(type);
        return Cache.GetOrAdd(type, node);
    }

    private static SchemaNode Build(Type declared)
    {
        var type = Nullable.GetUnderlyingType(declared) ?? declared;
        if (type == typeof(string) || type == typeof(TimeSpan) || type.IsEnum || IsTypedId(type))
        {
            return new SchemaNode(SchemaKind.Text, type);
        }

        if (type == typeof(bool))
        {
            return new SchemaNode(SchemaKind.Boolean, type);
        }

        if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte))
        {
            return new SchemaNode(SchemaKind.Integer, type);
        }

        if (type == typeof(double) || type == typeof(float) || type == typeof(decimal))
        {
            return new SchemaNode(SchemaKind.Number, type);
        }

        if (type.IsGenericType && type.GetGenericArguments() is [var element] && typeof(IEnumerable<>).MakeGenericType(element).IsAssignableFrom(type))
        {
            return new SchemaNode(SchemaKind.List, type) { Element = Of(element) };
        }

        var info = ConfigJsonContext.Default.GetTypeInfo(type)
                   ?? throw new InvalidOperationException($"{type.Name} isn't in the config document's JSON contract.");
        if (info.Kind != JsonTypeInfoKind.Object)
        {
            throw new InvalidOperationException($"{type.Name} isn't a value the YAML schema knows.");
        }

        var node = new SchemaNode(SchemaKind.Object, type);
        List<SchemaProperty> properties = [];
        foreach (var property in info.Properties)
        {
            if (string.Equals(property.Name, VersionKey, StringComparison.Ordinal) || IsPassword(type, property.Name))
            {
                continue;
            }

            var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            ReferenceKind? reference = property.Name.EndsWith("Id", StringComparison.Ordinal) && References.TryGetValue(propertyType, out var kind) ? kind : null;
            var isId = string.Equals(property.Name, "id", StringComparison.Ordinal);
            var yamlName = reference is null ? property.Name : property.Name[..^2];
            properties.Add(new SchemaProperty(property.Name, yamlName, Of(property.PropertyType), property.IsRequired && !isId, reference, isId, property.IsGetNullable));
        }

        node.Properties = properties;
        return node;
    }

    /// <summary>A user's plain password is a write-only field of the API; the YAML never carries a password, in either direction (PLAN §8).</summary>
    public static bool IsPassword(Type owner, string jsonName) =>
        owner == typeof(AccessListUserConfig) && string.Equals(jsonName, "password", StringComparison.Ordinal);

    private static bool IsTypedId(Type type) =>
        type.IsValueType && type.GetInterfaces().Any(static i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ITypedId<>));
}
