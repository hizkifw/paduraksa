using System.Text.Json;

namespace Paduraksa.Mcp;

public enum RequestKind
{
    /// <summary>Changes nothing: dispatched as it is.</summary>
    Read = 1,

    /// <summary>Changes something: needs <c>confirm: true</c>, and is a dry run without it where the operation has one.</summary>
    Write = 2,

    /// <summary>Never dispatched.</summary>
    Refused = 3,

    /// <summary>Not an operation of the API description.</summary>
    Unknown = 4,
}

/// <summary>How the policy classified one request.</summary>
/// <param name="Kind">What the request may do.</param>
/// <param name="Reason">For <see cref="RequestKind.Refused"/> and <see cref="RequestKind.Unknown"/>: what the model is told.</param>
/// <param name="SupportsDryRun">The operation takes <c>?dryRun=true</c>, which returns the plan and changes nothing.</param>
public sealed record RequestDecision(RequestKind Kind, string Reason = "", bool SupportsDryRun = false);

/// <summary>
/// Decides what a script's request may do (docs/design/admin-surfaces.md "MCP in code mode (M10)"), from the resolved API description and <see cref="EndpointRules"/>: only paths that match an
/// operation of the description are dispatched, those on the refused list never are, and a write is dispatched only with <c>confirm: true</c>. This is
/// a second check on top of the API's own permission for the token, which still applies to everything that is dispatched.
/// </summary>
public sealed class RequestPolicy
{
    private const string Prefix = "/api/v1/";

    private static readonly string[] Methods = ["GET", "POST", "PUT", "PATCH", "DELETE"];

    private readonly List<Operation> _operations;

    private RequestPolicy(List<Operation> operations) => _operations = operations;

    /// <summary>Every (method, path template) of the description with whether it takes <c>dryRun</c>.</summary>
    public IEnumerable<(string Method, string Template, bool SupportsDryRun)> Operations => _operations.Select(o => (o.Method, o.Template, o.SupportsDryRun));

    public static RequestPolicy FromSpec(JsonElement root)
    {
        List<Operation> operations = [];
        if (root.TryGetProperty("paths", out var paths) && paths.ValueKind == JsonValueKind.Object)
        {
            foreach (var path in paths.EnumerateObject())
            {
                foreach (var method in path.Value.EnumerateObject())
                {
                    var verb = method.Name.ToUpperInvariant();
                    if (Array.IndexOf(Methods, verb) >= 0 && method.Value.ValueKind == JsonValueKind.Object)
                    {
                        operations.Add(new Operation(verb, path.Name, path.Name.Split('/', StringSplitOptions.RemoveEmptyEntries), TakesDryRun(root, method.Value)));
                    }
                }
            }
        }

        return new RequestPolicy(operations);
    }

    public RequestDecision Decide(string method, string path)
    {
        if (Array.IndexOf(Methods, method) < 0)
        {
            return new RequestDecision(RequestKind.Refused, $"The method must be one of {string.Join(", ", Methods)}.");
        }

        // Judged on the path as written first, so a refusal names its reason even for a path the description does not know.
        if (EndpointRules.Refused.FirstOrDefault(rule => rule.Covers(method, path)) is { } listed)
        {
            return Refuse(listed);
        }

        if (!IsPlainPath(path, out var segments))
        {
            return new RequestDecision(RequestKind.Refused, "The path must be a plain path such as /api/v1/hosts or /api/v1/hosts/host_xyz: no query, fragment, dot segments, percent-encoding or empty segments. Put the query in the query option.");
        }

        var matches = _operations.Where(o => o.Method == method && o.Matches(segments)).ToList();
        foreach (var rule in EndpointRules.Refused)
        {
            if (matches.Any(m => rule.Covers(method, m.Template)))
            {
                return Refuse(rule);
            }
        }

        if (matches.Count == 0)
        {
            return new RequestDecision(RequestKind.Unknown, $"No {method} {path} in the API description. Find the operation with the search tool.");
        }

        if (method == "GET" || matches.All(m => EndpointRules.ReadOnlyPosts.Any(r => r.Covers(method, m.Template))))
        {
            return new RequestDecision(RequestKind.Read);
        }

        return new RequestDecision(RequestKind.Write, SupportsDryRun: matches.All(m => m.SupportsDryRun));
    }

    private static RequestDecision Refuse(EndpointRule rule) => new(RequestKind.Refused, $"Not available through this tool: {rule.Reason}.");

    private static bool TakesDryRun(JsonElement root, JsonElement operation)
    {
        if (!operation.TryGetProperty("parameters", out var parameters) || parameters.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var entry in parameters.EnumerateArray())
        {
            var parameter = entry;
            if (parameter.TryGetProperty("$ref", out var reference) && reference.GetString() is { } pointer && !SpecGraph.TryFind(root, pointer, out parameter))
            {
                continue;
            }

            if (parameter.TryGetProperty("name", out var name) && name.ValueEquals("dryRun") && parameter.TryGetProperty("in", out var place) && place.ValueEquals("query"))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A path the router would read exactly as written: under <c>/api/v1/</c>, segments of unreserved characters, no empty segment, no dot segment, no
    /// percent-encoding (which the router decodes after this check could have judged it), no trailing slash.
    /// </summary>
    private static bool IsPlainPath(string path, out string[] segments)
    {
        segments = [];
        if (path.Length is 0 or > 512 || !path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) || path[^1] == '/')
        {
            return false;
        }

        segments = path[1..].Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment is "." or ".." || !segment.All(IsSegmentChar))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSegmentChar(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '.' or '_' or '~' or ':' or '@';

    private sealed record Operation(string Method, string Template, string[] TemplateSegments, bool SupportsDryRun)
    {
        public bool Matches(string[] segments)
        {
            if (segments.Length != TemplateSegments.Length)
            {
                return false;
            }

            for (var i = 0; i < segments.Length; i++)
            {
                var template = TemplateSegments[i];
                var isParameter = template.Length > 2 && template[0] == '{' && template[^1] == '}';
                if (!isParameter && !string.Equals(template, segments[i], StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
