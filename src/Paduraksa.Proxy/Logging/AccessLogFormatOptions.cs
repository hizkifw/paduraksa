namespace Paduraksa.Proxy.Logging;

/// <summary>
/// What the access-log formatter is allowed to write: whether the query string appears at all, and which
/// query parameters are redacted. Cold-path data, read only by <c>AccessLogFormatter</c> on the drain thread.
/// </summary>
/// <remarks>
/// A parameter whose name matches <see cref="SensitiveQueryKeys"/> (ignoring case) is written as <c>name=REDACTED</c>,
/// with its value replaced whatever it is; a name matched without a value (<c>?token</c>) is written the same way. The
/// names are compared as written, so an unusual spelling of a name is not a bypass: nothing else in a query is sensitive
/// by default (<c>q</c>, <c>page</c>, ...), and a deployment that puts a token in a differently named parameter adds it.
/// </remarks>
public sealed record AccessLogFormatOptions
{
    /// <summary>
    /// Query parameter names whose values never reach the log. Built-in default: the well-known ones; a
    /// configured list replaces it.
    /// </summary>
    public static string[] DefaultSensitiveQueryKeys { get; } =
    [
        "token", "access_token", "refresh_token", "id_token", "code", "password", "passwd", "pwd", "secret",
        "client_secret", "key", "api_key", "apikey", "signature", "sig", "auth", "session",
    ];

    /// <summary>The built-in defaults: the query string is logged, the well-known secret-carrying names are redacted.</summary>
    public static AccessLogFormatOptions Default { get; } = new();

    /// <summary>Log the query string in the request target. Off: the path alone, so no parameter of any name is written.</summary>
    public bool LogQueryString { get; init; } = true;

    /// <summary>The parameter names redacted to <c>name=REDACTED</c> (compared ignoring case).</summary>
    public IReadOnlyList<string> SensitiveQueryKeys { get; init; } = DefaultSensitiveQueryKeys;

    /// <summary>Whether a parameter of this name is redacted. Cold path, so a set lookup is fine.</summary>
    public bool IsSensitive(ReadOnlySpan<char> name)
    {
        foreach (string key in SensitiveQueryKeys)
        {
            if (name.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
