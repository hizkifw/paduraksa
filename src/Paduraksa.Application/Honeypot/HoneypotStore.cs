using Microsoft.EntityFrameworkCore;

using Paduraksa.Core.Model;
using Paduraksa.Data;
using Paduraksa.Data.Honeypot;
using Paduraksa.Proxy.Honeypot;

namespace Paduraksa.Application.Honeypot;

/// <summary>What the honeypot tables keep (docs/design/waf.md "Honeypot", "Recording"): an age, and a cap on each table, oldest first.</summary>
/// <param name="Retention">Rows older than this go: sessions by their last request, requests by their time.</param>
/// <param name="MaxSessions">The most sessions kept; the ones that began first go first, with their requests.</param>
/// <param name="MaxRequests">The most requests kept; the oldest go first.</param>
public sealed record HoneypotRetention(TimeSpan Retention, int MaxSessions, int MaxRequests)
{
    /// <summary>30 days, 100 000 sessions, 1 000 000 requests.</summary>
    public static HoneypotRetention Default { get; } = new(TimeSpan.FromDays(30), 100_000, 1_000_000);
}

/// <summary>What one prune removed.</summary>
/// <param name="Sessions">Sessions deleted, by age or by the cap.</param>
/// <param name="Requests">Requests deleted, by age, by the cap or with their session.</param>
public readonly record struct HoneypotPruned(int Sessions, int Requests)
{
    public int Total => Sessions + Requests;
}

/// <summary>
/// The honeypot's sessions and requests in the config database: writes what the recorder hands over, deletes what is past retention, and the reads the API makes.
/// </summary>
/// <remarks>
/// <para><b>A write replaces a session's totals.</b> The recorder's counters are running totals over the whole session, so a record is stored as it is:
/// a new row, or the existing row overwritten. Requests are only ever inserted.</para>
/// <para><b>A failed write loses nothing the recorder still holds.</b> The batch goes back to the recorder, which bounds what waits (<see cref="HoneypotRecorder"/>).</para>
/// </remarks>
public sealed class HoneypotStore
{
    // Sessions looked up by ID in one statement; well under SQLite's bound-variable limit.
    private const int LookupChunk = 500;

    private readonly IDbContextFactory<ConfigDbContext> _contexts;
    private readonly HoneypotRecorder _recorder;

    internal HoneypotStore(IDbContextFactory<ConfigDbContext> contexts, HoneypotRecorder recorder)
    {
        _contexts = contexts;
        _recorder = recorder;
    }

    /// <summary>Folds what the sink queued into sessions and writes what changed. A failed write puts the batch back for the next pass and throws.</summary>
    public async Task WritePendingAsync(CancellationToken cancellationToken)
    {
        _recorder.Drain();
        var batch = _recorder.Take();
        if (batch.IsEmpty)
        {
            return;
        }

        try
        {
            await WriteAsync(batch, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _recorder.Requeue(batch);
            throw;
        }
    }

    internal async Task WriteAsync(HoneypotBatch batch, CancellationToken cancellationToken)
    {
        await _contexts.UseAsync(async context =>
        {
            context.ChangeTracker.AutoDetectChangesEnabled = false;
            Dictionary<HoneypotSessionId, HoneypotSessionRow> stored = [];
            for (var offset = 0; offset < batch.Sessions.Count; offset += LookupChunk)
            {
                var ids = new List<HoneypotSessionId>(LookupChunk);
                for (var i = offset; i < Math.Min(batch.Sessions.Count, offset + LookupChunk); i++)
                {
                    ids.Add(batch.Sessions[i].Id);
                }

                foreach (var row in await context.HoneypotSessions.Where(row => ids.Contains(row.Id)).ToListAsync(cancellationToken).ConfigureAwait(false))
                {
                    stored[row.Id] = row;
                }
            }

            foreach (var record in batch.Sessions)
            {
                if (stored.TryGetValue(record.Id, out var row))
                {
                    HoneypotRows.Fill(row, record);
                }
                else
                {
                    row = new HoneypotSessionRow { Id = record.Id };
                    HoneypotRows.Fill(row, record);
                    context.HoneypotSessions.Add(row);
                }
            }

            foreach (var request in batch.Requests)
            {
                context.HoneypotRequests.Add(HoneypotRows.RequestOf(request));
            }

            // Changes are detected once, here, rather than at every Add: a flood's batch is thousands of rows.
            context.ChangeTracker.DetectChanges();
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes what is past <paramref name="retention"/>: rows older than its age, then the oldest sessions (with their requests) over its session cap,
    /// then the oldest requests over its request cap. One statement each, so a prune holds the writer briefly.
    /// </summary>
    public async Task<HoneypotPruned> PruneAsync(DateTimeOffset now, HoneypotRetention retention, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(retention);
        var cutoff = (now - retention.Retention).ToUnixTimeMilliseconds();
        return await _contexts.UseAsync(async context =>
        {
            var requests = await context.HoneypotRequests.Where(row => row.Time < cutoff).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            var sessions = await context.HoneypotSessions.Where(row => row.LastSeen < cutoff).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

            var sessionCount = await context.HoneypotSessions.CountAsync(cancellationToken).ConfigureAwait(false);
            if (sessionCount > retention.MaxSessions)
            {
                // Sessions are ordered by when they began: the Nth oldest is the last one to go.
                var last = await context.HoneypotSessions.OrderBy(row => row.Id).Skip(sessionCount - retention.MaxSessions - 1).Select(row => row.Id).FirstAsync(cancellationToken).ConfigureAwait(false);
                requests += await context.HoneypotRequests.Where(row => row.SessionId <= last).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                sessions += await context.HoneypotSessions.Where(row => row.Id <= last).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            }

            var requestCount = await context.HoneypotRequests.CountAsync(cancellationToken).ConfigureAwait(false);
            if (requestCount > retention.MaxRequests)
            {
                var last = await context.HoneypotRequests.OrderBy(row => row.Id).Skip(requestCount - retention.MaxRequests - 1).Select(row => row.Id).FirstAsync(cancellationToken).ConfigureAwait(false);
                requests += await context.HoneypotRequests.Where(row => row.Id <= last).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            }

            return new HoneypotPruned(sessions, requests);
        }, cancellationToken).ConfigureAwait(false);
    }
}
