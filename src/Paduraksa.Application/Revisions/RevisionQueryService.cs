using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Paduraksa.Application.Config;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Revisions;
using Paduraksa.Core.Model;
using Paduraksa.Data;

namespace Paduraksa.Application.Revisions;

/// <summary>
/// Reads the config history for the surfaces (the REST API, later MCP and the dashboard): revision metadata pages, one revision
/// with its document, and the diff between two. Read-only and without authorization of its own: the caller checks
/// <c>config:read</c>, as it does for <see cref="Commands.ConfigService.GetCurrentAsync"/>. Everything it returns is a Contracts DTO.
/// </summary>
public sealed class RevisionQueryService
{
    /// <summary>The largest page.</summary>
    public const int MaxPageSize = ConfigRevisionStore.MaxPageSize - 1;

    private readonly IDbContextFactory<ConfigDbContext> _contexts;

    internal RevisionQueryService(IDbContextFactory<ConfigDbContext> contexts)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        _contexts = contexts;
    }

    /// <summary>
    /// Revisions, newest first. <paramref name="after"/> is the last revision of the previous page; null starts at the latest. Returns
    /// null when <paramref name="after"/> names no revision.
    /// </summary>
    /// <exception cref="RevisionIntegrityException">A stored revision's metadata is corrupt.</exception>
    public async Task<RevisionPage?> ListAsync(int limit, ConfigRevisionId? after, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxPageSize);
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            long? beforeSeq = null;
            if (after is { } cursor)
            {
                beforeSeq = await context.ConfigRevisions.AsNoTracking()
                    .Where(r => r.Id == cursor)
                    .Select(r => (long?)r.Seq)
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (beforeSeq is null)
                {
                    return null;
                }
            }

            // One more than asked: whether it exists says whether older revisions remain.
            IReadOnlyList<RevisionInfo> rows = await ConfigRevisionStore.ListAsync(context, limit + 1, beforeSeq, cancellationToken).ConfigureAwait(false);
            bool more = rows.Count > limit;
            RevisionSummary[] items = [.. rows.Take(limit).Select(ToSummary)];
            return new RevisionPage(items, more ? items[^1].Id : null);
        }
    }

    /// <summary>A revision with what it changed and its document; null if there is none with that ID.</summary>
    /// <exception cref="RevisionIntegrityException">The stored revision is corrupt.</exception>
    public async Task<RevisionDetail?> GetAsync(ConfigRevisionId id, CancellationToken cancellationToken = default)
    {
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            StoredRevision? stored = await ConfigRevisionStore.GetAsync(context, id, cancellationToken).ConfigureAwait(false);
            return stored is null ? null : new RevisionDetail(ToSummary(stored.Info), ReadChanges(stored.Info), ReadDocument(stored));
        }
    }

    /// <summary>What changed from <paramref name="from"/> to <paramref name="to"/>; null if either doesn't exist.</summary>
    /// <exception cref="RevisionIntegrityException">A stored revision is corrupt.</exception>
    public async Task<RevisionDiff?> DiffAsync(ConfigRevisionId from, ConfigRevisionId to, CancellationToken cancellationToken = default)
    {
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            StoredRevision? older = await ConfigRevisionStore.GetAsync(context, from, cancellationToken).ConfigureAwait(false);
            StoredRevision? newer = await ConfigRevisionStore.GetAsync(context, to, cancellationToken).ConfigureAwait(false);
            return older is null || newer is null
                ? null
                : new RevisionDiff(from, to, ConfigDiff.Compare(ReadDocument(older), ReadDocument(newer)));
        }
    }

    private static RevisionSummary ToSummary(RevisionInfo info) => new(
        info.Id,
        info.Seq,
        new ActorRef(info.Actor.Kind, info.Actor.UserId, info.Actor.TokenId),
        info.Channel,
        info.CreatedAt,
        Convert.ToHexStringLower(info.DocumentHash.Span),
        info.ParentId,
        info.Message);

    private static ConfigDocument ReadDocument(StoredRevision stored)
    {
        try
        {
            return CanonicalConfigJson.Deserialize(stored.Document.Span);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new RevisionIntegrityException(stored.Info.Id, stored.Info.Seq, "its document can't be read", ex);
        }
    }

    private static IReadOnlyList<ConfigChange> ReadChanges(RevisionInfo info)
    {
        try
        {
            return JsonSerializer.Deserialize(info.Summary, ConfigJsonContext.Default.IReadOnlyListConfigChange) ?? [];
        }
        catch (JsonException ex)
        {
            throw new RevisionIntegrityException(info.Id, info.Seq, "its change summary can't be read", ex);
        }
    }
}
