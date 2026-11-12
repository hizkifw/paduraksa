using Paduraksa.Application.Commands;
using Paduraksa.Application.Config;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Drafts;

namespace Paduraksa.Application.Drafts;

/// <summary>
/// A draft's entries replayed on a head: the merge. Each entry is planned, in order, on the document the entries before it left, exactly as the
/// same command would be planned live (<see cref="ConfigCommandPlanner.PlanObject"/>), so the version it was made against is checked against the
/// object as the head has it now. An entry whose object changed or went elsewhere is <see cref="DraftEntryState.ChangedElsewhere"/>, and so is every
/// entry that refers to what a blocked entry would have written (an upstream the draft creates under an ID someone else took, say), however far
/// the chain goes: a blocked entry is left out of the document, so what is shown never applies a change on top of something it doesn't contain.
/// </summary>
/// <remarks>
/// <para>A draft is applied whole or not at all, so the states are for marking and for refusing: an apply with any blocked entry commits nothing. A
/// blocked <i>delete</i> blocks nothing that refers to it: what still refers to an object that stays is the validator's to refuse.</para>
/// <para>Versions stay the head's: nothing is stamped, because the entries are not a commit. That is what lets a form read an object of the draft
/// and send it back with the version it shows, and an entry that created an object (version 0) look up the head without finding it.</para>
/// </remarks>
internal static class DraftReplay
{
    internal sealed record Replayed(ConfigDocument Document, IReadOnlyList<DraftEntry> Entries)
    {
        public IEnumerable<DraftEntry> Ready => Entries.Where(static e => e.State == DraftEntryState.Ready);

        public IEnumerable<DraftEntry> Blocked => Entries.Where(static e => e.State == DraftEntryState.ChangedElsewhere);
    }

    /// <summary>The document is canonical, so a page and the apply that follows it see the same order.</summary>
    public static Replayed Replay(ConfigDocument head, RevisionRef? headRevision, IEnumerable<DraftEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(head);
        ArgumentNullException.ThrowIfNull(entries);
        List<DraftEntry> all = [.. entries];
        Dictionary<string, string> reasons = new(StringComparer.Ordinal);
        while (true)
        {
            var document = head;
            var grew = false;
            foreach (var entry in all)
            {
                if (reasons.ContainsKey(entry.Target))
                {
                    continue;
                }

                var plan = ConfigCommandPlanner.PlanObject(entry.Command, document, headRevision);
                if (plan is { Early: null, Arranged: { } arranged })
                {
                    document = arranged;
                }
                else
                {
                    reasons[entry.Target] = "Its object was changed or removed elsewhere since you added it.";
                    grew = true;
                }
            }

            // What refers to something a blocked entry would have written is blocked too, and may in turn be referred to.
            foreach (var entry in all)
            {
                if (reasons.ContainsKey(entry.Target))
                {
                    continue;
                }

                var through = DraftReferences.Of(entry.Command)
                    .Select(target => all.FirstOrDefault(other => other.Target == target && other.Action != DraftEntryAction.Delete && reasons.ContainsKey(other.Target)))
                    .FirstOrDefault(static other => other is not null);
                if (through is not null)
                {
                    reasons[entry.Target] = $"It depends on {DraftText.Title(through, head)}, which is changed elsewhere.";
                    grew = true;
                }
            }

            if (!grew)
            {
                return new Replayed(
                    CanonicalConfigJson.Canonicalize(document),
                    [.. all.Select(e => reasons.TryGetValue(e.Target, out var reason)
                        ? e with { State = DraftEntryState.ChangedElsewhere, Reason = reason }
                        : e with { State = DraftEntryState.Ready, Reason = null })]);
            }
        }
    }
}
