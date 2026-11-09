using Microsoft.EntityFrameworkCore;
using Paduraksa.Core.Model;
using Paduraksa.Data;
using Paduraksa.Data.Audit;

namespace Paduraksa.Application.Audit;

/// <summary>What is wrong at the first broken event.</summary>
internal enum AuditChainProblem
{
    /// <summary>
    /// The event's <c>PrevHash</c> isn't the previous event's hash (or, for the first event, the genesis): a row before
    /// it was deleted, inserted or moved, or the previous row's hash was rewritten.
    /// </summary>
    BrokenLink = 1,

    /// <summary>The event's fields don't hash to its stored hash: the row was edited.</summary>
    HashMismatch = 2,

    /// <summary>The row can't be read back as an event (e.g. an unknown channel name): it was edited.</summary>
    Unreadable = 3,
}

/// <summary>The first break found by <see cref="AuditChainVerifier.VerifyAsync(ConfigDbContext, CancellationToken)"/>.</summary>
/// <param name="EventId">The first event that doesn't verify. Every event before it does.</param>
/// <param name="Problem">What is wrong with it.</param>
/// <param name="Description">A sentence for logs and the dashboard.</param>
internal sealed record AuditChainBreak(AuditEventId EventId, AuditChainProblem Problem, string Description);

/// <summary>The result of walking the audit chain.</summary>
/// <param name="VerifiedEvents">How many events verified, from the first one up to (not including) the break.</param>
/// <param name="Head">The last verified event, or null if none did.</param>
/// <param name="HeadHash">Its hash (empty if none). Record it elsewhere to detect later truncation (see remarks).</param>
/// <param name="Break">The first break, or null if the whole chain verified.</param>
/// <remarks>
/// A hash chain detects edits, deletions and reordering anywhere <em>before</em> the newest event, but not the
/// removal of the newest events themselves, nor a rewrite of the whole chain by someone with write access to the
/// file. Comparing <paramref name="Head"/>/<paramref name="HeadHash"/> against a copy kept outside the database
/// (a backup, an export, an external log) closes the first gap.
/// </remarks>
internal sealed record AuditChainReport(long VerifiedEvents, AuditEventId? Head, ReadOnlyMemory<byte> HeadHash, AuditChainBreak? Break)
{
    public bool IsIntact => Break is null;
}

/// <summary>Walks the audit chain in ID order and reports the first event that doesn't verify.</summary>
internal static class AuditChainVerifier
{
    private const int BatchSize = 1000;

    /// <summary>
    /// Checks every event, oldest first: that its <c>PrevHash</c> is the previous event's hash (the genesis for the
    /// first), and that its fields hash to its stored hash. Stops at the first failure. Reads in batches, so memory
    /// stays flat however long the log is; read-only, so it can run on any context, at any time.
    /// </summary>
    public static Task<AuditChainReport> VerifyAsync(ConfigDbContext context, CancellationToken cancellationToken = default) =>
        VerifyAsync(context, BatchSize, cancellationToken);

    /// <summary><see cref="VerifyAsync(ConfigDbContext, CancellationToken)"/> with a chosen batch size (tests).</summary>
    internal static async Task<AuditChainReport> VerifyAsync(ConfigDbContext context, int batchSize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        var expectedPrev = AuditChain.GenesisPrevHash;
        AuditEventId? head = null;
        long verified = 0;
        while (true)
        {
            var batch = await ReadBatchAsync(context, head, batchSize, cancellationToken).ConfigureAwait(false);
            if (batch.Count == 0)
            {
                return new AuditChainReport(verified, head, head is null ? ReadOnlyMemory<byte>.Empty : expectedPrev, null);
            }

            foreach (var (id, row) in batch)
            {
                var problem = Check(id, row, head, expectedPrev);
                if (problem is not null)
                {
                    return new AuditChainReport(verified, head, head is null ? ReadOnlyMemory<byte>.Empty : expectedPrev, problem);
                }

                expectedPrev = row!.Hash;
                head = id;
                verified++;
            }
        }
    }

