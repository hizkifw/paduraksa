using Paduraksa.TypedIds;

namespace Paduraksa.Tests.Unit.Ids;

[TypedId("host")]
public readonly partial record struct HostId;

[TypedId("chl", IdKind.UuidV4)]
public readonly partial record struct ChallengeId;

[TypedId("aud", IdKind.Int64Ordered)]
public readonly partial record struct AuditEventId;

[TypedId("int", IdKind.UuidV7)]
internal readonly partial record struct InternalId;
