using Paduraksa.Core.Model;
using Paduraksa.Proxy.Snapshot;

namespace Paduraksa.Proxy.Metrics;

/// <summary>
/// The slots the live snapshot holds and the names and IDs they are labelled with. The registry knows owners by GUID only, and a
/// retired slot's object is gone from the config, so exposition walks the snapshot: what is live is what is reported.
/// </summary>
internal sealed class MetricInventory
{
    /// <summary>The label of the reserved host slot that counts requests no host owns.</summary>
    public const string UnmatchedHostName = "(unmatched)";

    public required HostRow[] Hosts { get; init; }

    public required RouteRow[] Routes { get; init; }

    public required UpstreamRow[] Upstreams { get; init; }

    public required DestinationRow[] Destinations { get; init; }

    /// <param name="snapshot">The live snapshot.</param>
    /// <param name="registry">Whose slots the snapshot's objects hold: an object whose slot went to another object while its publish was
    /// being made (a conflict the publisher logs) is left out, so its label never carries the new owner's counters.</param>
    public static MetricInventory Of(RuntimeSnapshot snapshot, MetricsRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(registry);
        List<HostRow> hosts = [new(MetricsRegistry.UnmatchedHost, UnmatchedHostName, string.Empty)];
        List<RouteRow> routes = [];
        Dictionary<int, UpstreamRow> upstreams = [];
        List<DestinationRow> destinations = [];
        foreach (var host in snapshot.HostsByIndex)
        {
            if (!registry.IsCurrent(MetricSubject.Host, host.MetricSlot))
            {
                continue;
            }

            var hostId = host.Id.ToString();
            var name = host.Names.Count > 0 ? host.Names[0] : hostId;
            hosts.Add(new(host.MetricSlot, name, hostId));
            foreach (var route in host.Routes)
            {
                if (registry.IsCurrent(MetricSubject.Route, route.MetricSlot))
                {
                    routes.Add(new(route.MetricSlot, name, hostId, route.Id.ToString(), route.Path));
                }

                if (route.Upstream is { } upstream && registry.IsCurrent(MetricSubject.Upstream, upstream.Slot) && upstreams.TryAdd(upstream.Slot.Index, new(upstream.Slot, upstream.Id.ToString())))
                {
                    var upstreamId = upstream.Id.ToString();
                    for (var i = 0; i < upstream.DestinationKeys.Count; i++)
                    {
                        if (registry.IsCurrent(MetricSubject.Destination, upstream.DestinationSlots[i]))
                        {
                            destinations.Add(new(upstream.DestinationSlots[i], upstreamId, upstream.DestinationKeys[i]));
                        }
                    }
                }
            }
        }

        return new()
        {
            Hosts = hosts.ToArray(),
            Routes = routes.ToArray(),
            Upstreams = Copy(upstreams),
            Destinations = destinations.ToArray(),
        };
    }

    private static UpstreamRow[] Copy(Dictionary<int, UpstreamRow> upstreams)
    {
        var copy = new UpstreamRow[upstreams.Count];
        upstreams.Values.CopyTo(copy, 0);
        return copy;
    }

    internal readonly record struct HostRow(MetricSlot Slot, string Name, string Id);

    internal readonly record struct RouteRow(MetricSlot Slot, string Host, string HostId, string Id, string Path);

    internal readonly record struct UpstreamRow(MetricSlot Slot, string Id);

    internal readonly record struct DestinationRow(MetricSlot Slot, string UpstreamId, string Id);
}
