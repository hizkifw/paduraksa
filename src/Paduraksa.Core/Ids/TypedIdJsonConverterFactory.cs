using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Paduraksa.Core.Ids;

/// <summary>
/// Supplies <see cref="TypedIdJsonConverter{T}"/> for every generated typed ID. Register it on source-generated
/// contexts: <c>[JsonSourceGenerationOptions(Converters = [typeof(TypedIdJsonConverterFactory)])]</c>.
/// </summary>
/// <remarks>
/// The typed-ID generator puts <see cref="JsonConverterAttribute"/> on each ID, which is enough for reflection-based
/// serialization. But source generators can't see each other's output, so the System.Text.Json source generator
/// doesn't see that attribute and would serialize IDs as empty objects. This factory resolves the converter from the
/// attribute at runtime instead.
/// </remarks>
public sealed class TypedIdJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
    {
        ArgumentNullException.ThrowIfNull(typeToConvert);
        return GetConverterType(typeToConvert) is not null;
    }

    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(typeToConvert);
        return GetConverterType(typeToConvert) is { } converterType ? (JsonConverter?)Activator.CreateInstance(converterType) : null;
    }

    private static Type? GetConverterType(Type type) =>
        type.IsValueType
        && type.GetCustomAttribute<JsonConverterAttribute>(inherit: false)?.ConverterType is { IsGenericType: true } converterType
        && converterType.GetGenericTypeDefinition() == typeof(TypedIdJsonConverter<>)
            ? converterType
            : null;
}
