using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Paduraksa.Contracts.Audit;
using Paduraksa.Contracts.Revisions;
using Paduraksa.Core.Model;
using Paduraksa.Data;
using Paduraksa.Data.Audit;

namespace Paduraksa.Application.Audit;

/// <summary>
/// Reads the audit log for the surfaces: filtered pages, newest first, and the chain check. Read-only and without authorization of its
/// own: the caller checks <c>audit:read</c>. It returns Contracts DTOs only; the hashes of the chain stay out of events.
/// </summary>
public sealed class AuditQueryService
{
    /// <summary>The largest page.</summary>
    public const int MaxPageSize = 500;

    private readonly IDbContextFactory<ConfigDbContext> _contexts;

    internal AuditQueryService(IDbContextFactory<ConfigDbContext> contexts)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        _contexts = contexts;
    }

    /// <summary>
    /// Events matching <paramref name="filter"/>, newest first. <paramref name="after"/> is the last event of the previous page.
    /// </summary>
    /// <exception cref="ArgumentException">The filter's actor isn't <c>system</c>, <c>cli</c>, a user ID or an API token ID.</exception>
    public async Task<AuditPage> QueryAsync(AuditFilter filter, int limit, AuditEventId? after, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxPageSize);
        (ActorKind Kind, UserId? User, ApiTokenId? Token)? actor = filter.Actor is null ? null : ParseActor(filter.Actor);

        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var query = context.AuditEvents.AsNoTracking();
            if (after is { } cursor)
            {
                query = query.Where(e => e.Id < cursor);
            }

            if (filter.From is { } from)
            {
                var utc = from.ToUniversalTime();
                query = query.Where(e => e.Timestamp >= utc);
            }

            if (filter.To is { } to)
            {
                var utc = to.ToUniversalTime();
                query = query.Where(e => e.Timestamp < utc);
            }

            if (actor is { } who)
            {
                var kind = who.Kind;
                var user = who.User;
                var token = who.Token;
                query = kind switch
                {
                    ActorKind.User => query.Where(e => e.ActorKind == kind && e.ActorUserId == user),
                    ActorKind.Token => query.Where(e => e.ActorKind == kind && e.ActorTokenId == token),
                    _ => query.Where(e => e.ActorKind == kind),
                };
            }

            if (filter.Action is { Length: > 0 } action)
            {
                if (action.EndsWith('*'))
                {
                    var prefix = action[..^1];
                    query = query.Where(e => e.Action.StartsWith(prefix));
                }
                else
                {
                    query = query.Where(e => e.Action == action);
                }
            }

            if (filter.Target is { Length: > 0 } target)
            {
                query = query.Where(e => e.Target == target);
            }

            if (filter.Outcome is { } outcome)
            {
                query = query.Where(e => e.Outcome == outcome);
            }

            // One more than asked: whether it exists says whether older events remain.
            var rows = await query.OrderByDescending(e => e.Id).Take(limit + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
            var more = rows.Count > limit;
            AuditEventSummary[] items = [.. rows.Take(limit).Select(ToSummary)];
            return new AuditPage(items, more ? items[^1].Id : null);
        }
    }

    /// <summary>Walks the whole chain and reports the first event that doesn't verify.</summary>
    public async Task<AuditVerification> VerifyAsync(CancellationToken cancellationToken = default)
    {
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var report = await AuditChainVerifier.VerifyAsync(context, cancellationToken).ConfigureAwait(false);
            return new AuditVerification(
                report.IsIntact,
                report.VerifiedEvents,
                report.Head,
                report.Head is null ? null : Convert.ToHexStringLower(report.HeadHash.Span),
                report.Break is { } b ? new AuditBreak(b.EventId, ProblemName(b.Problem), b.Description) : null);
        }
    }

    /// <summary>Parses an actor filter: <c>system</c>, <c>cli</c>, a user ID or an API token ID.</summary>
    internal static (ActorKind Kind, UserId? User, ApiTokenId? Token) ParseActor(string actor)
    {
        if (actor == ActorKind.System.ToName())
        {
            return (ActorKind.System, null, null);
        }

        if (actor == ActorKind.Cli.ToName())
        {
            return (ActorKind.Cli, null, null);
        }

        if (UserId.TryParse(actor, out var user))
        {
            return (ActorKind.User, user, null);
        }

        return ApiTokenId.TryParse(actor, out var token)
            ? (ActorKind.Token, null, token)
            : throw new ArgumentException("The actor must be 'system', 'cli', a user ID or an API token ID.", nameof(actor));
    }

    private static string ProblemName(AuditChainProblem problem) => problem switch
    {
        AuditChainProblem.BrokenLink => "previousHashMismatch",
        AuditChainProblem.HashMismatch => "hashMismatch",
        _ => "unreadable",
    };

    private static AuditEventSummary ToSummary(AuditEvent e)
    {
        JsonElement? details = null;
        if (e.Details is not null)
        {
            try
            {
                using var document = JsonDocument.Parse(e.Details);
                details = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                // Details are stored as valid JSON; an edited row shows without them, and the chain check reports it.
            }
        }

        return new AuditEventSummary(
            e.Id,
            e.Timestamp,
            new ActorRef(e.ActorKind, e.ActorUserId, e.ActorTokenId),
            e.Channel,
            e.Action,
            e.Outcome,
            e.Target,
            e.ClientIp,
            details,
            e.RevisionId);
    }
}
