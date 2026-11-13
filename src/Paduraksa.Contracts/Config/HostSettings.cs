using System.Text.Json.Serialization;
using Paduraksa.Core.Model;

namespace Paduraksa.Contracts.Config;

/// <summary>
/// One layer of host settings. Every field is nullable and null (absent in JSON) means
/// <i>inherit</i>: the compiler resolves built-in defaults → global defaults → profile → host → route. Nested settings
/// (<see cref="Tls"/>, <see cref="Hsts"/>, <see cref="ServerHeader"/>, <see cref="ProofOfWork"/>, <see cref="RateLimit"/>,
/// <see cref="Robots"/>, <see cref="Cloudflare"/>, <see cref="AccessList"/>, <see cref="RequestLimits"/>, <see cref="RuleSet"/>, <see cref="AuthProvider"/>, <see cref="ErrorPages"/>, <see cref="Maintenance"/>, <see cref="Headers"/>, <see cref="Compression"/>) merge field by field.
/// </summary>
/// <remarks>A new setting is a new nullable field at the end: stored documents simply don't set it.</remarks>
public sealed record HostSettings
{
    /// <summary>Redirect plain-HTTP requests to HTTPS before anything else. Built-in default: off.</summary>
    public bool? ForceHttps { get; init; }

    /// <summary>Merged field by field with the layers below.</summary>
    public TlsSettings? Tls { get; init; }

    /// <summary>Merged field by field with the layers below.</summary>
    public HstsSettings? Hsts { get; init; }

    /// <summary>
    /// Write a line for every request of this host to the access log. Built-in default: on. A host that turns
    /// it off is still proxied, and its requests still carry a request ID; they just aren't logged (the drop counter and
    /// the error log are unaffected).
    /// </summary>
    public bool? AccessLog { get; init; }

    /// <summary>
    /// Where the request ID travels (<see cref="RequestIdPropagation"/>). Built-in default: upstream and client, which is
    /// what correlates a request across the proxy's log, the app's log and an error page.
    /// </summary>
    public RequestIdPropagation? RequestIdPropagation { get; init; }

    /// <summary>Merged field by field with the layers below. Built-in default: <c>Paduraksa</c>, with no version.</summary>
    public ServerHeaderSettings? ServerHeader { get; init; }

    /// <summary>Merged field by field with the layers below. Built-in default: off.</summary>
    public ProofOfWorkSettings? ProofOfWork { get; init; }

    /// <summary>Merged field by field with the layers below. Built-in default: none.</summary>
    public RateLimitSettings? RateLimit { get; init; }

    /// <summary>Merged field by field with the layers below. Built-in default: pass through to the upstream.</summary>
    public RobotsSettings? Robots { get; init; }

    /// <summary>Merged field by field with the layers below. Built-in default: no lockdown, no Authenticated Origin Pulls.</summary>
    public CloudflareSettings? Cloudflare { get; init; }

    /// <summary>Merged field by field with the layers below. Built-in default: none.</summary>
    public AccessListSettings? AccessList { get; init; }

    /// <summary>Merged field by field with the layers below. Built-in default: 100 MiB of body, 100 header lines, 32 KiB of
    /// headers, an 8 KiB request target.</summary>
    public RequestLimitsSettings? RequestLimits { get; init; }

    /// <summary>Merged field by field with the layers below. Built-in default: none.</summary>
    public RuleSetSettings? RuleSet { get; init; }

    /// <summary>Merged field by field with the layers below. Built-in default: none.</summary>
    public AuthProviderSettings? AuthProvider { get; init; }

    /// <summary>Merged entry by entry with the layers below. Built-in default: the built-in pages, and an upstream's own error bodies untouched.</summary>
    public ErrorPagesSettings? ErrorPages { get; init; }

    /// <summary>Merged field by field with the layers below. Built-in default: off.</summary>
    public MaintenanceSettings? Maintenance { get; init; }

    /// <summary>Merged rule by rule (keyed on the header name) with the layers below. Built-in default: none.</summary>
    public HeadersSettings? Headers { get; init; }

    /// <summary>Merged field by field with the layers below. Built-in default: off.</summary>
    public CompressionSettings? Compression { get; init; }
}

