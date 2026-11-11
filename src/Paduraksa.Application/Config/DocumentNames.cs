using System.Text.Json;

namespace Paduraksa.Application.Config;

/// <summary>Enum values spelled the way the config contract writes them (camelCase), for text the Application layer
/// stores outside the JSON serializer: audit details.</summary>
internal static class DocumentNames
{
    public static string Of<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
}
