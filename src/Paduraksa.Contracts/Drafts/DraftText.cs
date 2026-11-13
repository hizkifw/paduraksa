using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;

namespace Paduraksa.Contracts.Drafts;

/// <summary>A draft entry in words, for the revision's default note, the audit details and the dashboard's review list.</summary>
public static class DraftText
{
    /// <summary>"add host app.example.com", "change upstream app", "delete profile web": a deleted object's name comes from <paramref name="head"/>.</summary>
    public static string Title(DraftEntry entry, ConfigDocument head)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(head);
        var verb = entry.Action switch
        {
            DraftEntryAction.Create => "add",
            DraftEntryAction.Delete => "delete",
            _ => "change",
        };
        var (kind, name) = Of(entry.Command, head);
        return name.Length == 0 ? $"{verb} {kind}" : $"{verb} {kind} {name}";
    }

    /// <summary>The kind of object an entry is about ("host", "rate limit", "global settings") and its name; the name is empty for the settings.</summary>
    public static (string Kind, string Name) Of(ConfigCommand command, ConfigDocument head)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(head);
        return command switch
        {
            UpsertHostCommand c => ("host", HostName(c.Host, c.Host.Id.ToString())),
            DeleteHostCommand c => ("host", HostName(head.Hosts.FirstOrDefault(h => h.Id == c.HostId), c.HostId.ToString())),
            UpsertUpstreamCommand c => ("upstream", c.Upstream.Name),
            DeleteUpstreamCommand c => ("upstream", head.Upstreams.FirstOrDefault(u => u.Id == c.UpstreamId)?.Name ?? c.UpstreamId.ToString()),
            UpsertRateLimitPolicyCommand c => ("rate limit", c.Policy.Name),
            DeleteRateLimitPolicyCommand c => ("rate limit", head.RateLimitPolicies.FirstOrDefault(p => p.Id == c.RateLimitPolicyId)?.Name ?? c.RateLimitPolicyId.ToString()),
            UpsertAccessListCommand c => ("access list", c.AccessList.Name),
            DeleteAccessListCommand c => ("access list", head.AccessLists.FirstOrDefault(l => l.Id == c.AccessListId)?.Name ?? c.AccessListId.ToString()),
            UpsertRuleSetCommand c => ("rule set", c.RuleSet.Name),
            DeleteRuleSetCommand c => ("rule set", head.RuleSets.FirstOrDefault(r => r.Id == c.RuleSetId)?.Name ?? c.RuleSetId.ToString()),
            UpsertAuthProviderCommand c => ("sign-in provider", c.AuthProvider.Name),
            DeleteAuthProviderCommand c => ("sign-in provider", head.AuthProviders.FirstOrDefault(p => p.Id == c.AuthProviderId)?.Name ?? c.AuthProviderId.ToString()),
            UpsertProfileCommand c => ("profile", c.Profile.Name),
            DeleteProfileCommand c => ("profile", head.Profiles.FirstOrDefault(p => p.Id == c.ProfileId)?.Name ?? c.ProfileId.ToString()),
            _ => ("global settings", string.Empty),
        };
    }

    private static string HostName(HostConfig? host, string fallback) =>
        host is { Names: { Count: > 0 } names } ? names[0] : fallback;
}
