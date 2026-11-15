using System.Text.Json.Serialization;

using Paduraksa.Core.Json;
using Paduraksa.Core.Model;

namespace Paduraksa.Contracts.Config;

/// <summary>An upstream: a set of destinations that routes proxy to, compiled into a YARP cluster.</summary>
/// <param name="Id">Identity; also the YARP cluster ID.</param>
/// <param name="Name">Human label, unique ignoring case.</param>
/// <param name="Version">Concurrency version.</param>
/// <param name="Destinations">At least one. Canonical order: by ID. Changing a destination's ID resets its YARP health
/// state.</param>
/// <param name="LoadBalancing">Null uses YARP's default (today power of two choices). A value pins the policy, so null and
/// <see cref="LoadBalancingPolicy.PowerOfTwoChoices"/> are different settings.</param>
/// <param name="SessionAffinity">Cookie-based session affinity.</param>
/// <param name="PassiveHealth">Passive health (transport failure rate); null = off.</param>
/// <param name="ActiveHealth">Active health probes; null = off.</param>
/// <param name="MaxConnectionsPerServer">Null: no limit beyond the HTTP client's default.</param>
/// <param name="Notes">Free text.</param>
/// <param name="Tls">How HTTPS destinations are authenticated; null = the system trust store.</param>
/// <param name="HostHeader">The <c>Host</c> the upstream receives; null = the client's own (<see cref="UpstreamHostHeaderMode.Original"/>).</param>
/// <param name="Timeouts">Connect and idle timeouts; null = the built-in defaults.</param>
public sealed record UpstreamConfig(
    UpstreamId Id,
    string Name,
    long Version = 0,
    IReadOnlyList<DestinationConfig>? Destinations = null,
    LoadBalancingPolicy? LoadBalancing = null,
    bool SessionAffinity = false,
    PassiveHealthConfig? PassiveHealth = null,
    ActiveHealthConfig? ActiveHealth = null,
    int? MaxConnectionsPerServer = null,
    string? Notes = null,
    UpstreamTlsConfig? Tls = null,
    UpstreamHostHeaderConfig? HostHeader = null,
    UpstreamTimeoutsConfig? Timeouts = null)
{
    public IReadOnlyList<DestinationConfig> Destinations { get; init; } = Destinations ?? [];
}

/// <summary>How long the proxy waits on an upstream. A null value uses the built-in default.</summary>
/// <param name="Connect">TCP and TLS connect to a destination, 100 ms to 5 minutes; null = 10 seconds.</param>
/// <param name="Idle">How long a request, response stream or upgraded connection (a WebSocket) can carry no data in either direction before the proxy closes it, 1 second to 24 hours; zero = never; null = 1 hour.</param>
public sealed record UpstreamTimeoutsConfig(TimeSpan? Connect = null, TimeSpan? Idle = null)
{
    public static readonly TimeSpan DefaultConnect = TimeSpan.FromSeconds(10);

    public static readonly TimeSpan DefaultIdle = TimeSpan.FromHours(1);

    public static readonly TimeSpan MinConnect = TimeSpan.FromMilliseconds(100);

    public static readonly TimeSpan MaxConnect = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan MinIdle = TimeSpan.FromSeconds(1);

    public static readonly TimeSpan MaxIdle = TimeSpan.FromHours(24);
}

/// <summary>A destination of an upstream.</summary>
/// <param name="Id">Identity; also the YARP destination ID.</param>
/// <param name="Address">Absolute <c>http</c>/<c>https</c> URL, e.g. <c>http://jellyfin:8096</c>, optionally with a
/// base path; no query, fragment or credentials.</param>
/// <param name="Enabled">False: kept in the document but not proxied to.</param>
public sealed record DestinationConfig(DestinationId Id, string Address, bool Enabled = true);

/// <summary>YARP's load-balancing policies.</summary>
[JsonConverter(typeof(ConfigEnumConverter<LoadBalancingPolicy>))]
public enum LoadBalancingPolicy
{
    PowerOfTwoChoices,
    RoundRobin,
    Random,
    LeastRequests,
    FirstAlphabetical,
}

/// <summary>Passive health: the proxy takes a destination with too many transport failures out of rotation for a while.</summary>
public sealed record PassiveHealthConfig
{
    /// <summary>How long an unhealthy destination stays out; null = YARP's default.</summary>
    public TimeSpan? ReactivationPeriod { get; init; }
}

/// <summary>Active health: every <paramref name="Interval"/>, the proxy sends a GET to <paramref name="Path"/> on each destination.</summary>
/// <param name="Path">Starts with <c>/</c>, no query or fragment; appended to each destination's address.</param>
/// <param name="Interval">Positive; null = <see cref="DefaultInterval"/> (canonical form: set).</param>
/// <param name="Timeout">Positive; null = <see cref="DefaultTimeout"/> (canonical form: set).</param>
public sealed record ActiveHealthConfig(string Path = "/", TimeSpan? Interval = null, TimeSpan? Timeout = null)
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(15);

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
}

/// <summary>How the proxy authenticates an upstream's HTTPS destinations.</summary>
/// <param name="Trust">Which certificates are accepted.</param>
/// <param name="PinnedSha256">With <see cref="UpstreamTlsTrust.Pinned"/>: the SHA-256 fingerprints of the certificates to
/// accept, 64 hex digits each (colons allowed, as <c>openssl x509 -noout -fingerprint -sha256</c> prints them).</param>
public sealed record UpstreamTlsConfig(UpstreamTlsTrust Trust = UpstreamTlsTrust.System, IReadOnlyList<string>? PinnedSha256 = null);

/// <summary>The <c>Host</c> header an upstream receives. On an HTTPS destination SNI follows it, so the certificate is requested for
/// and checked against the name sent (a pinned fingerprint isn't tied to a name).</summary>
/// <param name="Mode">Which name is sent.</param>
/// <param name="Value">With <see cref="UpstreamHostHeaderMode.Custom"/>: a host name or IP address, with a port when the
/// upstream's virtual host needs one (<c>app.internal</c>, <c>app.internal:8080</c>, <c>[::1]:8080</c>). Absent otherwise.</param>
public sealed record UpstreamHostHeaderConfig(UpstreamHostHeaderMode Mode = UpstreamHostHeaderMode.Original, string? Value = null);
