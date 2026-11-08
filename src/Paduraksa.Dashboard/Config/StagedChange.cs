using System.Text.Json;

using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

namespace Paduraksa.Dashboard.Config;

/// <summary>
/// A host together with the objects made for it in its drawer (PLAN §12): an upstream or a profile created from the host form is not
/// applied on its own but staged into the host's draft, so the host and what it needs are reviewed and applied as one revision, and
/// cancelling the host leaves nothing behind. Without staged objects the host goes as its own object command, as before.
/// </summary>
public static class StagedChange
{
    /// <summary>The current document with the staged objects added: what the form's pickers offer and its inline validation runs against.</summary>
    public static ConfigDocument Draft(ConfigDocument current, IReadOnlyList<UpstreamConfig> upstreams, IReadOnlyList<ProfileConfig> profiles)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(upstreams);
        ArgumentNullException.ThrowIfNull(profiles);
        return upstreams.Count == 0 && profiles.Count == 0
            ? current
            : current with { Upstreams = [.. current.Upstreams, .. upstreams], Profiles = [.. current.Profiles, .. profiles] };
    }

    /// <summary>The staged upstreams <paramref name="host"/> still routes to (one made and then replaced by another is dropped).</summary>
    public static IReadOnlyList<UpstreamConfig> UsedUpstreams(HostConfig host, IReadOnlyList<UpstreamConfig> staged)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(staged);
        return host.Kind == HostKind.Proxy ? [.. staged.Where(u => host.Routes.Any(r => r.UpstreamId == u.Id))] : [];
    }

    /// <summary>The staged profile <paramref name="host"/> still picks, if any.</summary>
    public static IReadOnlyList<ProfileConfig> UsedProfiles(HostConfig host, IReadOnlyList<ProfileConfig> staged)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(staged);
        return [.. staged.Where(p => p.Id == host.ProfileId)];
    }

    /// <summary>
    /// <paramref name="head"/> with <paramref name="host"/> put in (in its place, or last when new) and the staged objects added: the
    /// document of the one revision a <c>ReplaceConfigCommand</c> based on <paramref name="head"/> makes.
    /// </summary>
    public static ConfigDocument Apply(ConfigDocument head, HostConfig host, IReadOnlyList<UpstreamConfig> upstreams, IReadOnlyList<ProfileConfig> profiles)
    {
        ArgumentNullException.ThrowIfNull(head);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(upstreams);
        ArgumentNullException.ThrowIfNull(profiles);
        bool exists = head.Hosts.Any(h => h.Id == host.Id);
        return head with
        {
            Upstreams = [.. head.Upstreams.Where(u => upstreams.All(s => s.Id != u.Id)), .. upstreams],
            Profiles = [.. head.Profiles.Where(p => profiles.All(s => s.Id != p.Id)), .. profiles],
            Hosts = exists ? [.. head.Hosts.Select(h => h.Id == host.Id ? host : h)] : [.. head.Hosts, host],
        };
    }

    /// <summary>
    /// A form's state as one string (the canonical JSON of the objects it would send), to tell whether the form holds changes: equal
    /// fingerprints, nothing to lose.
    /// </summary>
    public static string Fingerprint(ConfigDocument draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return JsonSerializer.Serialize(draft, ConfigJsonContext.Default.ConfigDocument);
    }
}
