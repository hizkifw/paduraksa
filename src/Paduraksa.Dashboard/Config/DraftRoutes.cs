using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Drafts;

namespace Paduraksa.Dashboard.Config;

/// <summary>Where a draft entry's object is in the dashboard.</summary>
public static class DraftRoutes
{
    /// <summary>Where the object's form is: its own address, or its list's.</summary>
    public static string Of(DraftEntry entry) => entry.Command switch
    {
        UpsertHostCommand c => DashboardRoutes.Host(c.Host.Id),
        DeleteHostCommand c => DashboardRoutes.Host(c.HostId),
        UpsertUpstreamCommand c => DashboardRoutes.Upstream(c.Upstream.Id),
        DeleteUpstreamCommand c => DashboardRoutes.Upstream(c.UpstreamId),
        UpsertProfileCommand c => DashboardRoutes.Profile(c.Profile.Id),
        DeleteProfileCommand c => DashboardRoutes.Profile(c.ProfileId),
        UpsertRateLimitPolicyCommand c => DashboardRoutes.RateLimit(c.Policy.Id),
        DeleteRateLimitPolicyCommand c => DashboardRoutes.RateLimit(c.RateLimitPolicyId),
        UpsertAccessListCommand c => DashboardRoutes.AccessList(c.AccessList.Id),
        DeleteAccessListCommand c => DashboardRoutes.AccessList(c.AccessListId),
        UpsertRuleSetCommand c => DashboardRoutes.RuleSet(c.RuleSet.Id),
        DeleteRuleSetCommand c => DashboardRoutes.RuleSet(c.RuleSetId),
        UpsertAuthProviderCommand c => DashboardRoutes.AuthProvider(c.AuthProvider.Id),
        DeleteAuthProviderCommand c => DashboardRoutes.AuthProvider(c.AuthProviderId),
        _ => SettingsRoutes.Defaults,
    };
}
