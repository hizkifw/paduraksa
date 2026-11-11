using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

namespace Paduraksa.Application.Commands;

/// <summary>
/// The top-level objects a change list touches (added, removed or modified), with changes to a child (a route, a
/// destination) counted against its parent: a top-level object's version covers its children.
/// </summary>
internal sealed class TouchedObjects
{
    public bool Settings { get; private init; }

    public HashSet<ProfileId> Profiles { get; } = [];

    public HashSet<UpstreamId> Upstreams { get; } = [];

    public HashSet<RateLimitPolicyId> RateLimitPolicies { get; } = [];

    public HashSet<AccessListId> AccessLists { get; } = [];

    public HashSet<RuleSetId> RuleSets { get; } = [];

    public HashSet<AuthProviderId> AuthProviders { get; } = [];

    public HashSet<HostId> Hosts { get; } = [];

    public static TouchedObjects From(IReadOnlyList<ConfigChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        TouchedObjects touched = new() { Settings = changes.Any(c => c is SettingsChange) };
        foreach (var change in changes)
        {
            switch (change)
            {
                case ProfileChange p:
                    touched.Profiles.Add(p.ProfileId);
                    break;
                case UpstreamChange u:
                    touched.Upstreams.Add(u.UpstreamId);
                    break;
                case DestinationChange d:
                    touched.Upstreams.Add(d.UpstreamId);
                    break;
                case RateLimitPolicyChange p:
                    touched.RateLimitPolicies.Add(p.RateLimitPolicyId);
                    break;
                case AccessListChange l:
                    touched.AccessLists.Add(l.AccessListId);
                    break;
                case RuleSetChange r:
                    touched.RuleSets.Add(r.RuleSetId);
                    break;
                case AuthProviderChange p:
                    touched.AuthProviders.Add(p.AuthProviderId);
                    break;
                case HostChange h:
                    touched.Hosts.Add(h.HostId);
                    break;
                case RouteChange r:
                    touched.Hosts.Add(r.HostId);
                    break;
            }
        }

        return touched;
    }
}
