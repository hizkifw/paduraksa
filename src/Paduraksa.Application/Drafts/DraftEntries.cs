using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Drafts;
using Paduraksa.Core.Model;

namespace Paduraksa.Application.Drafts;

/// <summary>What a command is about, in a draft: which object (one entry per object) and what it does to it.</summary>
internal static class DraftEntries
{
    /// <summary>The target and action of an object command; null for a command a draft doesn't take (a whole-document one).</summary>
    public static (string Target, DraftEntryAction Action)? Of(ConfigCommand command) => DraftTargets.Of(command);

    /// <summary>The command as a draft keeps it: what the caller's request added (a dry run, a forced lock-out, a note) is not part of an entry.</summary>
    public static ConfigCommand Normalize(ConfigCommand command) => command with { DryRun = false, Force = false, Message = null };
}
