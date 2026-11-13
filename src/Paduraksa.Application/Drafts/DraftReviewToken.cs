using System.Security.Cryptography;
using System.Text;

using Paduraksa.Application.Config;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Drafts;
using Paduraksa.Core.Model;

namespace Paduraksa.Application.Drafts;

/// <summary>
/// What a review shows, as a token the apply must send back: apply commits exactly what the person confirmed, or nothing (docs/design/dashboard.md "Drafts (M10)"). It is
/// <c>{draft version}.{hash}</c>, and the hash covers what the draft would do, stated without anything that moves for reasons of its own:
/// <list type="bullet">
/// <item>the draft's ID and version, so any change to the draft itself (an entry added, replaced or removed) refuses an apply of an older review;</item>
/// <item>for every entry, in order: its number, target and the object it leaves (its canonical JSON in the candidate document, or "gone"), so the diff is covered
/// field by field without the document positions that an unrelated host would shift;</item>
/// <item>every object those entries refer to and the draft doesn't write (the upstream of a drafted host, a profile, a list), as it is now, since the diff
/// doesn't show it and the person still confirmed it;</item>
/// <item>the compiler's warnings <i>that the draft changes</i>: those of the candidate against those of the live config, by code and message (their paths
/// are positions), so a change that makes the same draft warn differently does not match, while a warning about a host nobody is editing, which the live
/// config has too, is not part of it. (A set that fails the validator or the lock-out guard has no review to confirm: it is refused with its errors.)</item>
/// </list>
/// A commit that touches none of that (another host edited, a revision added) leaves the token as it was, so a busy instance can still apply. A change to an
/// object the draft writes makes its entry blocked, which refuses the apply on its own.
/// </summary>
internal static class DraftReviewToken
{
    public static string Compute(DraftId draft, long version, DraftReplay.Replayed replayed, IReadOnlyList<ConfigWarning> warnings, IReadOnlyList<ConfigWarning> liveWarnings)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Add(hash, $"draft-review/1|{draft}|{version}");
        var written = replayed.Ready.Select(static e => e.Target).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in replayed.Ready)
        {
            Add(hash, $"entry|{entry.Number}|{entry.Target}|{entry.Action}");
            Add(hash, ObjectJson(replayed.Document, entry.Target));
            foreach (var target in DraftReferences.Of(entry.Command).Where(t => !written.Contains(t)))
            {
                Add(hash, $"refers|{target}");
                Add(hash, ObjectJson(replayed.Document, target));
            }
        }

        foreach (var warning in WhatTheDraftChanges(warnings, liveWarnings))
        {
            Add(hash, warning);
        }

        return $"{version}.{Convert.ToBase64String(hash.GetHashAndReset(), 0, 16).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
    }

    /// <summary>
    /// The warnings the draft adds to the live ones or takes away (by code and message, counted: their paths are positions in a document). A host nobody
    /// is editing that warns about its certificate warns the same in the live config and in the candidate, so it is not part of what this draft does.
    /// </summary>
    private static IEnumerable<string> WhatTheDraftChanges(IReadOnlyList<ConfigWarning> candidate, IReadOnlyList<ConfigWarning> live)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var warning in candidate)
        {
            var key = $"{warning.Code}|{warning.Message}";
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        foreach (var warning in live)
        {
            var key = $"{warning.Code}|{warning.Message}";
            counts[key] = counts.GetValueOrDefault(key) - 1;
        }

        return counts.Where(static c => c.Value != 0).Select(static c => $"warning|{c.Value}|{c.Key}").Order(StringComparer.Ordinal);
    }

    private static void Add(IncrementalHash hash, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }

    private static string ObjectJson(ConfigDocument document, string target)
    {
        var (list, id) = target.Split('/', 2) is [var kind, var key] ? (kind, key) : (target, string.Empty);
        ConfigDocument? only = list switch
        {
            "hosts" => document.Hosts.FirstOrDefault(h => h.Id.ToString() == id) is { } h ? new ConfigDocument(Hosts: [h]) : null,
            "upstreams" => document.Upstreams.FirstOrDefault(u => u.Id.ToString() == id) is { } u ? new ConfigDocument(Upstreams: [u]) : null,
            "profiles" => document.Profiles.FirstOrDefault(p => p.Id.ToString() == id) is { } p ? new ConfigDocument(Profiles: [p]) : null,
            "rateLimitPolicies" => document.RateLimitPolicies.FirstOrDefault(p => p.Id.ToString() == id) is { } p ? new ConfigDocument(RateLimitPolicies: [p]) : null,
            "accessLists" => document.AccessLists.FirstOrDefault(l => l.Id.ToString() == id) is { } l ? new ConfigDocument(AccessLists: [l]) : null,
            "ruleSets" => document.RuleSets.FirstOrDefault(r => r.Id.ToString() == id) is { } r ? new ConfigDocument(RuleSets: [r]) : null,
            "authProviders" => document.AuthProviders.FirstOrDefault(p => p.Id.ToString() == id) is { } p ? new ConfigDocument(AuthProviders: [p]) : null,
            _ when target == DraftTargets.Settings => new ConfigDocument(Settings: document.Settings),
            _ => null,
        };
        return only is null ? "gone" : Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(only));
    }
}
