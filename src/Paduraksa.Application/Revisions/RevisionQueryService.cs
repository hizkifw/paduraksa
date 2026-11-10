using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Paduraksa.Application.Config;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Revisions;
using Paduraksa.Core.Model;
using Paduraksa.Data;

namespace Paduraksa.Application.Revisions;

/// <summary>Reads the config history for the surfaces (the REST API, the dashboard, later MCP): revision metadata
/// pages (with what each revision changed, for the dashboard's timeline), one revision with its document, and the
/// diff between two. Read-only and without authorization of its own: the caller checks <c>config:read</c>, as it does
/// for <see cref="Commands.ConfigService.GetCurrentAsync"/>.</summary>
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
    public async Task<RevisionPage?> ListAsync(int limit, ConfigRevisionId? after, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxPageSize);
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            if (await PageAsync(context, limit, after, cancellationToken).ConfigureAwait(false) is not { } rows)
            {
                return null;
            }

            var more = rows.Count > limit;
            RevisionSummary[] items = [.. rows.Take(limit).Select(ToSummary)];
            return new RevisionPage(items, more ? items[^1].Id : null);
        }
    }

    /// <summary>
    /// Like <see cref="ListAsync"/>, with what each revision changed against its parent (read from the stored change summary, so no
    /// document is decompressed): the history timeline's rows. Returns null when <paramref name="after"/> names no revision.
    /// </summary>
    /// <exception cref="RevisionIntegrityException">A stored revision's metadata or change summary is corrupt.</exception>
    public async Task<RevisionTimelinePage?> ListWithChangesAsync(int limit, ConfigRevisionId? after, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxPageSize);
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            if (await PageAsync(context, limit, after, cancellationToken).ConfigureAwait(false) is not { } rows)
            {
                return null;
            }

            var more = rows.Count > limit;
            RevisionTimelineEntry[] items = [.. rows.Take(limit).Select(row => new RevisionTimelineEntry(ToSummary(row), ReadChanges(row)))];
            return new RevisionTimelinePage(items, more ? items[^1].Revision.Id : null);
        }
    }

    /// <summary>A revision with what it changed and its document; null if there is none with that ID.</summary>
    public async Task<RevisionDetail?> GetAsync(ConfigRevisionId id, CancellationToken cancellationToken = default)
    {
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var stored = await ConfigRevisionStore.GetAsync(context, id, cancellationToken).ConfigureAwait(false);
            return stored is null ? null : new RevisionDetail(ToSummary(stored.Info), ReadChanges(stored.Info), ReadDocument(stored));
        }
    }

    /// <summary>What changed from <paramref name="from"/> to <paramref name="to"/>; null if either doesn't exist.</summary>
    public async Task<RevisionDiff?> DiffAsync(ConfigRevisionId from, ConfigRevisionId to, CancellationToken cancellationToken = default)
    {
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var older = await ConfigRevisionStore.GetAsync(context, from, cancellationToken).ConfigureAwait(false);
            var newer = await ConfigRevisionStore.GetAsync(context, to, cancellationToken).ConfigureAwait(false);
            return older is null || newer is null
                ? null
                : new RevisionDiff(from, to, ConfigDiff.Compare(ReadDocument(older), ReadDocument(newer)));
        }
    }

    /// <summary>Up to <paramref name="limit"/> + 1 revisions older than <paramref name="after"/> (one more than asked: whether it exists
    /// says whether older revisions remain); null when <paramref name="after"/> names no revision.</summary>
    private static async Task<IReadOnlyList<RevisionInfo>?> PageAsync(ConfigDbContext context, int limit, ConfigRevisionId? after, CancellationToken cancellationToken)
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

        return await ConfigRevisionStore.ListAsync(context, limit + 1, beforeSeq, cancellationToken).ConfigureAwait(false);
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
