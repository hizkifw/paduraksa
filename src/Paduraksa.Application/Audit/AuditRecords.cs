using System.Net;
using Paduraksa.Core.Ids;
using Paduraksa.Core.Model;

namespace Paduraksa.Application.Audit;

/// <summary>An audit event to append (see <see cref="AuditLog.AppendAsync"/>).</summary>
/// <param name="Actor">Who acted.</param>
/// <param name="Channel">Where it came in.</param>
/// <param name="Action">A stable dotted name: lowercase segments of <c>[a-z0-9_]</c>, at least two, e.g. <c>config.host.update</c>.</param>
/// <param name="Outcome">How it ended.</param>
internal sealed record AuditEntry(Actor Actor, Channel Channel, string Action, AuditOutcome Outcome)
{
    /// <summary>The typed-ID string of what was acted on (<c>host_...</c>), of any type. See <see cref="WithTarget"/>.</summary>
    public string? Target { get; init; }

    /// <summary>The client address, for actions that came in over the network.</summary>
    public IPAddress? ClientIp { get; init; }

    /// <summary>Extra details as a JSON object, stored (and hashed) exactly as given.</summary>
    public string? Details { get; init; }

    /// <summary>The config revision the action committed, if any.</summary>
    public ConfigRevisionId? RevisionId { get; init; }

    /// <summary>The command carried the explicit lock-out override (PLAN §3.1); written into the details JSON.</summary>
    public bool Force { get; init; }

    /// <summary>The command arrived through the admin app's internal listener; written into the details JSON.</summary>
    public bool ViaAdminProxy { get; init; }

    /// <summary>This entry with <see cref="Target"/> set to <paramref name="id"/>'s string form.</summary>
    public AuditEntry WithTarget<TId>(TId id)
        where TId : struct, ITypedId<TId> =>
        this with { Target = id.ToString() };
}

/// <summary>A stored audit event, as appended or read back by the verifier.</summary>
/// <param name="Id">Int64Ordered; the chain runs in ID order.</param>
/// <param name="Timestamp">When it was appended (UTC, to the tick).</param>
/// <param name="Actor">Who acted.</param>
/// <param name="Channel">Where it came in.</param>
/// <param name="Action">The dotted action name.</param>
/// <param name="Target">The typed-ID string of what was acted on, if any.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="ClientIp">The client address as stored (text), if any.</param>
/// <param name="Details">The details JSON as stored, if any.</param>
/// <param name="RevisionId">The config revision it committed, if any.</param>
/// <param name="PrevHash">The previous event's <paramref name="Hash"/>, or <see cref="AuditChain.GenesisPrevHash"/> for the first event.</param>
/// <param name="Hash"><see cref="AuditChain.ComputeHash"/> of this event.</param>
/// <remarks>Record equality compares the hashes by reference; compare <see cref="Id"/> instead.</remarks>
internal sealed record AuditRecord(
    AuditEventId Id,
    DateTimeOffset Timestamp,
    Actor Actor,
    Channel Channel,
    string Action,
    string? Target,
    AuditOutcome Outcome,
    string? ClientIp,
    string? Details,
    ConfigRevisionId? RevisionId,
    ReadOnlyMemory<byte> PrevHash,
    ReadOnlyMemory<byte> Hash);
