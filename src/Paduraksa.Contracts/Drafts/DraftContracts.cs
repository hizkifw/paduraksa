using System.Text.Json.Serialization;

using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Json;
using Paduraksa.Core.Model;

namespace Paduraksa.Contracts.Drafts;

/// <summary>What a draft entry does to its object.</summary>
[JsonConverter(typeof(ConfigEnumConverter<DraftEntryAction>))]
public enum DraftEntryAction
{
    /// <summary>The object is new (version 0 in the command).</summary>
    Create,

    /// <summary>The object is replaced.</summary>
    Update,

    /// <summary>The object is deleted.</summary>
    Delete,
}

/// <summary>Whether an entry can be applied on the config as it is now.</summary>
[JsonConverter(typeof(ConfigEnumConverter<DraftEntryState>))]
public enum DraftEntryState
{
    /// <summary>Its object is as it was when the entry was added (whether the whole set validates is the review's answer).</summary>
    Ready,

    /// <summary>Its object was changed, or removed, elsewhere since the entry was added. It is left out of an apply and stays in the draft.</summary>
    ChangedElsewhere,
}

/// <summary>One change in a draft: a config object command, kept with the version of the object it was made against.</summary>
/// <param name="Number">The entry's number in its draft: 1, 2, 3, ... in the order added, never reused within the draft.</param>
/// <param name="Action">What it does.</param>
/// <param name="Target">The object it is about, <c>hosts/host_...</c> or <c>settings</c>; one entry per object, the later edit replacing the earlier.</param>
/// <param name="Command">The command, as it will be replayed. It names the version it was made against (an upsert's object version, a delete's
/// <c>Version</c>). A password typed into an access list is not in it: the user carries the <c>credentialId</c> of a pending vault value.</param>
/// <param name="AddedAt">When the entry was added or last replaced.</param>
/// <param name="State">Against the config as it is now: worked out on every read, never stored.</param>
/// <param name="Reason">Why the entry is <see cref="DraftEntryState.ChangedElsewhere"/>, in words: its own object moved, or it depends on an entry that is blocked.</param>
public sealed record DraftEntry(
    int Number,
    DraftEntryAction Action,
    string Target,
    ConfigCommand Command,
    DateTimeOffset AddedAt,
    DraftEntryState State = DraftEntryState.Ready,
    string? Reason = null);

/// <summary>
/// What a draft's row keeps of an <see cref="DraftEntry"/>: everything but what is worked out against the head on every read (its state and reason).
/// </summary>
/// <param name="Number">See <see cref="DraftEntry.Number"/>.</param>
/// <param name="Action">See <see cref="DraftEntry.Action"/>.</param>
/// <param name="Target">See <see cref="DraftEntry.Target"/>.</param>
/// <param name="Command">See <see cref="DraftEntry.Command"/>.</param>
/// <param name="AddedAt">See <see cref="DraftEntry.AddedAt"/>.</param>
/// <param name="LegacyState">Read only, never written: rows made before the state stopped being stored carry it as <c>state</c>, and still read.</param>
public sealed record StoredDraftEntry(
    int Number,
    DraftEntryAction Action,
    string Target,
    ConfigCommand Command,
    DateTimeOffset AddedAt,
    [property: JsonPropertyName("state")] string? LegacyState = null);

/// <summary>A user's open draft.</summary>
/// <param name="Id">The draft.</param>
/// <param name="Version">Changes with every change to the draft; apply names the one it reviewed (<c>If-Match</c>).</param>
/// <param name="CreatedAt">When it was started.</param>
/// <param name="UpdatedAt">When it was last changed; 30 days later (<paramref name="ExpiresAt"/>) an untouched draft is dropped.</param>
/// <param name="ExpiresAt">When it is dropped if nobody touches it.</param>
/// <param name="Entries">The entries in the order they were added, each with its state against the head.</param>
/// <param name="StartedOn">The revision the draft was started on; null when there was none.</param>
/// <param name="Head">The latest revision now; null while there is none.</param>
public sealed record ConfigDraft(
    DraftId Id,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<DraftEntry> Entries,
    RevisionRef? StartedOn = null,
    RevisionRef? Head = null);

/// <summary>The head with a draft's entries applied: what every page and picker of the dashboard reads while a draft is open.</summary>
/// <param name="Document">The head's document with each ready entry applied in order; versions are the head's, not the ones a commit would give.</param>
/// <param name="Draft">The draft.</param>
/// <param name="Head">The latest revision; null while there is none.</param>
public sealed record DraftConfig(ConfigDocument Document, ConfigDraft Draft, RevisionRef? Head = null);

/// <summary>The body of "add entries": object commands, added in order. Each replaces an earlier entry for the same object.</summary>
/// <param name="Commands">Upsert and delete commands (never a replace or rollback). Their <c>dryRun</c>, <c>force</c> and <c>message</c> are ignored.</param>
public sealed record AddDraftEntriesRequest(IReadOnlyList<ConfigCommand> Commands);

/// <summary>The outcome of a review or an apply.</summary>
/// <param name="Result">The dry run over the ready entries (<see cref="ConfigCommandStatus.DryRun"/>, invalid, ...), or the commit.</param>
/// <param name="Draft">The draft after the call; null when it is gone (it was applied, whole).</param>
/// <param name="ReviewToken">A review that passed and could be applied: what apply must send back as <c>If-Match</c> to commit exactly this. Absent when the
/// draft has blocked entries (it applies whole or not at all) or the set doesn't pass.</param>
public sealed record DraftOutcome(ConfigCommandResult Result, ConfigDraft? Draft = null, string? ReviewToken = null);

/// <summary>A draft that was dropped for being untouched, told to its owner once.</summary>
/// <param name="Id">The dropped draft.</param>
/// <param name="DroppedAt">When.</param>
/// <param name="Entries">How many changes it held.</param>
public sealed record DraftNotice(DraftId Id, DateTimeOffset DroppedAt, int Entries);
