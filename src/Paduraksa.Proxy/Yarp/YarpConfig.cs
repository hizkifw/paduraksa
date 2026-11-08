using Yarp.ReverseProxy.Configuration;

namespace Paduraksa.Proxy.Yarp;

/// <summary>
/// The YARP routes and clusters of one snapshot: a pure function of content (PLAN §6.2, spike S2). IDs are stable
/// keys, never generations or revisions, so a policy-only edit produces an equal config and YARP has nothing to do.
/// <para>This is the content only. The <see cref="IProxyConfig"/> YARP reads (with its change token and
/// <c>RevisionId</c>) is created per publish by <c>SnapshotProxyConfigProvider</c>.</para>
/// </summary>
public sealed class YarpConfig
{
    /// <summary>
    /// The cluster every admin host routes to (PLAN §3.1). Its destination address is symbolic
    /// (<see cref="AdminUpstreamAddress"/>); the forwarder's connect callback reaches the admin app's internal listener instead
    /// of resolving it.
    /// </summary>
    public const string AdminClusterId = "__admin";

    /// <summary>The admin cluster's symbolic destination; never resolved.</summary>
    public const string AdminUpstreamAddress = "http://admin.internal/";

    public YarpConfig(IReadOnlyList<RouteConfig> routes, IReadOnlyList<ClusterConfig> clusters)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(clusters);
        Routes = routes;
        Clusters = clusters;
    }

    public static YarpConfig Empty { get; } = new([], []);

    public IReadOnlyList<RouteConfig> Routes { get; }

    public IReadOnlyList<ClusterConfig> Clusters { get; }

    public bool IsEmpty => Routes.Count == 0 && Clusters.Count == 0;

    /// <summary>
    /// Same routes and clusters by value (YARP's own equality), regardless of order. When true, publishing this
    /// config would be a YARP no-op.
    /// </summary>
    public bool ContentEquals(YarpConfig other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (Routes.Count != other.Routes.Count || Clusters.Count != other.Clusters.Count)
        {
            return false;
        }

        Dictionary<string, RouteConfig> routes = new(Routes.Count, StringComparer.OrdinalIgnoreCase);
        foreach (RouteConfig route in Routes)
        {
            routes[route.RouteId] = route;
        }

        foreach (RouteConfig route in other.Routes)
        {
            if (!routes.TryGetValue(route.RouteId, out RouteConfig? mine) || !mine.Equals(route))
            {
                return false;
            }
        }

        Dictionary<string, ClusterConfig> clusters = new(Clusters.Count, StringComparer.OrdinalIgnoreCase);
        foreach (ClusterConfig cluster in Clusters)
        {
            clusters[cluster.ClusterId] = cluster;
        }

        foreach (ClusterConfig cluster in other.Clusters)
        {
            if (!clusters.TryGetValue(cluster.ClusterId, out ClusterConfig? mine) || !mine.Equals(cluster))
            {
                return false;
            }
        }

        return true;
    }
}
