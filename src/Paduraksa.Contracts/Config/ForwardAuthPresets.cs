using Paduraksa.Core.Model;

namespace Paduraksa.Contracts.Config;

/// <summary>
/// What each forward-auth preset assumes, and the one function that turns a <see cref="ForwardAuthConfig"/> into the values the proxy uses:
/// the validator, the mapping and the dashboard all read it, so a preset means one thing everywhere.
/// </summary>
public static class ForwardAuthPresets
{
    /// <summary>The query parameter that carries the original URL to a sign-in page, where the config names none.</summary>
    public const string DefaultReturnParameter = "rd";

    /// <summary>Client headers every request to an endpoint carries: the credentials it decides on and what it uses to tell a browser from a script.
    /// The rest of the client's headers (and every hop-by-hop one) are not sent.</summary>
    public static IReadOnlyList<string> StandardRequestHeaders => ForwardAuthLimits.StandardRequestHeaders;

    /// <summary>The path a preset's endpoint has; null for <see cref="ForwardAuthPreset.Custom"/>, whose URL is the whole address.</summary>
    public static string? DefaultPath(ForwardAuthPreset preset) => preset switch
    {
        ForwardAuthPreset.Authentik => "/outpost.goauthentik.io/auth/traefik",
        ForwardAuthPreset.Authelia => "/api/authz/forward-auth",
        ForwardAuthPreset.OAuth2Proxy => "/oauth2/auth",
        _ => null,
    };

    /// <summary>The identity headers the provider returns, which are copied upstream and stripped from the client's own request (a trailing <c>*</c> is a prefix).</summary>
    public static IReadOnlyList<string> DefaultResponseHeaders(ForwardAuthPreset preset) => preset switch
    {
        ForwardAuthPreset.Authentik => ["X-authentik-*"],
        ForwardAuthPreset.Authelia => ["Remote-Email", "Remote-Groups", "Remote-Name", "Remote-User"],
        ForwardAuthPreset.OAuth2Proxy => ["X-Auth-Request-Email", "X-Auth-Request-Groups", "X-Auth-Request-Preferred-Username", "X-Auth-Request-User"],
        _ => [],
    };

    /// <summary>The preset's cache scope: Authelia decides per path and method, authentik and oauth2-proxy per application.</summary>
    public static ForwardAuthCacheScope DefaultCacheScope(ForwardAuthPreset preset) => preset switch
    {
        ForwardAuthPreset.Authentik or ForwardAuthPreset.OAuth2Proxy => ForwardAuthCacheScope.Host,
        _ => ForwardAuthCacheScope.Request,
    };

    /// <summary>The preset's sign-in redirect: oauth2-proxy answers an unauthenticated <c>/oauth2/auth</c> with a bare 401 and expects the
    /// proxy to send the browser to <c>/oauth2/start</c>; the others answer with their own redirect or page.</summary>
    public static ForwardAuthSignInConfig? DefaultSignIn(ForwardAuthPreset preset) =>
        preset == ForwardAuthPreset.OAuth2Proxy ? new ForwardAuthSignInConfig("/oauth2/start", DefaultReturnParameter) : null;

    /// <summary>
    /// <paramref name="config"/> with every default filled in. The URL gets the preset's path when it names none (a bare address, or a lone
    /// <c>/</c>); an unparseable URL is left as it is, for the validator to report.
    /// </summary>
    public static ResolvedForwardAuth Resolve(ForwardAuthConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var preset = config.Preset ?? ForwardAuthPreset.Custom;
        var url = config.Url ?? string.Empty;
        if (DefaultPath(preset) is { } path && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0 && !url.Contains('?', StringComparison.Ordinal))
        {
            url = uri.GetLeftPart(UriPartial.Authority) + path;
        }

        var signIn = config.SignIn ?? DefaultSignIn(preset);
        return new ResolvedForwardAuth(
            preset,
            url,
            TimeSpan.FromSeconds(config.TimeoutSeconds ?? ForwardAuthLimits.DefaultTimeoutSeconds),
            TimeSpan.FromSeconds(config.CacheSeconds ?? ForwardAuthLimits.DefaultCacheSeconds),
            config.CacheScope ?? DefaultCacheScope(preset),
            [.. (config.RequestHeaders ?? []).Order(StringComparer.OrdinalIgnoreCase)],
            [.. (config.ResponseHeaders ?? DefaultResponseHeaders(preset)).Order(StringComparer.OrdinalIgnoreCase)],
            signIn?.Url,
            signIn is null ? null : signIn.ReturnParameter ?? DefaultReturnParameter);
    }
}

/// <summary>A forward-auth endpoint with the preset's defaults filled in: what the proxy compiles.</summary>
/// <param name="Preset">The provider.</param>
/// <param name="Url">The full endpoint URL.</param>
/// <param name="Timeout">How long to wait for the answer.</param>
/// <param name="CacheTtl">How long an allow is remembered; zero = not at all.</param>
/// <param name="CacheScope">What a remembered allow covers.</param>
/// <param name="RequestHeaders">Extra client headers sent to the endpoint, sorted ignoring case.</param>
/// <param name="ResponseHeaders">The headers copied upstream and stripped from the client's request, sorted ignoring case.</param>
/// <param name="SignInUrl">Where an unauthenticated browser is sent; null = the 401 is relayed.</param>
/// <param name="SignInReturnParameter">The query parameter that carries the original URL; null when there is no sign-in URL.</param>
public sealed record ResolvedForwardAuth(
    ForwardAuthPreset Preset,
    string Url,
    TimeSpan Timeout,
    TimeSpan CacheTtl,
    ForwardAuthCacheScope CacheScope,
    IReadOnlyList<string> RequestHeaders,
    IReadOnlyList<string> ResponseHeaders,
    string? SignInUrl,
    string? SignInReturnParameter);
