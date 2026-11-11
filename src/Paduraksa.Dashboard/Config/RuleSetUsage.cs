using Paduraksa.Application.Compilation;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

namespace Paduraksa.Dashboard.Config;

/// <summary>One route that names a rule set in its own settings.</summary>
/// <param name="Host">The host the route belongs to.</param>
/// <param name="Route">The route.</param>
public sealed record RuleSetRouteUse(HostConfig Host, RouteConfig Route);

/// <summary>Where a rule set is in force: the hosts that apply it, and the routes that name it themselves.</summary>
/// <param name="Hosts">Hosts whose resolved settings (host, profile or global defaults) name it.</param>
/// <param name="Routes">Routes that name it in their own settings.</param>
public sealed record RuleSetUsage(IReadOnlyList<HostConfig> Hosts, IReadOnlyList<RuleSetRouteUse> Routes)
{
    public bool IsEmpty => Hosts.Count == 0 && Routes.Count == 0;

    /// <summary>The verb that goes with <see cref="CountText"/>: one host or one route "uses" it, any other count "use" it.</summary>
    public string UseVerb => Hosts.Count + Routes.Count == 1 ? "uses" : "use";

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
/// Which hosts and routes a rule set reaches, resolved the way the validator resolves it: a host's own layer, its profile and the
/// global defaults, and a route's own layer. A set only a profile or the global defaults name still counts through every host that
/// picks it up, because deleting it would refuse that document.
/// </summary>
public static class RuleSetUsageOf
{
    public static RuleSetUsage Find(ConfigDocument document, RuleSetId set)
    {
        ArgumentNullException.ThrowIfNull(document);
        List<HostConfig> hosts = [];
        List<RuleSetRouteUse> routes = [];
        foreach (var host in document.Hosts)
        {
            var profile = host.ProfileId is { } profileId ? document.Profiles.FirstOrDefault(p => p.Id == profileId) : null;
            var resolved = HostSettingsLayers.MergeAll(HostSettingsLayers.BuiltIn, document.Settings.Defaults, profile?.Settings, host.Settings);
            if (resolved.RuleSet?.RuleSetId == set)
            {
                hosts.Add(host);
            }

            foreach (var route in host.Routes)
            {
                if (route.Settings?.RuleSet?.RuleSetId == set)
                {
                    routes.Add(new RuleSetRouteUse(host, route));
                }
            }
        }

        return new RuleSetUsage(
            [.. hosts.OrderBy(ChangeView.HostTitle, StringComparer.OrdinalIgnoreCase)],
            [.. routes.OrderBy(route => ChangeView.HostTitle(route.Host), StringComparer.OrdinalIgnoreCase).ThenBy(route => route.Route.Path, StringComparer.Ordinal)]);
    }
}
