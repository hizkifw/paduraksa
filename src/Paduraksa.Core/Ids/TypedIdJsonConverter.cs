using System.Text.Json;
using System.Text.Json.Serialization;

namespace Paduraksa.Core.Ids;

/// <summary>
/// <see cref="System.Text.Json"/> converter for any typed ID: a JSON string in the canonical <c>prefix_base62</c>
/// form, also usable as a dictionary key. Applied to generated IDs via <see cref="JsonConverterAttribute"/>;
/// source-generated <see cref="JsonSerializerContext"/>s must register <see cref="TypedIdJsonConverterFactory"/>.
/// </summary>
/// <typeparam name="T">The typed ID.</typeparam>
public sealed class TypedIdJsonConverter<T> : JsonConverter<T>
    where T : struct, ITypedId<T>
{
    // JSON escapes expand one char to at most 6 bytes (\uXXXX); anything longer can't be a valid ID.
    private const int MaxEscapeExpansion = 6;

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"Expected a string for {typeof(T).Name}, got {reader.TokenType}.");
        }

        return ReadCore(ref reader);
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        Span<byte> buffer = stackalloc byte[T.FormattedLength];
        value.TryFormat(buffer, out var written, default, null);
        writer.WriteStringValue(buffer[..written]);
    }

    public override T ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ReadCore(ref reader);

    public override void WriteAsPropertyName(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        Span<byte> buffer = stackalloc byte[T.FormattedLength];
        value.TryFormat(buffer, out var written, default, null);
        writer.WritePropertyName(buffer[..written]);
    }

    private static T ReadCore(ref Utf8JsonReader reader)
    {
        if (!reader.HasValueSequence && !reader.ValueIsEscaped)
        {
            if (T.TryParse(reader.ValueSpan, out var id))
            {
                return id;
            }
        }
        else
        {
            var rawLength = reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length;
            if (rawLength <= (long)T.FormattedLength * MaxEscapeExpansion)
            {
                Span<char> buffer = stackalloc char[(int)rawLength];
                var length = reader.CopyString(buffer);
                if (T.TryParse(buffer[..length], out var id))
                {
                    return id;
                }
            }
        }

        throw new JsonException($"Invalid {typeof(T).Name}: expected '{T.Prefix}_' followed by base62 characters.");
    }
}
