namespace Paduraksa.Application.Drafts;

/// <summary>The stable <c>code</c>s of the errors a draft reports itself (the ones of a refused entry are the config command's).</summary>
public static class DraftErrorCodes
{
    /// <summary>The command is not a change to one object (a whole-config command, or no command at all).</summary>
    public const string EntryCommand = "draft.entry.command";

    /// <summary>An entry that is changed elsewhere (or depends on one that is), named by an apply that refused the draft for it.</summary>
    public const string EntryBlocked = "draft.entry.blocked";
}
