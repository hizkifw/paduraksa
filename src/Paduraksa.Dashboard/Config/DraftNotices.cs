namespace Paduraksa.Dashboard.Config;

/// <summary>The words about a draft that other changes must say in one voice: a whole-document change while a draft is open, and applying one object on its own.</summary>
public static class DraftNotices
{
    /// <summary>A rollback, an undo or an import while a draft is open: the draft was made on the head that this moves.</summary>
    public static string WholeDocument(int entries) =>
        $"You have a draft with {(entries == 1 ? "1 change" : $"{entries} changes")}, made on the config as it is now. "
        + "Applying this moves the config, so every entry for an object it changes is then changed elsewhere and blocks the draft until it is reloaded.";

    /// <summary>The line under an Apply now that the draft also holds this object's entry.</summary>
    public const string ApplyingDropsEntry = "Applying now drops this object's entry from the draft.";
}
