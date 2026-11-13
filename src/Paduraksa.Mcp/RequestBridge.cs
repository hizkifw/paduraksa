using System.Text.Json;
using System.Text.RegularExpressions;

namespace Paduraksa.Mcp;

/// <summary>
/// The parent's side of <c>paduraksa.request</c> (docs/design/admin-surfaces.md "MCP in code mode (M10)"): the child sends the script's options as JSON, and this validates every field by name,
/// applies <see cref="RequestPolicy"/> (the refused list, the dry run or the 428 for a write without <c>confirm</c>) and dispatches as the token's caller.
/// What comes back is a response frame, or an error the child raises in the script. The child is not trusted: nothing it sends is used before it is checked
/// here, and nothing here depends on the child having checked.
/// </summary>
internal sealed partial class RequestBridge(RequestPolicy policy, IApiDispatcher dispatcher, SandboxLimits limits)
{
    private static readonly HashSet<string> Options = new(["method", "path", "query", "body", "confirm", "ifMatch"], StringComparer.Ordinal);

    /// <summary>The frame to send back for the child's <paramref name="id"/>d request.</summary>
    public async ValueTask<byte[]> HandleAsync(int id, string optionsJson, CancellationToken cancellationToken)
    {
        ApiCall call;
        bool confirm;
        try
        {
            (call, confirm) = Parse(optionsJson);
        }
        catch (RequestException ex)
        {
            return Failure(id, ex.Type, ex.Message);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            // text with an escape that is not a character, or a value the reader cannot give back
            return Failure(id, "TypeError", "request takes one object: { method, path, query?, body?, confirm?, ifMatch? }.");
        }

        var decision = policy.Decide(call.Method, call.Path);
        switch (decision.Kind)
        {
            case RequestKind.Refused or RequestKind.Unknown:
                return Failure(id, "Error", decision.Reason);
            case RequestKind.Write when !confirm && !decision.SupportsDryRun:
                return RequiresConfirm(id, call);
            case RequestKind.Write when !confirm:
                var plan = await dispatcher.SendAsync(call with { Query = [.. call.Query, new("dryRun", "true")] }, cancellationToken).ConfigureAwait(false);
                return Reply(id, plan, dryRun: true);
            default:
                return Reply(id, await dispatcher.SendAsync(call, cancellationToken).ConfigureAwait(false), dryRun: false);
        }
    }

    /// <summary>The same failure the child would raise for a request it could not frame, for a call the dispatcher failed to answer.</summary>
    public static byte[] DispatchFailed(int id) => Failure(id, "Error", "The request failed inside the server; nothing more is known.");