/// <summary>
/// A nested settings record: one that merges field by field with the same record in the layers below. One that sets none of
/// its fields is no layer at all, so canonical JSON writes no key for it and a stored revision keeps the bytes it had
/// before the record existed. Every field belongs in <see cref="SetsNothing"/>; a new one that is left out of it would be
/// dropped from a document that sets it.
/// </summary>
public interface ISettingsLayer
{
    /// <summary>True when this record sets none of its fields. Never part of a document.</summary>
    [JsonIgnore]
    bool SetsNothing { get; }
}

/// <summary>The route layer: the route-level subset of <see cref="HostSettings"/>, on top of the host's resolved
/// settings. Null fields inherit from the host.</summary>
public sealed record RouteSettings
{
    /// <summary>
    /// The route-level subset of <see cref="ProofOfWorkSettings"/>: only <see cref="ProofOfWorkSettings.Enabled"/> is
    /// accepted here. It overrides the host's resolved switch either way, so one path can be exempt or protected.
    /// </summary>
    public ProofOfWorkSettings? ProofOfWork { get; init; }

    /// <summary>
    /// The route-level subset of <see cref="RateLimitSettings"/>: only <see cref="RateLimitSettings.Enabled"/> and
    /// <see cref="RateLimitSettings.PolicyId"/> are accepted here, field by field over the host's resolved values. So a
    /// route can inherit its host's policy, name another one, or turn the limit off for its own paths.
    /// </summary>
    public RateLimitSettings? RateLimit { get; init; }

    /// <summary>Field by field over the host's resolved list: a route inherits the host's, names another, or turns the list
    /// off for its own paths.</summary>
    public AccessListSettings? AccessList { get; init; }

    /// <summary>
    /// The route-level subset of <see cref="RequestLimitsSettings"/>: only <see cref="RequestLimitsSettings.MaxBodySize"/> is
    /// accepted here, over the host's resolved value, so an upload route can take more (or less) than the rest of the host.
    /// The header and URL limits are judged before a route is known, so they belong to the host.
    /// </summary>
    public RequestLimitsSettings? RequestLimits { get; init; }

    /// <summary>Field by field over the host's resolved set: a route inherits the host's, names another, or turns the rules off
    /// for its own paths.</summary>
    public RuleSetSettings? RuleSet { get; init; }

    /// <summary>Field by field over the host's resolved provider: a route inherits the host's, names another, or turns authentication off
    /// for its own paths (the route that serves the provider's sign-in pages has to).</summary>
    public AuthProviderSettings? AuthProvider { get; init; }

    /// <summary>Rule by rule over the host's resolved rules: a route's rule for a header name replaces the host's for it.</summary>
    public HeadersSettings? Headers { get; init; }
}

/// <summary>
/// Compressing the upstream's responses with Brotli or gzip, for a client whose <c>Accept-Encoding</c> takes one. Only a
/// text-like content type (HTML, CSS, JavaScript, JSON, XML, SVG, plain text, WebAssembly) over <see cref="MinimumSize"/>, and
/// never a response the upstream already encoded, a range, an event stream or the proxy's own answers. Each field inherits on
/// its own.
/// </summary>
public sealed record CompressionSettings : ISettingsLayer
{
    /// <summary>Compress. Built-in default: off: a response that carries a secret next to input an attacker controls leaks the
    /// secret through its compressed size (BREACH), and the proxy can't tell which responses do.</summary>
    public bool? Enabled { get; init; }

    /// <summary>The smallest declared body that is compressed, in bytes, 0 to 1 MiB; a body without a declared length is always
    /// compressed. Built-in default: 1 KiB, below which the framing outweighs the saving.</summary>
    public int? MinimumSize { get; init; }

    [JsonIgnore]
    public bool SetsNothing => Enabled is null && MinimumSize is null;
}

/// <summary>TLS settings; each field inherits on its own. A layer that sets none is the same as no layer.</summary>
public sealed record TlsSettings : ISettingsLayer
{
    /// <summary>Lowest TLS version over TCP (QUIC is always 1.3). Built-in default: TLS 1.2.</summary>
    public TlsMinimumVersion? MinimumVersion { get; init; }

    /// <summary>ALPN offered over TCP. Built-in default: HTTP/2 and HTTP/1.1.</summary>
    public TcpAlpn? TcpAlpn { get; init; }

