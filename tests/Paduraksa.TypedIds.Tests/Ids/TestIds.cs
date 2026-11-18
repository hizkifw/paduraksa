using Paduraksa.TypedIds;

namespace Paduraksa.TypedIds.Tests;

// HostId, ChallengeId and AuditEventId (one of each kind) are the sample model's, in Shared/TypedIdSample.
[TypedId("int", IdKind.UuidV7)]
internal readonly partial record struct InternalId;
