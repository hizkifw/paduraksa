using System.Buffers;

using Paduraksa.Core.Hosting;

namespace Paduraksa.Core.Model;

/// <summary>
/// Which custom header rules a layer may hold, shared by the validator, the compiler and the dashboard
/// (docs/design/hot-path.md "Custom headers"). A rule may not touch a header the proxy owns: the framing and hop-by-hop
/// headers in both directions, and per direction the ones another setting decides (the request's <c>Host</c>, forwarding,
/// request-ID and Cloudflare headers; the response's HSTS, <c>Alt-Svc</c> and request ID).
/// <c>Server</c> stays allowed on the response: a custom rule there is applied after the built-in one, so it wins.
/// </summary>
public static class CustomHeaderRules
{
    /// <summary>Most rules one layer may hold per direction.</summary>
    public const int MaxRules = 32;

    /// <summary>Longest header name.</summary>
    public const int MaxNameLength = 64;

    /// <summary>Longest value (a <c>Content-Security-Policy</c> runs to a few KiB).</summary>
    public const int MaxValueLength = 4096;

    private static readonly SearchValues<char> TokenChars =
        SearchValues.Create("!#$%&'*+-.^_`|~0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ");

    /// <summary>Framing and hop-by-hop headers: meaningful for one connection only, or the body's own length and coding.</summary>
    private static readonly string[] Framing =
    [
        "Connection", "Content-Encoding", "Content-Length", "Expect", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "Proxy-Connection", "TE", "Trailer",
        "Transfer-Encoding", "Upgrade",
    ];

    private static readonly string[] RequestOwned =
    [
        "Host", "Forwarded", "X-Real-IP", "X-Request-Id", "True-Client-IP", AdminInternalListener.HeaderName,
    ];

    private static readonly string[] ResponseOwned = ["Strict-Transport-Security", "Alt-Svc", "X-Request-Id"];

    /// <summary>
    /// Whether a rule's value looks like a credential: <c>Authorization</c>, <c>Proxy-Authorization</c>, <c>Cookie</c>, or a name
    /// ending in <c>-Key</c>, <c>-Token</c> or <c>-Secret</c>. Rule values are config, stored and exported like any setting, not vault
    /// secrets; this only keeps them out of what the simulator reports (and so out of what an MCP script reads from a trace).
    /// </summary>
    public static bool LooksSecret(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("-Key", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("-Token", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("-Secret", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Why a well-named rule can't do <paramref name="operation"/>, or null when it can. A response <c>Set-Cookie</c> only takes
    /// <see cref="HeaderOperation.Add"/>: a set or a remove would take off every cookie the upstream sets and the session cookie of
    /// the host's sign-in, which the proxy writes on the same response.
    /// </summary>
    public static string? OperationProblem(string? name, HeaderOperation operation, bool response) =>
        response && operation != HeaderOperation.Add && string.Equals(name, "Set-Cookie", StringComparison.OrdinalIgnoreCase)
            ? "a Set-Cookie rule can only add a cookie: a set or a remove would take the upstream's cookies and the sign-in session's off"
            : null;

    /// <summary>Why <paramref name="name"/> can't be the name of a rule for that direction, or null when it can.</summary>
    public static string? NameProblem(string? name, bool response)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "a header name can't be empty";
        }

        if (name.Length > MaxNameLength)
        {
            return $"a header name can be at most {MaxNameLength} characters";
        }

        if (name.AsSpan().ContainsAnyExcept(TokenChars))
        {
            return $"'{name}' isn't a valid header name (letters, digits and !#$%&'*+-.^_`|~ only)";
        }

        if (Owner(name, response) is { } owner)
        {
            return $"'{name}' is {owner}";
        }

        return null;
    }

    /// <summary>Why <paramref name="value"/> can't be sent, or null when it can: 1 to <see cref="MaxValueLength"/> visible ASCII
    /// characters, spaces or tabs, with no leading or trailing whitespace (no CR or LF, so a value can't start another header).</summary>
    public static string? ValueProblem(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "a set or add rule needs a value";
        }

        if (value.Length > MaxValueLength)
        {
            return $"a header value can be at most {MaxValueLength} characters";
        }

        foreach (var c in value)
        {
            if (c is not ((>= ' ' and <= '~') or '\t'))
            {
                return "a header value takes visible ASCII characters, spaces and tabs only (no line breaks)";
            }
        }

        return value[0] is ' ' or '\t' || value[^1] is ' ' or '\t' ? "a header value can't start or end with whitespace" : null;
    }

    /// <summary>Whether a well-formed <paramref name="name"/> is one the proxy owns in that direction.</summary>
    public static bool IsReserved(string? name, bool response) => name is not null && Owner(name, response) is not null;

    /// <summary>What owns <paramref name="name"/> in that direction, as a phrase, or null when a rule may touch it.</summary>
    private static string? Owner(string name, bool response)
    {
        foreach (var framing in Framing)
        {
            if (string.Equals(name, framing, StringComparison.OrdinalIgnoreCase))
            {
                return "a framing, body-coding or hop-by-hop header the proxy manages";
            }
        }

        if (response)
        {
            foreach (var owned in ResponseOwned)
            {
                if (string.Equals(name, owned, StringComparison.OrdinalIgnoreCase))
                {
                    return "a header the proxy sets from its own setting (HSTS, HTTP/3 or the request ID)";
                }
            }

            // A static Location would turn every answer of the host into a redirect, the proxy's own redirects (the HTTPS redirect,
            // a Redirect host, the proof-of-work return) included, and override a route's Location mapping.
            if (string.Equals(name, "Location", StringComparison.OrdinalIgnoreCase))
            {
                return "set by the upstream's and the proxy's own redirects";
            }

            return null;
        }

        if (string.Equals(name, "Host", StringComparison.OrdinalIgnoreCase))
        {
            return "set by the upstream's Host header mode";
        }

        // The forwarding headers are the gate's resolution of the client, and a Cloudflare header passes only from a trusted
        // edge: a rule that set either would let configuration forge what the upstream trusts.
        if (name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase) || name.StartsWith("cf-", StringComparison.OrdinalIgnoreCase))
        {
            return "a forwarding header the proxy sets from its trusted-proxy handling";
        }

        foreach (var owned in RequestOwned)
        {
            if (string.Equals(name, owned, StringComparison.OrdinalIgnoreCase))
            {
                return "a header the proxy sets itself";
            }
        }

        return null;
    }
}
