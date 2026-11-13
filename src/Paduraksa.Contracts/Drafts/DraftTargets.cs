using Paduraksa.Contracts.Commands;
using Paduraksa.Core.Model;

namespace Paduraksa.Contracts.Drafts;

/// <summary>The <see cref="DraftEntry.Target"/> of each kind of object: <c>hosts/host_...</c>, or <c>settings</c>. One entry per target.</summary>
public static class DraftTargets
{
    /// <summary>The global settings.</summary>
    public const string Settings = "settings";

    public static string Host(HostId id) => $"hosts/{id}";

    public static string Upstream(UpstreamId id) => $"upstreams/{id}";

    public static string Profile(ProfileId id) => $"profiles/{id}";

    public static string RateLimit(RateLimitPolicyId id) => $"rateLimitPolicies/{id}";

    public static string AccessList(AccessListId id) => $"accessLists/{id}";

    public static string RuleSet(RuleSetId id) => $"ruleSets/{id}";

    public static string AuthProvider(AuthProviderId id) => $"authProviders/{id}";

    /// <summary>The target and action of an object command; null for a command a draft doesn't take (a whole-document one).</summary>
    public static (string Target, DraftEntryAction Action)? Of(ConfigCommand command) => command switch
    {
        UpsertHostCommand { Host: { } o } => (Host(o.Id), Verb(o.Version)),
        DeleteHostCommand c => (Host(c.HostId), DraftEntryAction.Delete),
        UpsertUpstreamCommand { Upstream: { } o } => (Upstream(o.Id), Verb(o.Version)),
        DeleteUpstreamCommand c => (Upstream(c.UpstreamId), DraftEntryAction.Delete),
        UpsertRateLimitPolicyCommand { Policy: { } o } => (RateLimit(o.Id), Verb(o.Version)),
        DeleteRateLimitPolicyCommand c => (RateLimit(c.RateLimitPolicyId), DraftEntryAction.Delete),
        UpsertAccessListCommand { AccessList: { } o } => (AccessList(o.Id), Verb(o.Version)),
        DeleteAccessListCommand c => (AccessList(c.AccessListId), DraftEntryAction.Delete),
        UpsertRuleSetCommand { RuleSet: { } o } => (RuleSet(o.Id), Verb(o.Version)),
        DeleteRuleSetCommand c => (RuleSet(c.RuleSetId), DraftEntryAction.Delete),
        UpsertAuthProviderCommand { AuthProvider: { } o } => (AuthProvider(o.Id), Verb(o.Version)),
        DeleteAuthProviderCommand c => (AuthProvider(c.AuthProviderId), DraftEntryAction.Delete),
        UpsertProfileCommand { Profile: { } o } => (Profile(o.Id), Verb(o.Version)),
        DeleteProfileCommand c => (Profile(c.ProfileId), DraftEntryAction.Delete),
        UpdateSettingsCommand { Settings: not null } => (Settings, DraftEntryAction.Update),
        _ => null,
    };

    private static DraftEntryAction Verb(long version) => version == 0 ? DraftEntryAction.Create : DraftEntryAction.Update;
}
