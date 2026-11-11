using System.Text;
using System.Text.Json;

namespace Paduraksa.Application.Audit;

/// <summary>The details of an audit event: a small JSON object written by the caller.</summary>
internal static class AuditDetails
{
    public static string Json(Action<Utf8JsonWriter> write)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
