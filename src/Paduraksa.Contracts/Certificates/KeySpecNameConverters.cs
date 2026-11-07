using System.Text.Json;
using System.Text.Json.Serialization;

using Paduraksa.Core.Vault;

namespace Paduraksa.Contracts.Certificates;

/// <summary>
/// Writes a <see cref="KeySpec"/> as its stable name (<c>ecdsa-p256</c>, <c>rsa-2048</c>, see <see cref="VaultNames"/>), like
/// every other enum on the wire, and reads nothing else: a number or an unknown name is a <see cref="JsonException"/>.
/// </summary>
public sealed class KeySpecNameConverter : JsonConverter<KeySpec>
{
    public override KeySpec Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("A key spec is a string such as 'ecdsa-p256'.");
        }

        try
        {
            return VaultNames.ParseKeySpec(reader.GetString()!);
        }
        catch (FormatException ex)
        {
            throw new JsonException(ex.Message, ex);
        }
    }

    public override void Write(Utf8JsonWriter writer, KeySpec value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Enum.IsDefined(value) ? value.ToName() : throw new JsonException("Undefined key spec."));
}

/// <summary>The nullable form of <see cref="KeySpecNameConverter"/>: null stays null.</summary>
public sealed class NullableKeySpecNameConverter : JsonConverter<KeySpec?>
{
    private static readonly KeySpecNameConverter Inner = new();

    public override KeySpec? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : Inner.Read(ref reader, typeof(KeySpec), options);

    public override void Write(Utf8JsonWriter writer, KeySpec? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            Inner.Write(writer, value.Value, options);
        }
    }
}
