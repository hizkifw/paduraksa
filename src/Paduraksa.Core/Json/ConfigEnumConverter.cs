using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Paduraksa.Core.Json;

/// <summary>
/// Enums as their exact camelCase member names (<c>proxy</c>, <c>misdirectedRequest</c>), the wire form of the config
/// document and of the API. Reading accepts nothing else: no other casing, no numbers, no flag combinations
/// (<c>"proxy,redirect"</c>). Writing uses the same table; an undefined value, which only a hand-built (and invalid)
/// document has, is written as its number so that serializing and diffing never throw, and reading such bytes back fails.
/// <para>Attached to the enum itself rather than listed in one context's options, so every context that serializes it -
/// the config document's, the admin API's - spells it the same way, with no per-context list to forget. That is why the
/// enums the contract and the proxy share live in <c>Paduraksa.Core</c>.</para>
/// </summary>
public sealed class ConfigEnumConverter<TEnum> : JsonConverter<TEnum>
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
