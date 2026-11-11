using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

namespace Paduraksa.Application.Commands;

/// <summary>
/// The pipeline's version rule: every top-level object a revision changes gets that revision's
/// <c>Seq</c> as its version, and every other object keeps its current one. Callers never set a version.
/// </summary>
/// <remarks>
/// <para>Using the <c>Seq</c> rather than "previous + 1" makes versions unique across the whole history, not only
/// increasing per object: a version number is issued once, ever. That is what keeps a rollback (or an import, or
/// deleting and re-creating an object) from handing out a version an old editor still holds. Restoring revision #37
/// as #52 gives every object it changes version 52, above anything it had before (versions are always at most the
/// latest <c>Seq</c>), so an editor holding the version from #37, or any version up to #51, gets a conflict.</para>
/// <para>Objects the revision doesn't change keep their version, so a rollback or an import never conflicts with an
/// edit to an object it leaves alone.</para>
/// </remarks>
internal static class ConfigVersions
{
    /// <summary>
    /// <paramref name="document"/> with every top-level object's version set: <paramref name="next"/> (the coming
    /// revision's <c>Seq</c>) for the objects <paramref name="changes"/> touches, the version in
    /// <paramref name="current"/> for the rest. Lists keep their order (and any null elements, for validation to
    /// report), so error paths still index the document as it was arranged.
    /// </summary>
    public static ConfigDocument Stamp(ConfigDocument document, ConfigDocument current, IReadOnlyList<ConfigChange> changes, long next)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(current);
        var touched = TouchedObjects.From(changes);

        var profiles = Versions(current.Profiles, p => p.Id, p => p.Version);
        var upstreams = Versions(current.Upstreams, u => u.Id, u => u.Version);
        var rateLimitPolicies = Versions(current.RateLimitPolicies, p => p.Id, p => p.Version);
        var accessLists = Versions(current.AccessLists, l => l.Id, l => l.Version);
        var ruleSets = Versions(current.RuleSets, r => r.Id, r => r.Version);
        var hosts = Versions(current.Hosts, h => h.Id, h => h.Version);

        // Not in the current document and untouched only happens with duplicate IDs (invalid): a new version then.
        long VersionOf<TId>(Dictionary<TId, long> versions, TId id, bool isTouched)
            where TId : notnull =>
            !isTouched && versions.TryGetValue(id, out var version) ? version : next;

        var settings = document.Settings ?? new();
        return document with
        {
            Settings = settings with { Version = touched.Settings ? next : current.Settings.Version },
            Profiles = [.. (document.Profiles ?? []).Select(p => p is null ? p! : p with { Version = VersionOf(profiles, p.Id, touched.Profiles.Contains(p.Id)) })],
            Upstreams = [.. (document.Upstreams ?? []).Select(u => u is null ? u! : u with { Version = VersionOf(upstreams, u.Id, touched.Upstreams.Contains(u.Id)) })],
            RateLimitPolicies = [.. (document.RateLimitPolicies ?? []).Select(p => p is null ? p! : p with { Version = VersionOf(rateLimitPolicies, p.Id, touched.RateLimitPolicies.Contains(p.Id)) })],
            AccessLists = [.. (document.AccessLists ?? []).Select(l => l is null ? l! : l with { Version = VersionOf(accessLists, l.Id, touched.AccessLists.Contains(l.Id)) })],
            RuleSets = [.. (document.RuleSets ?? []).Select(r => r is null ? r! : r with { Version = VersionOf(ruleSets, r.Id, touched.RuleSets.Contains(r.Id)) })],
            Hosts = [.. (document.Hosts ?? []).Select(h => h is null ? h! : h with { Version = VersionOf(hosts, h.Id, touched.Hosts.Contains(h.Id)) })],
        };
    }

    private static Dictionary<TId, long> Versions<T, TId>(IReadOnlyList<T> items, Func<T, TId> idOf, Func<T, long> versionOf)
        where TId : notnull
    {
        Dictionary<TId, long> versions = [];
        foreach (var item in items)
        {
            versions.TryAdd(idOf(item), versionOf(item));
        }

        return versions;
    }
}
