using Paduraksa.Application.Compilation;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Components;

namespace Paduraksa.Dashboard.Config;

/// <summary>One route that names a referenced object (access list, rate-limit policy, auth provider, rule set) in its own settings.</summary>
public sealed record RouteUse(HostConfig Host, RouteConfig Route);

/// <summary>Where a referenced object is in force: the hosts that apply it, and the routes that name it themselves.</summary>
/// <param name="Hosts">Hosts whose resolved settings (host, profile or global defaults) name it.</param>
/// <param name="Routes">Routes that name it in their own settings.</param>
public sealed record ReferenceUsage(IReadOnlyList<HostConfig> Hosts, IReadOnlyList<RouteUse> Routes)
{
    public static ReferenceUsage None { get; } = new([], []);

    public bool IsEmpty => Hosts.Count == 0 && Routes.Count == 0;

    /// <summary>"2 hosts, 1 route", or a part of it when the other is empty. The list pages' Used by column.</summary>
    public string CountText => IsEmpty ? "No host or route" : string.Join(", ", Counts);

    /// <summary>"Used by 2 hosts and 1 route", or nothing when it is unused.</summary>
    public string? UsedByText => IsEmpty ? null : $"Used by {Wording.List(Counts)}";

    /// <summary>Why the object can't be deleted: "Used by 2 hosts and 1 route. Pick another access list (or none) for them first.", or nothing when it is unused.</summary>
    /// <param name="replacement">What to pick instead, as the sentence says it: "access list (or none)".</param>
    public string? Refusal(string replacement) => UsedByText is { } usedBy ? $"{usedBy}. Pick another {replacement} for them first." : null;

    private List<string> Counts
    {
        get
        {
            List<string> parts = [];
            if (Hosts.Count > 0)
            {
                parts.Add(Wording.Plural(Hosts.Count, "host", "hosts"));
            }

            if (Routes.Count > 0)
            {
                parts.Add(Wording.Plural(Routes.Count, "route", "routes"));
            }

            return parts;
        }
    }

    /// <summary>
    /// Which hosts and routes name an object, resolved the way the validator resolves it: a host's own layer, its profile and the
    /// global defaults, and a route's own layer. An object only a profile or the global defaults name still counts through every host
    /// that picks it up, because deleting it would refuse that document. <c>names</c> is told whether a settings layer references the
    /// object; a route's layer is passed as a host-shaped one.
    /// </summary>
    public static ReferenceUsage Find(ConfigDocument document, Func<HostSettings, bool> names)
    {
        ArgumentNullException.ThrowIfNull(document);
        List<HostConfig> hosts = [];
        List<RouteUse> routes = [];
        foreach (var host in document.Hosts)
        {
            var profile = host.ProfileId is { } profileId ? document.Profiles.FirstOrDefault(p => p.Id == profileId) : null;
            var resolved = HostSettingsLayers.MergeAll(HostSettingsLayers.BuiltIn, document.Settings.Defaults, profile?.Settings, host.Settings);
            if (names(resolved))
            {
                hosts.Add(host);
            }

            foreach (var route in host.Routes)
            {
                if (route.Settings is { } own && names(new HostSettings { RateLimit = own.RateLimit, AccessList = own.AccessList, RuleSet = own.RuleSet, AuthProvider = own.AuthProvider }))
                {
                    routes.Add(new RouteUse(host, route));
                }
            }
        }

        return new ReferenceUsage(
            [.. hosts.OrderBy(ChangeView.HostTitle, StringComparer.OrdinalIgnoreCase)],
            [.. routes.OrderBy(route => ChangeView.HostTitle(route.Host), StringComparer.OrdinalIgnoreCase).ThenBy(route => route.Route.Path, StringComparer.Ordinal)]);
    }
}