    /// <summary>Serve over HTTP/3 when the listener exists. Built-in default: on.</summary>
    public bool? Http3 { get; init; }

    [JsonIgnore]
    public bool SetsNothing => MinimumVersion is null && TcpAlpn is null && Http3 is null;
}

/// <summary>
/// HTTP Strict Transport Security: the host's HTTPS responses tell a browser to keep the host on HTTPS for
/// <see cref="MaxAge"/> seconds, and with <see cref="IncludeSubDomains"/> every subdomain too. Each field inherits on its
/// own. Paduraksa sends it only to HTTPS requests, and never for an IP literal or a loopback name.
/// </summary>
public sealed record HstsSettings : ISettingsLayer
{
    /// <summary>Send the header at all. Built-in default: off, so nothing is pinned on a browser's behalf until it is
    /// turned on.</summary>
    public bool? Enabled { get; init; }

    /// <summary>Seconds a browser remembers the host as HTTPS-only (<c>max-age</c>), 0 to 63072000 (two years). Built-in
    /// default: one year (31536000). 0 is valid, and tells a browser that already remembers the host to forget it.</summary>
    public int? MaxAge { get; init; }

    /// <summary>Cover every subdomain too (<c>includeSubDomains</c>). Built-in default: off, because a subdomain that has
    /// no certificate of its own stops working over plain HTTP. A browser begins to trust the setting when it sees it, and
    /// keeps it for <see cref="MaxAge"/>, so a wrong one is felt for as long as that.</summary>
    public bool? IncludeSubDomains { get; init; }

    /// <summary>Ask the browser vendors' preload lists to ship the host as HTTPS-only (<c>preload</c>). Built-in default:
    /// off. It needs <see cref="IncludeSubDomains"/> and a <see cref="MaxAge"/> of at least a year, and it is hard to undo:
    /// browsers that ship the list keep refusing plain HTTP even after the header stops.</summary>
    public bool? Preload { get; init; }

    [JsonIgnore]
    public bool SetsNothing => Enabled is null && MaxAge is null && IncludeSubDomains is null && Preload is null;
}

/// <summary>
/// What the host's responses say in <c>Server</c>. <see cref="Mode"/> and <see cref="Value"/> inherit on
/// their own, like every other nested setting.
/// </summary>
public sealed record ServerHeaderSettings : ISettingsLayer
{
    /// <summary>Which header wins. Built-in default: <see cref="ServerHeaderMode.Paduraksa"/>, the name without a
    /// version.</summary>
    public ServerHeaderMode? Mode { get; init; }

    /// <summary>The header <see cref="ServerHeaderMode.Custom"/> sends: 1-128 visible ASCII characters or spaces, with no
    /// leading or trailing space. Required once the resolved mode is <see cref="ServerHeaderMode.Custom"/>; ignored by
    /// every other mode.</summary>
    public string? Value { get; init; }

    [JsonIgnore]
    public bool SetsNothing => Mode is null && Value is null;
}

/// <summary>
/// The browser check: a visitor without a valid token gets a small page that solves a SHA-256 proof of work, then a
/// signed, expiring cookie bound to this host and client address lets it through. Each field inherits on its own.
/// </summary>
public sealed record ProofOfWorkSettings : ISettingsLayer
{
    /// <summary>Challenge visitors. Built-in default: off.</summary>
    public bool? Enabled { get; init; }

    /// <summary>Leading zero bits of SHA-256 a solution needs, 8 to 28. Built-in default: 16, well under a second on an
    /// ordinary laptop.</summary>
    public int? Difficulty { get; init; }

    /// <summary>How long the cookie lasts, in hours, 1 to 720. Built-in default: 168 (seven days).</summary>
    public int? TokenLifetimeHours { get; init; }

    [JsonIgnore]
    public bool SetsNothing => Enabled is null && Difficulty is null && TokenLifetimeHours is null;
}