    private static AuditChainBreak? Check(AuditEventId id, AuditEvent? row, AuditEventId? previous, ReadOnlyMemory<byte> expectedPrev)
    {
        if (row is null)
        {
            return new(id, AuditChainProblem.Unreadable, $"Audit event {id} can't be read back as an event: the row was edited.");
        }

        if (!row.PrevHash.AsSpan().SequenceEqual(expectedPrev.Span))
        {
            return new(id, AuditChainProblem.BrokenLink, previous is { } p
                ? $"Audit event {id} doesn't follow {p}: an event between them was deleted, inserted or moved, or {p}'s hash was rewritten."
                : $"Audit event {id} is the first event but doesn't start the chain: earlier events were deleted, or it was inserted or moved.");
        }

        byte[] hash;
        try
        {
            hash = AuditChain.ComputeHash(AuditMapping.ToRecord(row));
        }
        catch (ArgumentException)
        {
            return new(id, AuditChainProblem.Unreadable, $"Audit event {id} can't be encoded: the row was edited.");
        }

        return hash.AsSpan().SequenceEqual(row.Hash)
            ? null
            : new(id, AuditChainProblem.HashMismatch, $"Audit event {id} doesn't match its hash: the row was edited.");
    }

    /// <summary>
    /// The next events after <paramref name="after"/>, in ID order. A row whose values can't be materialised (a
    /// converter rejects an edited column) comes back as <c>(id, null)</c>, so the walk can report it in place.
    /// </summary>
    private static async Task<List<(AuditEventId Id, AuditEvent? Row)>> ReadBatchAsync(
        ConfigDbContext context, AuditEventId? after, int batchSize, CancellationToken cancellationToken)
    {
        try
        {
            var rows = await After(context, after).Take(batchSize).ToListAsync(cancellationToken).ConfigureAwait(false);
            return [.. rows.Select(r => (r.Id, (AuditEvent?)r))];
        }
        catch (Exception e) when (IsUnreadable(e))
        {
            // Slow path for a damaged batch: the IDs (always readable), then each row on its own.
            var ids = await After(context, after).Take(batchSize).Select(r => r.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
            var batch = new List<(AuditEventId, AuditEvent?)>(ids.Count);
            foreach (var id in ids)
            {
                AuditEvent? row;
                try
                {
                    row = await context.AuditEvents.AsNoTracking().SingleAsync(r => r.Id == id, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception inner) when (IsUnreadable(inner))
                {
                    row = null;
                }

                batch.Add((id, row));
                if (row is null)
                {
                    break; // the walk stops here anyway
                }
            }

            return batch;
        }
    }

    private static IQueryable<AuditEvent> After(ConfigDbContext context, AuditEventId? after)
    {
        var query = context.AuditEvents.AsNoTracking();
        if (after is { } id)
        {
            query = query.Where(r => r.Id > id);
        }

        return query.OrderBy(r => r.Id);
    }

    /// <summary>
    /// A row value that can't be materialised, and nothing else: anything other (a disposed context, a cancelled read,
    /// an EF usage error, SQLite I/O) propagates. Without detailed errors EF rethrows the value converter's own
    /// exception as is (verified: <see cref="FormatException"/> from <c>ModelNames</c> or the timestamp parser,
    /// <see cref="ArgumentException"/> from a Guid of the wrong length); with <c>EnableDetailedErrors</c> it wraps it
    /// in an <see cref="InvalidOperationException"/>.
    /// </summary>
    private static bool IsUnreadable(Exception e) => e switch
    {
        FormatException or InvalidCastException => true,
        ArgumentException and not ArgumentNullException => true,
        InvalidOperationException { InnerException: { } inner } and not ObjectDisposedException => IsUnreadable(inner),
        _ => false,
    };
}
