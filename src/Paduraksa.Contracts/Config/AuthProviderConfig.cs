using System.Text.Json.Serialization;

using Paduraksa.Core.Model;

namespace Paduraksa.Contracts.Config;

/// <summary>
/// A named way to ask who a request is. Today that is a forward-auth endpoint that checks every request before it is
/// forwarded. It is a top-level object, so one provider can guard several hosts and routes. It holds no secret: the session lives
/// in the browser's cookie and the endpoint is the operator's own service.
/// </summary>
/// <param name="Id">Identity.</param>
/// <param name="Name">Human label, unique ignoring case.</param>
/// <param name="Version">Concurrency version.</param>
/// <param name="Notes">Free text.</param>
/// <param name="ForwardAuth">The forward-auth endpoint. Required today. A later kind (native OIDC) will be another property, and exactly one is set.</param>
public sealed record AuthProviderConfig(
    AuthProviderId Id,
    string Name,
    long Version = 0,
    string? Notes = null,
    ForwardAuthConfig? ForwardAuth = null);

/// <summary>
/// A forward-auth endpoint. For each request the proxy sends the endpoint a <c>GET</c> that carries the client's credentials and the original
/// request in <c>X-Forwarded-Method</c>, <c>-Proto</c>, <c>-Host</c>, <c>-Uri</c> and <c>-For</c>. A 2xx lets the request through with the
/// configured response headers copied upstream, a 401, 403 or redirect is relayed to the client, and anything else (or a timeout) refuses it.
/// A field left null takes the preset's value (<see cref="ForwardAuthPresets"/>); <see cref="ForwardAuthPresets.Resolve"/> is the one
/// place that decides which.
/// </summary>
/// <param name="Preset">Which provider this is; null is <see cref="ForwardAuthPreset.Custom"/>.</param>
/// <param name="Url">The endpoint, an absolute <c>http</c> or <c>https</c> URL with no credentials. For a preset the bare address
/// (<c>http://authelia:9091</c>) is enough: the preset's path is added when the URL has none.</param>
/// <param name="TimeoutSeconds">How long to wait for the answer; null = <see cref="ForwardAuthLimits.DefaultTimeoutSeconds"/>. A timeout refuses the request (504).</param>
/// <param name="CacheSeconds">How long an allow is remembered; null = <see cref="ForwardAuthLimits.DefaultCacheSeconds"/>, 0 = never. A session the provider has
/// revoked keeps working for that long.</param>
/// <param name="CacheScope">What a remembered allow covers; null = the preset's. <c>host</c> reuses an allow for every path only if the provider named
/// someone (copied an identity header); an allow without one is remembered for its own method and path.</param>
/// <param name="RequestHeaders">Client headers sent to the endpoint besides the standard ones (cookies, authorization, user agent, accept and the
/// <c>X-Forwarded-*</c> set); null = none.</param>
/// <param name="ResponseHeaders">The endpoint's response headers that the proxy copies to the upstream when the endpoint allows a request, after removing them from the client's own
/// request. A trailing <c>*</c> makes a name a prefix. Null = the preset's; an empty list = none. <c>Authorization</c>, <c>Cookie</c> and
/// <c>Proxy-Authorization</c> can't be listed: the client's own are forwarded unchanged.</param>
/// <param name="SignIn">Where a browser without a session is sent when the endpoint answers 401; null = the preset's. Without one the proxy relays the 401.</param>
public sealed record ForwardAuthConfig(
    string Url,
    ForwardAuthPreset? Preset = null,
    int? TimeoutSeconds = null,
    int? CacheSeconds = null,
    ForwardAuthCacheScope? CacheScope = null,
    IReadOnlyList<string>? RequestHeaders = null,
    IReadOnlyList<string>? ResponseHeaders = null,
    ForwardAuthSignInConfig? SignIn = null);

/// <summary>A sign-in page a browser is redirected to, with the address it came from in a query parameter.</summary>
/// <param name="Url">Absolute, or a path on the protected host (<c>/oauth2/start</c>).</param>
/// <param name="ReturnParameter">The query parameter that carries the original URL (<c>rd</c>); null = <c>rd</c>.</param>
public sealed record ForwardAuthSignInConfig(string Url, string? ReturnParameter = null);

/// <summary>Which auth provider a layer applies. <see cref="AuthProviderId"/> names a provider the document declares. Both fields inherit on their
/// own, so a host can turn an inherited provider off and still remember which one it was, and a route can inherit its host's provider, name another or turn
/// it off (the route that serves the provider's own sign-in pages must). If a layer names a provider without stating <see cref="Enabled"/>,
/// the switch takes the built-in default, which is on.</summary>
public sealed record AuthProviderSettings : ISettingsLayer
{
    /// <summary>Ask the resolved provider. Built-in default: on. Off lifts an inherited provider.</summary>
    public bool? Enabled { get; init; }

    /// <summary>The provider to ask; null = inherit. With <see cref="Enabled"/> off the reference is kept but inert.</summary>
    public AuthProviderId? AuthProviderId { get; init; }

    [JsonIgnore]
    public bool SetsNothing => Enabled is null && AuthProviderId is null;
}
