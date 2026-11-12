using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Drafts;

namespace Paduraksa.Application.Drafts;

/// <summary>Which other objects an entry's object names (a host its upstreams, profile and lists), as <see cref="DraftTargets"/> values.</summary>
internal static class DraftReferences
{
    /// <summary>What the object an upsert writes refers to; nothing for a delete, an upstream, a rate limit, an access list, a rule set or a provider.</summary>
    public static IReadOnlyList<string> Of(ConfigCommand command)
    {
        List<string> targets = [];
        switch (command)
        {
            case UpsertHostCommand { Host: { } host }:
                if (host.ProfileId is { } profile)
                {
                    targets.Add(DraftTargets.Profile(profile));
                }

                Settings(host.Settings, targets);
                foreach (var route in host.Routes ?? [])
                {
                    targets.Add(DraftTargets.Upstream(route.UpstreamId));
                    Settings(route.Settings, targets);
                }

                break;
            case UpsertProfileCommand { Profile: { } drafted }:
                Settings(drafted.Settings, targets);
                break;
            case UpdateSettingsCommand { Settings: { } settings }:
                Settings(settings.Defaults, targets);
                break;
        }

        return [.. targets.Distinct(StringComparer.Ordinal)];
    }

    private static void Settings(HostSettings? settings, List<string> targets)
    {
        if (settings is null)
        {
            return;
        }

        Add(settings.RateLimit?.PolicyId, DraftTargets.RateLimit, targets);
        Add(settings.AccessList?.AccessListId, DraftTargets.AccessList, targets);
        Add(settings.RuleSet?.RuleSetId, DraftTargets.RuleSet, targets);
        Add(settings.AuthProvider?.AuthProviderId, DraftTargets.AuthProvider, targets);
    }

    private static void Settings(RouteSettings? settings, List<string> targets)
    {
        if (settings is null)
        {
            return;
        }

        Add(settings.RateLimit?.PolicyId, DraftTargets.RateLimit, targets);
        Add(settings.AccessList?.AccessListId, DraftTargets.AccessList, targets);
        Add(settings.RuleSet?.RuleSetId, DraftTargets.RuleSet, targets);
        Add(settings.AuthProvider?.AuthProviderId, DraftTargets.AuthProvider, targets);
    }

    private static void Add<TId>(TId? id, Func<TId, string> target, List<string> targets)
        where TId : struct
    {
        if (id is { } value)
        {
            targets.Add(target(value));
        }
    }
}
