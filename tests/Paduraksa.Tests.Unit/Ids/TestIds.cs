using Paduraksa.Core.Ids;

namespace Paduraksa.Tests.Unit.Ids;

[TypedId("host")]
public readonly partial record struct HostId;

[TypedId("chl", IdKind.UuidV4)]
public readonly partial record struct ChallengeId;

[TypedId("aud", IdKind.Int64Ordered)]
public readonly partial record struct AuditEventId;

[TypedId("usr2fa", IdKind.UuidV7)]
internal readonly partial record struct InternalId;

/// <summary>Settable clock for generator tests.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
