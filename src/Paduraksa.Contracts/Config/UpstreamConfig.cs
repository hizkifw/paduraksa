using System.Text.Json.Serialization;

using Paduraksa.Core.Json;
using Paduraksa.Core.Model;

namespace Paduraksa.Contracts.Config;

/// <summary>An upstream (PLAN §5.1): a set of destinations that routes proxy to, compiled into a YARP cluster.</summary>
/// <param name="Id">Identity; also the YARP cluster ID.</param>
/// <param name="Name">Human label, unique ignoring case.</param>
/// <param name="Version">Concurrency version (PLAN §6.4).</param>
/// <param name="Destinations">At least one. Canonical order: by ID. Their IDs must stay stable for YARP health state to
/// survive (PLAN §6.3).</param>
/// <param name="LoadBalancing">Null: whatever YARP's default is (today power of two choices); a value pins the
/// policy, so null and <see cref="LoadBalancingPolicy.PowerOfTwoChoices"/> are kept apart.</param>
/// <param name="SessionAffinity">Cookie-based session affinity.</param>
/// <param name="PassiveHealth">Passive health (transport failure rate); null = off.</param>
/// <param name="ActiveHealth">Active health probes; null = off.</param>
/// <param name="MaxConnectionsPerServer">Null: no limit beyond the HTTP client's default.</param>
/// <param name="Notes">Free text.</param>
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
    string? Notes = null)
{
    public IReadOnlyList<DestinationConfig> Destinations { get; init; } = Destinations ?? [];
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

/// <summary>Passive health: a destination with too many transport failures is taken out for a while.</summary>
public sealed record PassiveHealthConfig
{
    /// <summary>How long an unhealthy destination stays out; null = YARP's default.</summary>
    public TimeSpan? ReactivationPeriod { get; init; }
}

/// <summary>Active health: a GET of <paramref name="Path"/> on each destination every <paramref name="Interval"/>.</summary>
/// <param name="Path">Starts with <c>/</c>, no query or fragment; appended to each destination's address.</param>
/// <param name="Interval">Positive; null = <see cref="DefaultInterval"/> (canonical form: set).</param>
/// <param name="Timeout">Positive; null = <see cref="DefaultTimeout"/> (canonical form: set).</param>
public sealed record ActiveHealthConfig(string Path = "/", TimeSpan? Interval = null, TimeSpan? Timeout = null)
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(15);

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
}