/// <summary>
/// Which rate-limit policy a layer applies. <see cref="PolicyId"/> names a policy the document declares; it inherits
/// field by field, so a host can turn an inherited policy off without forgetting which one it was, and a route can
/// inherit its host's, name another, or turn it off. A layer that names a policy without stating
/// <see cref="Enabled"/> leaves the switch to the built-in default, which is on: naming a policy is the whole job.
/// </summary>
public sealed record RateLimitSettings : ISettingsLayer
{
    /// <summary>Apply the resolved policy. Built-in default: on. Off turns an inherited policy off.</summary>
    public bool? Enabled { get; init; }

    /// <summary>The policy to apply; null = inherit. With <see cref="Enabled"/> off the reference is kept but inert, so
    /// turning the switch back on needs no second lookup.</summary>
    public RateLimitPolicyId? PolicyId { get; init; }

    [JsonIgnore]
    public bool SetsNothing => Enabled is null && PolicyId is null;
}

/// <summary>
/// What the host answers at <c>/robots.txt</c>. There is no route-level override: a host has one file, and every route
/// shares it. Each field inherits on its own, so a layer can change the mode and keep the text below it.
/// </summary>
public sealed record RobotsSettings : ISettingsLayer
{
    /// <summary>Passthrough, a preset, or the operator's text. Built-in default: passthrough.</summary>
    public RobotsMode? Mode { get; init; }

    /// <summary>
    /// The text <see cref="RobotsMode.Custom"/> serves. Set with that mode at its layer, or inherited from one that set it;
    /// any other mode ignores it. Line endings are normalised when the proxy compiles it, not in the stored document.
    /// </summary>
    public string? CustomText { get; init; }

    [JsonIgnore]
    public bool SetsNothing => Mode is null && CustomText is null;
}

/// <summary>
/// Keeping the host reachable through Cloudflare only. Both fields do nothing unless the global trusted-proxy mode is
/// <c>CloudflareProxy</c> or <c>CloudflareTunnel</c> (the config warns when they are set without it). Each field inherits on
/// its own.
/// </summary>
public sealed record CloudflareSettings : ISettingsLayer
{
    /// <summary>
    /// Refuse a request whose connection peer isn't Cloudflare's edge (<c>CloudflareProxy</c>) or the configured
    /// <c>cloudflared</c> address (<c>CloudflareTunnel</c>), before anything else the host does. Built-in default: off. When
    /// every host in the config has it on, such a peer is dropped before the TLS handshake whichever mode is chosen.
    /// </summary>
    public OriginLockdownMode? OriginLockdown { get; init; }

    /// <summary>
    /// Require Cloudflare's client certificate (Authenticated Origin Pulls) on the TLS handshake, checked against
    /// Cloudflare's origin-pull CA. Built-in default: off. Cloudflare doesn't connect to origins over HTTP/3, so a host with
    /// this on is not served over it, and it needs the setting turned on in the zone's Cloudflare dashboard too.
    /// </summary>
    public bool? AuthenticatedOriginPulls { get; init; }

    [JsonIgnore]
    public bool SetsNothing => OriginLockdown is null && AuthenticatedOriginPulls is null;
}

/// <summary>
/// How much of a request a host takes. Each field inherits on its own. A request over a limit is refused before the upstream
/// sees it: 413 for the body, 431 for the headers, 414 for the request target. Sizes are bytes.
/// </summary>
public sealed record RequestLimitsSettings : ISettingsLayer
{
    /// <summary>Largest request body in bytes; 0 is no limit. Built-in default: 100 MiB. Over it is 413: at once when the request declares
    /// its size, mid-upload when it doesn't. A route may set its own.</summary>
    public long? MaxBodySize { get; init; }

    /// <summary>Most header lines a request may carry, 8 to 128. Built-in default: 100. Host-wide: a route can't set it.</summary>
    public int? MaxHeaderCount { get; init; }

    /// <summary>Most bytes the header names and values may take together, 1 KiB to 64 KiB. Built-in default: 32 KiB.
    /// Host-wide: a route can't set it.</summary>
    public int? MaxHeaderBytes { get; init; }

    /// <summary>Longest request target (path and query, as sent), in bytes, 256 to 16 KiB. Built-in default: 8 KiB. Host-wide:
    /// a route can't set it.</summary>
    public int? MaxUrlLength { get; init; }

    [JsonIgnore]
    public bool SetsNothing => MaxBodySize is null && MaxHeaderCount is null && MaxHeaderBytes is null && MaxUrlLength is null;
}
