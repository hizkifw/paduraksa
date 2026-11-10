using Paduraksa.Application.Compilation;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

namespace Paduraksa.Dashboard.Config;

/// <summary>One route that names a policy in its own settings.</summary>
/// <param name="Host">The host the route belongs to.</param>
/// <param name="Route">The route.</param>
public sealed record RateLimitRouteUse(HostConfig Host, RouteConfig Route);

/// <summary>Where a policy is in force: the hosts that apply it, and the routes that name it themselves.</summary>
/// <param name="Hosts">Hosts whose resolved settings (host, profile or global defaults) name it.</param>
/// <param name="Routes">Routes that name it in their own settings.</param>
public sealed record RateLimitUsage(IReadOnlyList<HostConfig> Hosts, IReadOnlyList<RateLimitRouteUse> Routes)
{
    public bool IsEmpty => Hosts.Count == 0 && Routes.Count == 0;

    /// <summary>"2 hosts, 1 route", or a part of it when the other is empty.</summary>
    public string CountText
    {
        get
        {
            List<string> parts = [];
            if (Hosts.Count > 0)
            {
                parts.Add(Hosts.Count == 1 ? "1 host" : $"{Hosts.Count} hosts");
            }

            if (Routes.Count > 0)
            {
                parts.Add(Routes.Count == 1 ? "1 route" : $"{Routes.Count} routes");
            }

            return parts.Count == 0 ? "No host or route" : string.Join(", ", parts);
        }
    }
}

/// <summary>
/// Which hosts and routes a rate-limit policy reaches, resolved the way the validator resolves it: a host's own layer, its
/// profile and the global defaults, and a route's own layer. A policy only a profile or the global defaults name still
/// counts through every host that picks it up, because deleting it would refuse that document.
/// </summary>
public static class RateLimitUsageOf
{
    public static RateLimitUsage Find(ConfigDocument document, RateLimitPolicyId policy)
    {
        ArgumentNullException.ThrowIfNull(document);
        List<HostConfig> hosts = [];
        List<RateLimitRouteUse> routes = [];
        foreach (var host in document.Hosts)
        {
            var profile = host.ProfileId is { } profileId ? document.Profiles.FirstOrDefault(p => p.Id == profileId) : null;
            var resolved = HostSettingsLayers.MergeAll(HostSettingsLayers.BuiltIn, document.Settings.Defaults, profile?.Settings, host.Settings);
            if (resolved.RateLimit?.PolicyId == policy)
            {
                hosts.Add(host);
            }

            foreach (var route in host.Routes)
            {
                if (route.Settings?.RateLimit?.PolicyId == policy)
                {
                    routes.Add(new RateLimitRouteUse(host, route));
                }
            }
        }

        return new RateLimitUsage(
            [.. hosts.OrderBy(ChangeView.HostTitle, StringComparer.OrdinalIgnoreCase)],
            [.. routes.OrderBy(route => ChangeView.HostTitle(route.Host), StringComparer.OrdinalIgnoreCase).ThenBy(route => route.Route.Path, StringComparer.Ordinal)]);
    }
}