    private (ApiCall Call, bool Confirm) Parse(string optionsJson)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(optionsJson, new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException)
        {
            throw Invalid("request takes one object: { method, path, query?, body?, confirm?, ifMatch? }.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Invalid("request takes one object: { method, path, query?, body?, confirm?, ifMatch? }.");
            }

            foreach (var property in root.EnumerateObject())
            {
                if (!Options.Contains(property.Name))
                {
                    throw Invalid($"Unknown option '{property.Name}'. The options are method, path, query, body, confirm and ifMatch.");
                }
            }

            if (!root.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String || !root.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String)
            {
                throw Invalid("method and path are required strings, e.g. { method: \"GET\", path: \"/api/v1/hosts\" }.");
            }

            var verb = method.GetString()!.ToUpperInvariant();
            var confirm = false;
            if (root.TryGetProperty("confirm", out var confirmValue))
            {
                if (confirmValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw Invalid("confirm must be true or false.");
                }

                confirm = confirmValue.GetBoolean();
            }

            string? ifMatch = null;
            if (root.TryGetProperty("ifMatch", out var tag))
            {
                if (tag.ValueKind != JsonValueKind.String || !EntityTag().IsMatch(tag.GetString()!))
                {
                    throw Invalid("ifMatch must be the quoted etag a read returned, e.g. \"3\".");
                }

                ifMatch = tag.GetString();
            }

            string? bodyJson = null;
            if (root.TryGetProperty("body", out var body))
            {
                if (verb == "GET")
                {
                    throw Invalid("A GET takes no body; put filters in query.");
                }

                bodyJson = body.GetRawText();
                if (System.Text.Encoding.UTF8.GetByteCount(bodyJson) > limits.MaxRequestBodyBytes)
                {
                    throw Invalid($"The body is larger than {limits.MaxRequestBodyBytes / 1024} KiB.");
                }
            }

            root.TryGetProperty("query", out var query);
            return (new ApiCall(verb, path.GetString()!, Query(query), bodyJson, ifMatch), confirm);
        }
    }

    private static List<KeyValuePair<string, string>> Query(JsonElement query)
    {
        List<KeyValuePair<string, string>> pairs = [];
        if (query.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return pairs;
        }

        if (query.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("query must be an object such as { limit: 20 }.");
        }

        foreach (var property in query.EnumerateObject())
        {
            var name = property.Name;
            if (name.Length is 0 or > 64)
            {
                throw Invalid("A query name is empty or longer than 64 characters.");
            }

            if (string.Equals(name, "dryRun", StringComparison.OrdinalIgnoreCase))
            {
                throw Invalid("dryRun is the client's: leave out confirm for a dry run, send confirm: true to apply.");
            }

            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in property.Value.EnumerateArray())
                {
                    Add(pairs, name, item);
                }
            }
            else
            {
                Add(pairs, name, property.Value);
            }
        }

        return pairs;
    }

    private static void Add(List<KeyValuePair<string, string>> pairs, string name, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()!,
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            _ => throw Invalid($"The query value of '{name}' must be a string, number or boolean (or an array of them)."),
        };
        if (text.Length > 1024)
        {
            throw Invalid($"The query value of '{name}' is longer than 1024 characters.");
        }

        if (pairs.Count >= 64)
        {
            throw Invalid("A query has at most 64 values.");
        }

        pairs.Add(new(name, text));
    }

    private static RequestException Invalid(string message) => new("TypeError", message);

    private static byte[] Failure(int id, string type, string message) => Messages.Build(w =>
    {
        w.WriteString("t", "response");
        w.WriteNumber("id", id);
        w.WriteStartObject("error");
        w.WriteString("type", type);
        w.WriteString("message", message);
        w.WriteEndObject();
    });

    private static byte[] RequiresConfirm(int id, ApiCall call) => Messages.Build(w =>
    {
        w.WriteString("t", "response");
        w.WriteNumber("id", id);
        w.WriteNumber("status", 428);
        w.WriteBoolean("json", true);
        w.WriteString("body", JsonSerializer.Serialize(new
        {
            requiresConfirm = true,
            method = call.Method,
            path = call.Path,
            note = "Nothing was sent: this request changes things and the operation has no dry run. Repeat it with confirm: true to apply it.",
        }));
    });

    private byte[] Reply(int id, ApiReply reply, bool dryRun) => Messages.Build(w =>
    {
        w.WriteString("t", "response");
        w.WriteNumber("id", id);
        w.WriteNumber("status", reply.Status);
        if (reply.Truncated || string.IsNullOrEmpty(reply.Body))
        {
            w.WriteNull("body");
        }
        else
        {
            w.WriteString("body", reply.Body);
            w.WriteBoolean("json", reply.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true);
        }

        if (reply.ETag is { Length: > 0 } etag)
        {
            w.WriteString("etag", etag);
        }

        if (dryRun)
        {
            w.WriteBoolean("dryRun", true);
            w.WriteString("note", "Dry run: nothing was changed. Repeat the request with confirm: true to apply it.");
        }
        else if (reply.Truncated)
        {
            w.WriteString("note", $"The answer is larger than {limits.MaxResponseBytes / 1024} KiB and was not read; narrow the request (limit, filters).");
        }
    });

    [GeneratedRegex("^\"[\\x21\\x23-\\x7E]{1,200}\"$")]
    private static partial Regex EntityTag();

    private sealed class RequestException(string type, string message) : Exception(message)
    {
        public string Type { get; } = type;
    }
}
