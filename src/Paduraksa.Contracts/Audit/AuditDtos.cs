using System.Text.Json;

using Paduraksa.Contracts.Revisions;
using Paduraksa.Core.Model;

namespace Paduraksa.Contracts.Audit;

/// <summary>An audit event, as the log reads back. The hashes of the chain are left to <see cref="AuditVerification"/>.</summary>
/// <param name="Id">The event.</param>
/// <param name="Timestamp">When it was appended (UTC).</param>
/// <param name="Actor">Who acted.</param>
/// <param name="Channel">Where it came in.</param>
/// <param name="Action">The dotted action name, e.g. <c>config.host.update</c>.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="Target">The typed-ID string of what was acted on, if any.</param>
/// <param name="ClientIp">The client address, if the action came in over the network.</param>
/// <param name="Details">Extra details the action recorded: a JSON object, never a secret.</param>
/// <param name="RevisionId">The config revision it committed, if any.</param>
public sealed record AuditEventSummary(
    AuditEventId Id,
    DateTimeOffset Timestamp,
    ActorRef Actor,
    Channel Channel,
    string Action,
    AuditOutcome Outcome,
    string? Target = null,
    string? ClientIp = null,
    JsonElement? Details = null,
    ConfigRevisionId? RevisionId = null);

/// <summary>Filters of an audit query; every filter that is set must match.</summary>
/// <param name="From">Events at or after this time.</param>
/// <param name="To">Events before this time.</param>
/// <param name="Actor">One actor: <c>system</c>, <c>cli</c>, a user ID or an API token ID.</param>
/// <param name="Action">An action name, or a prefix ending in <c>*</c> (<c>config.*</c>).</param>
/// <param name="Target">The typed-ID string of what was acted on.</param>
public sealed record AuditFilter(DateTimeOffset? From = null, DateTimeOffset? To = null, string? Actor = null, string? Action = null, string? Target = null);

/// <summary>A page of audit events, newest first, before the API turns the continuation into a cursor.</summary>
/// <param name="Items">The page.</param>
/// <param name="Next">The last event of the page when older ones remain; null otherwise.</param>
public sealed record AuditPage(IReadOnlyList<AuditEventSummary> Items, AuditEventId? Next = null);

/// <summary>The first event of the chain that doesn't verify.</summary>
/// <param name="EventId">The event. Every one before it verifies.</param>
/// <param name="Problem">The kind of break: <c>previousHashMismatch</c>, <c>hashMismatch</c> or <c>unreadable</c>.</param>
/// <param name="Description">A sentence for an operator.</param>
public sealed record AuditBreak(AuditEventId EventId, string Problem, string Description);

/// <summary>The result of walking the audit hash chain.</summary>
/// <param name="Intact">Every event verified.</param>
/// <param name="VerifiedEvents">How many events verified, from the first up to the break.</param>
/// <param name="Head">The last verified event.</param>
/// <param name="HeadHash">Its hash, lowercase hex: keep a copy elsewhere to detect the removal of the newest events.</param>
/// <param name="Break">The first break, when there is one.</param>
public sealed record AuditVerification(bool Intact, long VerifiedEvents, AuditEventId? Head = null, string? HeadHash = null, AuditBreak? Break = null);
