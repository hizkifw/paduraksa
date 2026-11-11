using Paduraksa.Application.LastKnownGood;
using Paduraksa.Contracts.Commands;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Yarp;

namespace Paduraksa.Application.Commands;

/// <summary>A committed revision's queued publish.</summary>
/// <param name="Revision">The revision it publishes (<see cref="ConfigRevisionId.Empty"/>, seq 0: the empty config).</param>
/// <param name="Task">The publish.</param>
/// <param name="LastKnownGood">The entry to record once it is live; null for the empty config.</param>
/// <param name="FromDatabase">The config is the database's latest revision. False for a republish of the
/// last-known-good document (the database can't be used): going live doesn't end a degraded start.</param>
internal sealed record PublishTicket(
    RevisionRef Revision,
    Task<PublishResult> Task,
    LastKnownGoodEntry? LastKnownGood,
    bool FromDatabase = true);

/// <param name="Result">The command's result.</param>
/// <param name="Publish">The queued publish of a committed command.</param>
/// <param name="Candidate">A dry run's compiled snapshot (never published); the request simulator runs on it.</param>
internal readonly record struct Execution(
    ConfigCommandResult Result,
    PublishTicket? Publish = null,
    RuntimeSnapshot? Candidate = null);
