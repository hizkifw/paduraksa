using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

namespace Paduraksa.Contracts.Revisions;

/// <summary>Who did something, as the history records it.</summary>
/// <param name="Kind">The kind of actor.</param>
/// <param name="User">The user, for <see cref="ActorKind.User"/>.</param>
/// <param name="Token">The API token, for <see cref="ActorKind.Token"/>.</param>
public sealed record ActorRef(ActorKind Kind, UserId? User = null, ApiTokenId? Token = null);

/// <summary>A committed revision's metadata.</summary>
/// <param name="Id">The revision.</param>
/// <param name="Seq">1, 2, 3, ... in commit order; shown as "#42".</param>
/// <param name="Actor">Who made the change.</param>
/// <param name="Channel">Where it came in.</param>
/// <param name="CreatedAt">Commit time (UTC).</param>
/// <param name="DocumentSha256">SHA-256 of the canonical document, lowercase hex.</param>
/// <param name="ParentId">The revision it was made on top of; null for the first.</param>
/// <param name="Message">The optional note.</param>
public sealed record RevisionSummary(
    ConfigRevisionId Id,
    long Seq,
    ActorRef Actor,
    Channel Channel,
    DateTimeOffset CreatedAt,
    string DocumentSha256,
    ConfigRevisionId? ParentId = null,
    string? Message = null);

/// <summary>A revision with what it changed and its whole document.</summary>
/// <param name="Revision">The metadata.</param>
/// <param name="Changes">What it changed against its parent.</param>
/// <param name="Document">The config as of this revision, versions included.</param>
public sealed record RevisionDetail(RevisionSummary Revision, IReadOnlyList<ConfigChange> Changes, ConfigDocument Document);

/// <summary>What changed from one revision to another.</summary>
/// <param name="From">The older side.</param>
/// <param name="To">The newer side.</param>
/// <param name="Changes">The structural diff, as a dry run reports it.</param>
public sealed record RevisionDiff(ConfigRevisionId From, ConfigRevisionId To, IReadOnlyList<ConfigChange> Changes);

/// <summary>A page of revisions, newest first, before the API turns the continuation into a cursor.</summary>
/// <param name="Items">The page.</param>
/// <param name="Next">The last revision of the page when older ones remain; null otherwise.</param>
public sealed record RevisionPage(IReadOnlyList<RevisionSummary> Items, ConfigRevisionId? Next = null);

/// <summary>A revision on the history timeline: its metadata and what it changed against its parent.</summary>
/// <param name="Revision">The metadata.</param>
/// <param name="Changes">What it changed against its parent.</param>
public sealed record RevisionTimelineEntry(RevisionSummary Revision, IReadOnlyList<ConfigChange> Changes);

/// <summary>A page of the history timeline, newest first.</summary>
/// <param name="Items">The page.</param>
/// <param name="Next">The last revision of the page when older ones remain; null otherwise.</param>
public sealed record RevisionTimelinePage(IReadOnlyList<RevisionTimelineEntry> Items, ConfigRevisionId? Next = null);
