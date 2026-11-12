using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Paduraksa.Mcp;

/// <summary>The other side broke the framing or the message grammar: the child is killed and the call ends with a fixed error.</summary>
public sealed class SandboxProtocolException(string message) : Exception(message);

/// <summary>
/// One JSON message per frame on the child's stdin and stdout: a 4-byte big-endian length, then the UTF-8 JSON. A length over the maximum is a
/// violation before a byte of the body is read, so a hostile peer cannot make the reader allocate.
/// </summary>
public static class FrameCodec
{
    /// <summary>Largest frame the child accepts (the API description, an answer of the API).</summary>
    public const int MaxToChild = 4 * 1024 * 1024;

    /// <summary>Largest frame the parent accepts from the child.</summary>
    public const int MaxFromChild = 1024 * 1024;

    public static void Write(Stream stream, ReadOnlySpan<byte> json, int max)
    {
        if (json.Length > max)
        {
            throw new SandboxProtocolException("A frame is over the limit.");
        }

        Span<byte> prefix = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(prefix, json.Length);
        stream.Write(prefix);
        stream.Write(json);
        stream.Flush();
    }

    public static async ValueTask WriteAsync(Stream stream, ReadOnlyMemory<byte> json, int max, CancellationToken cancellationToken)
    {
        if (json.Length > max)
        {
            throw new SandboxProtocolException("A frame is over the limit.");
        }

        var buffer = new byte[4 + json.Length];
        BinaryPrimitives.WriteInt32BigEndian(buffer, json.Length);
        json.CopyTo(buffer.AsMemory(4));
        await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <returns>The frame's JSON, or <c>null</c> at the end of the stream (before a frame began).</returns>
    public static byte[]? Read(Stream stream, int max)
    {
        Span<byte> prefix = stackalloc byte[4];
        var got = ReadFully(stream, prefix);
        if (got == 0)
        {
            return null;
        }

        var length = Length(prefix, got, max);
        var body = new byte[length];
        if (ReadFully(stream, body) != length)
        {
            throw new SandboxProtocolException("The stream ended inside a frame.");
        }

        return body;
    }

    public static async ValueTask<byte[]?> ReadAsync(Stream stream, int max, CancellationToken cancellationToken)
    {
        var prefix = new byte[4];
        var got = await ReadFullyAsync(stream, prefix, cancellationToken).ConfigureAwait(false);
        if (got == 0)
        {
            return null;
        }

        var length = Length(prefix, got, max);
        var body = new byte[length];
        if (await ReadFullyAsync(stream, body, cancellationToken).ConfigureAwait(false) != length)
        {
            throw new SandboxProtocolException("The stream ended inside a frame.");
        }

        return body;
    }

    private static int Length(ReadOnlySpan<byte> prefix, int got, int max)
    {
        if (got != 4)
        {
            throw new SandboxProtocolException("The stream ended inside a frame.");
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (length is <= 0 || length > max)
        {
            throw new SandboxProtocolException("A frame has a length outside the limit.");
        }

        return length;
    }

    private static int ReadFully(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer[total..]);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static async ValueTask<int> ReadFullyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}

/// <summary>Builds and reads the messages. Hand-written over <see cref="Utf8JsonWriter"/> and <see cref="JsonDocument"/>, so that every field the parent accepts from the child is read in one place and by name.</summary>
internal static class Messages
{
    public static byte[] Build(Action<Utf8JsonWriter> write)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    public static JsonDocument Parse(byte[] frame)
    {
        if (!System.Text.Unicode.Utf8.IsValid(frame))
        {
            throw new SandboxProtocolException("A frame is not UTF-8.");
        }

        try
        {
            var document = JsonDocument.Parse(frame, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("t", out var type) || type.ValueKind != JsonValueKind.String)
            {
                document.Dispose();
                throw new SandboxProtocolException("A frame is not a message.");
            }

            return document;
        }
        catch (JsonException)
        {
            throw new SandboxProtocolException("A frame is not JSON.");
        }
    }

    public static string TypeOf(JsonDocument message) => message.RootElement.GetProperty("t").GetString()!;

    public static string Text(JsonElement message, string name, int maxChars, bool required = true)
    {
        if (!message.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return required ? throw new SandboxProtocolException($"A message lacks '{name}'.") : string.Empty;
        }

        string text;
        try
        {
            text = value.GetString()!;
        }
        catch (InvalidOperationException)
        {
            // an escape that is not a character (a lone surrogate)
            throw new SandboxProtocolException($"A message's '{name}' is not text.");
        }

        return text.Length <= maxChars ? text : throw new SandboxProtocolException($"A message's '{name}' is too long.");
    }

    public static int Number(JsonElement message, string name)
    {
        return message.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : throw new SandboxProtocolException($"A message lacks the number '{name}'.");
    }

    public static bool Flag(JsonElement message, string name) =>
        message.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False && value.GetBoolean();

    public static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);
}
