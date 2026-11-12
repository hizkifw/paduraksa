using Paduraksa.Core.Model;

using Yarp.ReverseProxy;
using Yarp.ReverseProxy.Health;
using Yarp.ReverseProxy.Model;

namespace Paduraksa.Proxy.Yarp;

/// <summary>A destination of the running proxy: its ID and the address it is configured with.</summary>
public sealed record LiveDestination(DestinationId Id, string Address);

/// <summary>Which destinations of an upstream the running proxy currently sends traffic to.</summary>
public sealed record UpstreamHealthSnapshot(IReadOnlyList<LiveDestination> Healthy, IReadOnlyList<LiveDestination> Unhealthy);

/// <summary>
/// Live destination health. YARP owns it and it exists only in the running proxy, so the request
/// simulator reads it from here, never from a snapshot: draft and revision snapshots aren't wired to YARP.
/// </summary>
public interface IUpstreamHealth
{
    /// <summary>The live proxy's view of <paramref name="upstream"/>, or null when it has no such cluster.</summary>
    UpstreamHealthSnapshot? Lookup(UpstreamId upstream);

    /// <summary>Every upstream of the live proxy with each destination's active, passive and combined verdict; empty while there is no proxy.</summary>
    IReadOnlyList<UpstreamReading> ReadAll();
}

/// <summary>
/// An <see cref="IUpstreamHealth"/> that can be handed out before YARP exists: the process-wide services (the request
/// simulator) are built before the public app, whose YARP holds the state. Answers null (no such cluster) until
/// <see cref="Attach"/>, and again after <see cref="Detach"/>.
/// </summary>
public sealed class DeferredUpstreamHealth : IUpstreamHealth
{
    private volatile IUpstreamHealth? _inner;

    /// <summary>Starts answering from <paramref name="health"/>.</summary>
    public void Attach(IUpstreamHealth health)
    {
        ArgumentNullException.ThrowIfNull(health);
        _inner = health;
    }

    /// <summary>Stops answering (the proxy it read has stopped).</summary>
    public void Detach() => _inner = null;

    public UpstreamHealthSnapshot? Lookup(UpstreamId upstream) => _inner?.Lookup(upstream);

    public IReadOnlyList<UpstreamReading> ReadAll() => _inner?.ReadAll() ?? [];
}

/// <summary><see cref="IUpstreamHealth"/> over YARP's runtime state (<see cref="IProxyStateLookup"/>).</summary>
public sealed class YarpUpstreamHealth(IProxyStateLookup state, ProbeLog? probes = null) : IUpstreamHealth
{
    public IReadOnlyList<UpstreamReading> ReadAll()
    {
        List<UpstreamReading> readings = [];
        foreach (var cluster in state.GetClusters())
        {
            if (!UpstreamId.TryParse(cluster.ClusterId, out var id))
            {
                continue;
            }

            var checks = cluster.Model.Config.HealthCheck;
            List<DestinationReading> destinations = [];
            foreach (var destination in cluster.DestinationsState.AllDestinations)
            {
                if (!DestinationId.TryParse(destination.DestinationId, out var destinationId))
                {
                    continue;
                }

                var active = Verdict(destination.Health.Active);
                var passive = Verdict(destination.Health.Passive);
                destinations.Add(new DestinationReading(
                    destinationId, destination.Model.Config.Address, active, passive, Combine(active, passive), probes?.Last(destination)));
            }

            readings.Add(new UpstreamReading(id, checks?.Active?.Enabled == true, checks?.Passive?.Enabled == true, destinations));
        }

        return readings;
    }

    /// <summary>YARP's combined verdict (its own property is internal): either side unhealthy, or else a healthy side, or no verdict.</summary>
    private static DestinationVerdict Combine(DestinationVerdict active, DestinationVerdict passive) =>
        active == DestinationVerdict.Unhealthy || passive == DestinationVerdict.Unhealthy ? DestinationVerdict.Unhealthy
        : active == DestinationVerdict.Healthy || passive == DestinationVerdict.Healthy ? DestinationVerdict.Healthy
        : DestinationVerdict.Unknown;

    private static DestinationVerdict Verdict(DestinationHealth health) => health switch
    {
        DestinationHealth.Healthy => DestinationVerdict.Healthy,
        DestinationHealth.Unhealthy => DestinationVerdict.Unhealthy,
        _ => DestinationVerdict.Unknown,
    };

    public UpstreamHealthSnapshot? Lookup(UpstreamId upstream)
    {
        if (!state.TryGetCluster(upstream.ToString(), out var cluster))
        {
            return null;
        }

        var destinations = cluster.DestinationsState;
        HashSet<string> available = new(StringComparer.OrdinalIgnoreCase);
        foreach (var destination in destinations.AvailableDestinations)
        {
            available.Add(destination.DestinationId);
        }

        List<LiveDestination> healthy = [];
        List<LiveDestination> unhealthy = [];
        foreach (var destination in destinations.AllDestinations)
        {
            if (!DestinationId.TryParse(destination.DestinationId, out var id))
            {
                continue;
            }

            (available.Contains(destination.DestinationId) ? healthy : unhealthy).Add(new LiveDestination(id, destination.Model.Config.Address));
        }

        return new UpstreamHealthSnapshot(healthy, unhealthy);
    }
}
