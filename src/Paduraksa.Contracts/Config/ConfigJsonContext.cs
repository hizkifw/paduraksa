using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

using Paduraksa.Core.Ids;

namespace Paduraksa.Contracts.Config;

/// <summary>
/// The source-generated JSON contract of the config document, shared by revisions, the API, MCP and YAML. Writing is
/// deterministic: camelCase properties in a fixed order (each record's constructor parameters, then the properties
/// declared in its body), nulls omitted, no indentation, ASCII output (the default encoder escapes everything else),
/// invariant numbers, <see cref="TimeSpan"/> as <c>[-][d.]hh:mm:ss[.fffffff]</c>, typed IDs as their string form and
/// enums as camelCase names. Canonical bytes also need the content in canonical order and spelling;
/// <c>Paduraksa.Application.Config.CanonicalConfigJson</c> takes care of that.
/// </summary>
/// <remarks>
/// Reading is strict, because a document that half-reads is worse than one that doesn't: unknown properties (typos,
/// or fields from a newer build that this one would silently drop), duplicate properties, missing required properties
/// (<c>apiVersion</c> included), null for a required value, and anything but an exact enum name are all rejected. An
/// explicit null for an optional list or nested record reads as absent (empty); null list elements are rejected by
/// <c>CanonicalConfigJson.Deserialize</c>. Compatibility is one way only: newer builds read every older document, and
/// downgrades are unsupported (an older build rejects a document with fields it doesn't know).
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    Converters = [typeof(TypedIdJsonConverterFactory)])]
[JsonSerializable(typeof(ConfigDocument))]
[JsonSerializable(typeof(ConfigError))]
[JsonSerializable(typeof(IReadOnlyList<ConfigError>))]
public sealed partial class ConfigJsonContext : JsonSerializerContext;

/// <summary>
/// Enums as their exact camelCase member names (<c>proxy</c>, <c>misdirectedRequest</c>). Reading accepts nothing else:
/// no other casing, no numbers, no flag combinations (<c>"proxy,redirect"</c>). Writing uses the same table; an
/// undefined value, which only a hand-built (and invalid) document has, is written as its number so that serializing
/// and diffing never throw, and reading such bytes back fails.
/// </summary>
internal sealed class ConfigEnumConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    private static readonly FrozenDictionary<string, TEnum> ByName =
        Enum.GetValues<TEnum>().ToFrozenDictionary(v => JsonNamingPolicy.CamelCase.ConvertName(v.ToString()), StringComparer.Ordinal);

    private static readonly FrozenDictionary<TEnum, string> NameOf = ByName.ToFrozenDictionary(p => p.Value, p => p.Key);

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && reader.GetString() is { } name && ByName.TryGetValue(name, out TEnum value))
        {
            return value;
        }

        throw new JsonException($"Expected one of {string.Join(", ", ByName.Keys.Order(StringComparer.Ordinal).Select(k => $"'{k}'"))} for {typeof(TEnum).Name}.");
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (NameOf.TryGetValue(value, out string? name))
        {
            writer.WriteStringValue(name);
        }
        else
        {
            writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
        }
    }
}
