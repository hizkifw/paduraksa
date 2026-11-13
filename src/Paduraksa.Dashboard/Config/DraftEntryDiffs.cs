using Paduraksa.Application.Config;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Drafts;

namespace Paduraksa.Dashboard.Config;

/// <summary>
/// The diff of each draft entry, as the review sheet shows a change: each entry is applied, in order, on the document the entries before it left, and
/// its object is shown before and after. An object has one entry, so its "before" is the object as the head has it; the entries before it only
/// make the names resolve (a host naming an upstream the draft adds). A blocked entry is not applied and has no diff.
/// </summary>
public static class DraftEntryDiffs
{
    public static IReadOnlyDictionary<int, IReadOnlyList<ChangeGroup>> Build(ConfigDocument head, IEnumerable<DraftEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(head);
        ArgumentNullException.ThrowIfNull(entries);
        Dictionary<int, IReadOnlyList<ChangeGroup>> diffs = [];
        var running = head;
        foreach (var entry in entries)
        {
            if (entry.State != DraftEntryState.Ready || ChangeView.Apply(running, entry.Command) is not { } after)
            {
                continue;
            }

            diffs[entry.Number] = ChangeView.Build(running, after, ConfigDiff.Compare(running, after));
            running = after;
        }

        return diffs;
    }
}
