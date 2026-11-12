using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Drafts;
using Paduraksa.Core.Model;

namespace Paduraksa.Application.Drafts;

/// <summary>What a command is about, in a draft: which object (one entry per object) and what it does to it.</summary>
internal static class DraftEntries
{
    public const string SettingsObject = "settings";

    /// <summary>The target and action of an object command; null for a command a draft doesn't take (a whole-document one).</summary>
    public static (string Target, DraftEntryAction Action)? Of(ConfigCommand command) => command switch
    {
        UpsertHostCommand { Host: { } o } => ($"hosts/{o.Id}", Verb(o.Version)),
        DeleteHostCommand c => ($"hosts/{c.HostId}", DraftEntryAction.Delete),
        UpsertUpstreamCommand { Upstream: { } o } => ($"upstreams/{o.Id}", Verb(o.Version)),
        DeleteUpstreamCommand c => ($"upstreams/{c.UpstreamId}", DraftEntryAction.Delete),
        UpsertRateLimitPolicyCommand { Policy: { } o } => ($"rateLimitPolicies/{o.Id}", Verb(o.Version)),
        DeleteRateLimitPolicyCommand c => ($"rateLimitPolicies/{c.RateLimitPolicyId}", DraftEntryAction.Delete),
        UpsertAccessListCommand { AccessList: { } o } => ($"accessLists/{o.Id}", Verb(o.Version)),
        DeleteAccessListCommand c => ($"accessLists/{c.AccessListId}", DraftEntryAction.Delete),
        UpsertRuleSetCommand { RuleSet: { } o } => ($"ruleSets/{o.Id}", Verb(o.Version)),
        DeleteRuleSetCommand c => ($"ruleSets/{c.RuleSetId}", DraftEntryAction.Delete),
        UpsertAuthProviderCommand { AuthProvider: { } o } => ($"authProviders/{o.Id}", Verb(o.Version)),
        DeleteAuthProviderCommand c => ($"authProviders/{c.AuthProviderId}", DraftEntryAction.Delete),
        UpsertProfileCommand { Profile: { } o } => ($"profiles/{o.Id}", Verb(o.Version)),
        DeleteProfileCommand c => ($"profiles/{c.ProfileId}", DraftEntryAction.Delete),
        UpdateSettingsCommand { Settings: not null } => (SettingsObject, DraftEntryAction.Update),
        _ => null,
    };

    /// <summary>The command as a draft keeps it: what the caller's request added (a dry run, a forced lock-out, a note) is not part of an entry.</summary>
    public static ConfigCommand Normalize(ConfigCommand command) => command with { DryRun = false, Force = false, Message = null };

    /// <summary>"add host app.example.com", "change upstream app", "delete profile web": for the revision's default note and the audit details.</summary>
    public static string Title(DraftEntry entry, ConfigDocument head)
    {
        var verb = entry.Action switch
        {
            DraftEntryAction.Create => "add",
            DraftEntryAction.Delete => "delete",
            _ => "change",
        };
        var (kind, name) = entry.Command switch
        {
            UpsertHostCommand c => ("host", HostName(c.Host)),
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
        return name.Length == 0 ? $"{verb} {kind}" : $"{verb} {kind} {name}";
    }

    private static string HostName(HostConfig? host, string fallback = "") =>
        host is { Names: { Count: > 0 } names } ? names[0] : fallback;

    private static string HostName(HostConfig host) => HostName(host, host.Id.ToString());

    private static DraftEntryAction Verb(long version) => version == 0 ? DraftEntryAction.Create : DraftEntryAction.Update;
}
