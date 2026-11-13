using System.Text.Json.Serialization;

using Paduraksa.Core.Json;
using Paduraksa.Core.Model;

namespace Paduraksa.Contracts.Config;

/// <summary>A host: the names it answers for and what it does with requests.</summary>
/// <param name="Id">Identity.</param>
/// <param name="Names">Exact names, <c>*.parent</c> wildcards or IP literals; a set. Canonical form: normalised
/// (trimmed, lowercase, IDN → punycode, no trailing dot), sorted ordinally, no duplicates. Unique across enabled
/// hosts.</param>
/// <param name="Version">Concurrency version.</param>
/// <param name="Kind">What the host does with requests.</param>
/// <param name="Enabled">False: validated, but serves nothing; its names may then repeat an enabled host's.</param>
/// <param name="ProfileId">The profile layer, between the global defaults and this host; null = none.</param>
/// <param name="Certificate">Which certificate this host's names are served with; null = <see cref="CertificateBindingMode.Auto"/>.</param>
/// <param name="Settings">The host layer of settings; unset fields inherit.</param>
/// <param name="Routes">Proxy hosts only (at least one). The most specific path wins, whatever the list order: exact
/// routes before prefixes, then a longer prefix before a shorter one, then <see cref="RouteConfig.Order"/>, then the
/// listed order. Canonical order: that evaluation order.</param>
/// <param name="Redirect">Redirect hosts only (required).</param>
/// <param name="Respond">Respond hosts only (required).</param>
/// <param name="Tags">Free-form labels for filtering; a set. Canonical form: trimmed, lowercase, sorted, no
/// duplicates.</param>
/// <param name="Notes">Free text.</param>
public sealed record HostConfig(
    HostId Id,
    IReadOnlyList<string> Names,
    long Version = 0,
    HostKind Kind = HostKind.Proxy,
    bool Enabled = true,
    ProfileId? ProfileId = null,
    CertificateBinding? Certificate = null,
    HostSettings? Settings = null,
    IReadOnlyList<RouteConfig>? Routes = null,
    RedirectConfig? Redirect = null,
    RespondConfig? Respond = null,
    IReadOnlyList<string>? Tags = null,
    string? Notes = null)
{
    public CertificateBinding Certificate { get; init; } = Certificate ?? new();

    public HostSettings Settings { get; init; } = Settings ?? new();

    public IReadOnlyList<RouteConfig> Routes { get; init; } = Routes ?? [];

    public IReadOnlyList<string> Tags { get; init; } = Tags ?? [];
}

/// <summary>A host's certificate binding.</summary>
public sealed record CertificateBinding
{
    public CertificateBindingMode Mode { get; init; }

    /// <summary>Set exactly when <see cref="Mode"/> is <see cref="CertificateBindingMode.Specific"/>.</summary>
    public CertificateId? CertificateId { get; init; }
}

/// <summary>How a host picks its certificate.</summary>
[JsonConverter(typeof(ConfigEnumConverter<CertificateBindingMode>))]
public enum CertificateBindingMode
{
    /// <summary>A certificate that covers the host's names, else the default certificate.</summary>
    Auto,

    /// <summary>The certificate in <see cref="CertificateBinding.CertificateId"/>.</summary>
    Specific,

    /// <summary>No HTTPS for this host.</summary>
    None,
}

/// <summary>One route of a proxy host.</summary>
/// <param name="Id">Identity; unique across all hosts.</param>
/// <param name="UpstreamId">The upstream to proxy to.</param>
/// <param name="Order">A specificity tiebreaker: lower runs first between routes of equal match kind and path length;
/// equal orders keep their listed order.</param>
/// <param name="Match">How <paramref name="Path"/> matches.</param>
/// <param name="Path">Starts with <c>/</c>; decoded and canonical (no <c>\</c>, <c>//</c>, <c>;</c>, dot segments,
/// percent-encoding, query or fragment). Matching ignores case, but the path keeps the case it was written in; two
/// routes of a host with the same match kind can't have paths that differ only in case.</param>
/// <param name="Settings">The route layer: the route-level subset of the host settings.</param>
/// <param name="Rewrite">How the path the upstream receives differs from the client's; null forwards it as received.</param>
public sealed record RouteConfig(
    RouteId Id,
    UpstreamId UpstreamId,
    int Order = 0,
    RouteMatchKind Match = RouteMatchKind.Prefix,
    string Path = "/",
    RouteSettings? Settings = null,
    PathRewriteConfig? Rewrite = null)
{
    public RouteSettings Settings { get; init; } = Settings ?? new();
}

/// <summary>
/// The path a route forwards: the client's, with the route's own path taken off the front, a fixed prefix put on, or both
/// (<c>/app/x</c> becomes <c>/x</c>, or <c>/api/x</c>). It works on the path as the client sent it, without decoding or
/// re-encoding it, and the query is untouched. With <see cref="StripPrefix"/> the upstream is told the stripped prefix in
/// <c>X-Forwarded-Prefix</c>, and a <c>Location</c> it answers with that points into its own paths gets the client's
/// prefix back.
/// </summary>
/// <param name="StripPrefix">Take the route's path off the front. A request that spells the prefix differently from the
/// route (encoded, with <c>//</c>, <c>;</c> or dot segments) can't be stripped without guessing, and gets 404. Not on a route
/// whose path is <c>/</c>.</param>
/// <param name="AddPrefix">Put this in front of what is forwarded: starts with <c>/</c>, no trailing <c>/</c>, at most 256
/// characters of a URL path (letters, digits, <c>-._~!$&amp;'()*+,;=:@/</c> and percent escapes), with no empty or dot
/// segments. Sent as written.</param>
/// <param name="RewriteLocation">With <paramref name="StripPrefix"/>: give an upstream's <c>Location</c> the client's prefix back
/// (<c>/login</c> becomes <c>/app/login</c>; one that already starts with <c>/app</c> is left alone). Null is on; turn it off for an
/// upstream that redirects to other routes of the host, which the mapping would put under this one. Only with a strip.</param>
public sealed record PathRewriteConfig(bool StripPrefix = false, string? AddPrefix = null, bool? RewriteLocation = null);

/// <summary>A Redirect host's target.</summary>
/// <param name="Target">Absolute <c>http</c>/<c>https</c> URL in printable ASCII (punycode host, percent-encoded path);
/// it becomes the <c>Location</c> header.</param>
/// <param name="StatusCode">301, 302, 303, 307 or 308.</param>
/// <param name="PreservePath">Append the request's path and query to <paramref name="Target"/> (which then has no
/// query or fragment).</param>
public sealed record RedirectConfig(string Target, int StatusCode = 301, bool PreservePath = true);

/// <summary>A Respond host's fixed answer.</summary>
/// <param name="StatusCode">200-599; 204, 205 and 304 have no body.</param>
/// <param name="Body">Sent as UTF-8.</param>
/// <param name="ContentType">The <c>Content-Type</c> header: visible ASCII, spaces and tabs.</param>
public sealed record RespondConfig(int StatusCode, string Body = "", string ContentType = RespondConfig.DefaultContentType)
{
    public const string DefaultContentType = "text/plain; charset=utf-8";
}
