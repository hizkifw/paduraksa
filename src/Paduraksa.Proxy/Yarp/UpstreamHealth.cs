using Paduraksa.Core.Model;

using Yarp.ReverseProxy;
using Yarp.ReverseProxy.Model;

namespace Paduraksa.Proxy.Yarp;

/// <summary>A destination of the running proxy: its ID and the address it is configured with.</summary>
public sealed record LiveDestination(DestinationId Id, string Address);

/// <summary>Which destinations of an upstream the running proxy currently sends traffic to.</summary>
/// <param name="Healthy">Destinations in rotation.</param>
/// <param name="Unhealthy">Destinations the proxy knows but keeps traffic away from (failed active or passive checks).</param>
public sealed record UpstreamHealthSnapshot(IReadOnlyList<LiveDestination> Healthy, IReadOnlyList<LiveDestination> Unhealthy);

/// <summary>
/// Live destination health (PLAN §7.4, §7.7). YARP owns it and it exists only in the running proxy, so the request
/// simulator reads it from here, never from a snapshot: draft and revision snapshots aren't wired to YARP.
/// </summary>
public interface IUpstreamHealth
{
    /// <summary>The live proxy's view of <paramref name="upstream"/>, or null when it has no such cluster.</summary>
    UpstreamHealthSnapshot? Lookup(UpstreamId upstream);
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
}

/// <summary><see cref="IUpstreamHealth"/> over YARP's runtime state (<see cref="IProxyStateLookup"/>).</summary>
public sealed class YarpUpstreamHealth(IProxyStateLookup state) : IUpstreamHealth
{
    public UpstreamHealthSnapshot? Lookup(UpstreamId upstream)
    {
        if (!state.TryGetCluster(upstream.ToString(), out ClusterState? cluster))
        {
            return null;
        }

        ClusterDestinationsState destinations = cluster.DestinationsState;
        HashSet<string> available = new(StringComparer.OrdinalIgnoreCase);
        foreach (DestinationState destination in destinations.AvailableDestinations)
        {
            available.Add(destination.DestinationId);
        }

        List<LiveDestination> healthy = [];
        List<LiveDestination> unhealthy = [];
        foreach (DestinationState destination in destinations.AllDestinations)
        {
            if (!DestinationId.TryParse(destination.DestinationId, out DestinationId id))
            {
                continue;
            }

            (available.Contains(destination.DestinationId) ? healthy : unhealthy).Add(new LiveDestination(id, destination.Model.Config.Address));
        }

        return new UpstreamHealthSnapshot(healthy, unhealthy);
    }
}
