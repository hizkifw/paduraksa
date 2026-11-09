using System.Text.Json.Serialization;

using Paduraksa.Core.Json;
using Paduraksa.Core.Model;

namespace Paduraksa.Contracts.Config;

/// <summary>
/// The whole proxy config (PLAN §6.4): global settings, profiles, upstreams and hosts with their routes. It is the
/// source of truth: each revision stores it as canonical JSON (<see cref="ConfigJsonContext"/>), and startup, rollback,
/// diff and YAML export all read it. It holds references only, never secrets.
/// </summary>
/// <remarks>
/// <para><b>Evolving it.</b> A new field goes at the end of its record's parameters, is nullable, means "as before"
/// when null, and is omitted when null. Then every stored revision still reads, and re-serialises byte for byte, so its
/// hash still matches. (A revision's hash is always the hash of its stored bytes, never of a re-serialisation.) Adding
/// a field never reorders the existing ones. Removing or renaming a field, or changing what an existing value means,
/// needs a new <see cref="ApiVersion"/>. Downgrades are unsupported: reading stays strict, so an older build rejects a
/// document with a field it doesn't know rather than silently dropping it.</para>
/// <para><b>Defaults are constructor defaults.</b> The JSON source generator sets init-only properties in an object
/// initializer, so a property initializer would be overwritten when the property is absent. A field whose default isn't
/// <c>default(T)</c> is therefore a constructor parameter; lists and nested records default to null there and the
/// property turns that into empty.</para>
/// <para><b>Equality.</b> The records hold lists, so <c>==</c> compares them by reference. Two documents mean the same
/// config exactly when their canonical bytes are equal.</para>
/// </remarks>
/// <param name="ApiVersion">Always <see cref="V1"/> for now; required in JSON.</param>
/// <param name="Settings">The global settings.</param>
/// <param name="Profiles">Canonical order: by ID.</param>
/// <param name="Upstreams">Canonical order: by ID.</param>
/// <param name="Hosts">Canonical order: by ID.</param>
public sealed record ConfigDocument(
    [property: JsonRequired] string ApiVersion = ConfigDocument.V1,
    GlobalSettings? Settings = null,
    IReadOnlyList<ProfileConfig>? Profiles = null,
    IReadOnlyList<UpstreamConfig>? Upstreams = null,
    IReadOnlyList<HostConfig>? Hosts = null)
{
    /// <summary>The only <see cref="ApiVersion"/> this build reads and writes.</summary>
    public const string V1 = "paduraksa.dev/v1";

    public GlobalSettings Settings { get; init; } = Settings ?? new();

    public IReadOnlyList<ProfileConfig> Profiles { get; init; } = Profiles ?? [];

    public IReadOnlyList<UpstreamConfig> Upstreams { get; init; } = Upstreams ?? [];

    public IReadOnlyList<HostConfig> Hosts { get; init; } = Hosts ?? [];
}

/// <summary>The global settings: one per document.</summary>
/// <param name="Version">Concurrency version, incremented by the command pipeline on every change (PLAN §6.4).</param>
/// <param name="Defaults">The bottom settings layer: what every host inherits unless its profile, itself or a route
/// overrides it. Unset fields fall back to the built-in defaults.</param>
/// <param name="UnknownHost">What a request for a name that no enabled host serves gets.</param>
/// <param name="DefaultCertificateId">Certificate served when the SNI matches no host or is absent, and to hosts whose
/// binding resolves to no certificate of their own. Null: such handshakes are rejected.</param>
/// <param name="TrustedProxies">Who may tell us the client IP and scheme (PLAN §7.6).</param>
public sealed record GlobalSettings(
    long Version = 0,
    HostSettings? Defaults = null,
    UnknownHostBehaviour UnknownHost = UnknownHostBehaviour.NotFound,
    CertificateId? DefaultCertificateId = null,
    TrustedProxySettings? TrustedProxies = null)
{
    public HostSettings Defaults { get; init; } = Defaults ?? new();

    public TrustedProxySettings TrustedProxies { get; init; } = TrustedProxies ?? new();
}

/// <summary>
/// Trusted proxies in front of Paduraksa (PLAN §7.2 C, §7.6). Only a peer inside the trusted set may supply the
/// client IP through a header; anyone else is the client.
/// </summary>
/// <param name="Mode">How the client IP reaches us.</param>
/// <param name="Proxies">Addresses or CIDRs of the trusted peers; a set (canonical order: sorted, no duplicates).
/// <see cref="TrustedProxyMode.Generic"/>: the proxies (required). <see cref="TrustedProxyMode.CloudflareTunnel"/>:
/// where cloudflared connects from (required). <see cref="TrustedProxyMode.Off"/> and
/// <see cref="TrustedProxyMode.CloudflareProxy"/>: must be empty (the latter uses Cloudflare's published ranges).</param>
/// <param name="Header">Generic mode only: the header carrying the client; null = <c>X-Forwarded-For</c>, read right
/// to left.</param>
/// <param name="UseTrueClientIp">Cloudflare modes: also accept <c>True-Client-IP</c> when <c>CF-Connecting-IP</c> is
/// absent.</param>
/// <param name="ForwardedForFallback">Cloudflare modes: fall back to the right-most untrusted <c>X-Forwarded-For</c>
/// entry.</param>
/// <param name="TrustForwardedProto">Generic mode only: take the request scheme from the trusted proxies'
/// <c>X-Forwarded-Proto</c>.</param>
public sealed record TrustedProxySettings(
    TrustedProxyMode Mode = TrustedProxyMode.Off,
    IReadOnlyList<string>? Proxies = null,
    string? Header = null,
    bool UseTrueClientIp = false,
    bool ForwardedForFallback = false,
    bool TrustForwardedProto = false)
{
    public IReadOnlyList<string> Proxies { get; init; } = Proxies ?? [];
}

/// <summary>A named preset of host settings (PLAN §5.1), e.g. "LAN only"; a host names at most one.</summary>
/// <param name="Id">Identity.</param>
/// <param name="Name">Human label, unique ignoring case.</param>
/// <param name="Version">Concurrency version (PLAN §6.4).</param>
/// <param name="Settings">The profile layer: between the global defaults and the host.</param>
/// <param name="Notes">Free text.</param>
public sealed record ProfileConfig(
    ProfileId Id,
    string Name,
    long Version = 0,
    HostSettings? Settings = null,
    string? Notes = null)
{
    public HostSettings Settings { get; init; } = Settings ?? new();
}
