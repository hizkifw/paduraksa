using Paduraksa.Core.Model;

namespace Paduraksa.Application.Revisions;

/// <summary>A revision to commit (see <see cref="ConfigRevisionStore.AppendAsync"/>).</summary>
/// <param name="Document">The config document as canonical UTF-8 JSON bytes (deterministic: equal configs, equal bytes).</param>
/// <param name="Summary">The change list against the latest revision, as JSON (stored as given).</param>
/// <param name="Actor">Who made the change.</param>
/// <param name="Channel">Where it came in.</param>
/// <param name="Message">An optional note ("Rollback to #37", a commit message from the API).</param>
internal sealed record NewRevision(ReadOnlyMemory<byte> Document, string Summary, Actor Actor, Channel Channel, string? Message = null)
{
    /// <summary>The revision's ID, from <see cref="ConfigRevisionStore.NextId"/>; null: a new one.</summary>
    public ConfigRevisionId? Id { get; init; }
}

/// <summary>A committed revision's metadata: everything but the document itself.</summary>
/// <param name="Id">The revision ID.</param>
/// <param name="Seq">1, 2, 3, … in commit order.</param>
/// <param name="ParentId">The revision this one was made on top of; null only for the first.</param>
/// <param name="Actor">Who made the change.</param>
/// <param name="Channel">Where it came in.</param>
/// <param name="Message">The optional note.</param>
/// <param name="CreatedAt">Commit time (UTC).</param>
/// <param name="DocumentHash">SHA-256 of the canonical document bytes (32 bytes).</param>
/// <param name="Summary">The change list against the parent, as JSON.</param>
/// <remarks>Record equality compares <paramref name="DocumentHash"/> by reference; compare <see cref="Id"/> instead.</remarks>
internal sealed record RevisionInfo(
    ConfigRevisionId Id,
    long Seq,
    ConfigRevisionId? ParentId,
    Actor Actor,
    Channel Channel,
    string? Message,
    DateTimeOffset CreatedAt,
    ReadOnlyMemory<byte> DocumentHash,
    string Summary)
{
    /// <summary>The display form of <see cref="Seq"/>: <c>#42</c>.</summary>
    public string Label => $"#{Seq}";
}

/// <summary>A committed revision with its document, decompressed and checked against its hash.</summary>
/// <param name="Info">The metadata.</param>
/// <param name="Document">The canonical UTF-8 JSON bytes, exactly as committed.</param>
internal sealed record StoredRevision(RevisionInfo Info, ReadOnlyMemory<byte> Document);
