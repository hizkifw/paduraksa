using System.Text.Json.Serialization;

using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

namespace Paduraksa.Contracts.Commands;

/// <summary>How a <see cref="ConfigCommand"/> ended.</summary>
[JsonConverter(typeof(ConfigEnumConverter<ConfigCommandStatus>))]
public enum ConfigCommandStatus
{
    /// <summary>A new revision was committed and is live (or still queued for publishing:
    /// <see cref="PublishOutcomeStatus.Pending"/>).</summary>
    Committed,

    /// <summary>A dry run passed every check; nothing was persisted. <see cref="ConfigCommandResult.Changes"/> is what
    /// it would change.</summary>
    DryRun,

    /// <summary>The command changes nothing (the config already is that way); nothing was committed.</summary>
    Unchanged,

    /// <summary>Validation or compilation failed (<see cref="ConfigCommandResult.Errors"/>); nothing was persisted.</summary>
    Invalid,

    /// <summary>The object's version or the base revision is stale (<see cref="ConfigCommandResult.Conflict"/>);
    /// nothing was persisted.</summary>
    Conflict,

    /// <summary>The object (or, for a rollback, the target revision) doesn't exist; nothing was persisted.</summary>
    NotFound,

    /// <summary>Not authorised; nothing was persisted.</summary>
    Denied,

    /// <summary>The revision was committed, but publishing it failed (<see cref="ConfigCommandResult.Publish"/>): the
    /// previous snapshot stays live. The commit stands, so the change goes live with the next successful publish or
    /// the next start.</summary>
    PublishFailed,

    /// <summary>The config store is unavailable: the process started degraded, serving the last-known-good config
    /// because the database couldn't be opened or its latest revision is corrupt (<see cref="ConfigCommandResult.Errors"/>
    /// says why). Nothing was read or persisted; fix the database and restart.</summary>
    Unavailable,
}

/// <summary>
/// The outcome of a <see cref="ConfigCommand"/>.
/// </summary>
/// <param name="Status">How it ended.</param>
/// <param name="Revision">The revision committed (<see cref="ConfigCommandStatus.Committed"/>,
/// <see cref="ConfigCommandStatus.PublishFailed"/>); otherwise null.</param>
/// <param name="Changes">What the command changes against the current config: committed, or would commit (dry run,
/// invalid, conflict). Paths index the canonical document after the change (<see cref="ConfigChange"/>). Empty when
/// the command never got that far (not found, denied).</param>
/// <param name="Errors">
/// Errors about what the caller sent. Object commands: JSON pointers relative to the object
/// (<c>/names/0</c>; <c>""</c> is the object itself). Whole-document commands: pointers into the document as sent
/// (a rollback: as stored in the target revision). Validation errors index what was sent; compile errors, a backstop
/// that only fires on what validation can't see, index its canonical form.
/// </param>
/// <param name="RelatedErrors">Object commands only: errors the change causes elsewhere in the config, e.g. a deleted
/// upstream that a route still uses, or a name another host already serves. Pointers into the canonical document
/// after the change, like <paramref name="Changes"/>.</param>
/// <param name="Publish">How publishing went: set exactly when a revision was committed.</param>
/// <param name="Conflict">Set exactly for <see cref="ConfigCommandStatus.Conflict"/>.</param>
/// <param name="Version">Object commands that upsert: the object's version after the change (committed or, for a dry
/// run, the one it would get).</param>
public sealed record ConfigCommandResult(
    ConfigCommandStatus Status,
    RevisionRef? Revision = null,
    IReadOnlyList<ConfigChange>? Changes = null,
    IReadOnlyList<ConfigError>? Errors = null,
    IReadOnlyList<ConfigError>? RelatedErrors = null,
    PublishOutcome? Publish = null,
    ConfigConflict? Conflict = null,
    long? Version = null)
{
    public IReadOnlyList<ConfigChange> Changes { get; init; } = Changes ?? [];

    public IReadOnlyList<ConfigError> Errors { get; init; } = Errors ?? [];

    public IReadOnlyList<ConfigError> RelatedErrors { get; init; } = RelatedErrors ?? [];
}

/// <summary>A committed revision, by ID and display number.</summary>
/// <param name="Id">The revision.</param>
/// <param name="Seq">1, 2, 3, … in commit order; shown as "#42".</param>
public sealed record RevisionRef(ConfigRevisionId Id, long Seq)
{
    /// <summary>The display form: <c>#42</c>.</summary>
    public override string ToString() => $"#{Seq}";
}

/// <summary>
/// What a conflict dialog needs: what the command was based on against what is current.
/// </summary>
/// <param name="CurrentRevision">The latest revision; null while there is none.</param>
/// <param name="CurrentVersion">Object commands: the object's current version. (An update or delete of an object that
/// no longer exists is <see cref="ConfigCommandStatus.NotFound"/>, not a conflict; a create of one that already exists
/// is a conflict with its version here.) Whole-document commands: null.</param>
/// <param name="ChangedSinceBase">Whole-document commands: what changed from the base revision to the latest (empty
/// when the base revision is unknown). Object commands: empty; <see cref="ConfigCommandResult.Changes"/> holds what the
/// command would change on top of the current config.</param>
public sealed record ConfigConflict(RevisionRef? CurrentRevision, long? CurrentVersion, IReadOnlyList<ConfigChange> ChangedSinceBase);

/// <summary>
/// How the snapshot publish after a commit went (PLAN §6.1 step 6). The publisher only publishes the latest snapshot:
/// when a newer revision is queued before this one went live, this one is superseded, and its outcome is that of the
/// publish that carries it (<paramref name="Revision"/> then names the newer revision).
/// </summary>
/// <param name="Status">The verdict.</param>
/// <param name="Generation">The live snapshot generation, when <see cref="PublishOutcomeStatus.Published"/>.</param>
/// <param name="Errors">Why it failed, if it did.</param>
/// <param name="Revision">The revision whose publish this is: the committed one, or the newer one that superseded it.</param>
public sealed record PublishOutcome(PublishOutcomeStatus Status, long Generation, IReadOnlyList<string> Errors, ConfigRevisionId Revision)
{
    public bool Succeeded => Status == PublishOutcomeStatus.Published;
}

/// <summary>The publisher's verdict on a committed revision's snapshot.</summary>
[JsonConverter(typeof(ConfigEnumConverter<PublishOutcomeStatus>))]
public enum PublishOutcomeStatus
{
    /// <summary>Live.</summary>
    Published,

    /// <summary>Still queued or in progress when the pipeline stopped waiting; it goes live when the publisher gets to
    /// it (before the proxy has started, at its start). A later failure is logged and audited.</summary>
    Pending,

    /// <summary>YARP's validator rejected the snapshot.</summary>
    Invalid,

    /// <summary>YARP failed to apply it; its previous config was restored.</summary>
    Rejected,

    /// <summary>YARP didn't answer in time.</summary>
    TimedOut,

    /// <summary>Anything else (the publisher stopped, endpoints never matched).</summary>
    Failed,
}

/// <summary>The current config, as an editor starts from it.</summary>
/// <param name="Revision">The latest revision; null while there is none (the empty config).</param>
/// <param name="Document">Its canonical document, versions included.</param>
public sealed record CurrentConfig(RevisionRef? Revision, ConfigDocument Document);

/// <summary>A change notification (PLAN §6.1 step 7): a revision was committed.</summary>
/// <param name="Revision">The new revision.</param>
/// <param name="Changes">What it changed.</param>
public sealed record ConfigCommitted(RevisionRef Revision, IReadOnlyList<ConfigChange> Changes);
