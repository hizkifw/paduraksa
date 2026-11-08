using Paduraksa.Core.Ids;

namespace Paduraksa.Core.Model;

// Typed IDs of the config model (PLAN §4, §5). Defined in Core because every layer names them: the config document
// (Contracts), the EF entities (Data), the compiled snapshot (Proxy) and the command pipeline (Application).

/// <summary>A named preset of host-settings overrides (PLAN §5.1).</summary>
[TypedId("prof")]
public readonly partial record struct ProfileId;

/// <summary>A host (PLAN §5.1).</summary>
[TypedId("host")]
public readonly partial record struct HostId;

/// <summary>A route of a proxy host (PLAN §5.1).</summary>
[TypedId("rte")]
public readonly partial record struct RouteId;

/// <summary>An upstream, compiled into a YARP cluster (PLAN §5.1).</summary>
[TypedId("ups")]
public readonly partial record struct UpstreamId;

/// <summary>A destination of an upstream; must stay stable for YARP health state to survive (PLAN §6.3).</summary>
[TypedId("dst")]
public readonly partial record struct DestinationId;

/// <summary>A certificate (PLAN §5.1, §9). Hosts bind to one; the key material stays in the vault.</summary>
[TypedId("crt")]
public readonly partial record struct CertificateId;

/// <summary>A committed config revision (PLAN §5.2, §6.1).</summary>
[TypedId("rev")]
public readonly partial record struct ConfigRevisionId;

/// <summary>An audit-log entry (PLAN §5.2). Int64Ordered: admin-only and append-only.</summary>
[TypedId("aud", IdKind.Int64Ordered)]
public readonly partial record struct AuditEventId;

/// <summary>An admin user (PLAN §5.2); audit actors named it from M2 and the entity is <c>UserRow</c> in Data.</summary>
[TypedId("usr")]
public readonly partial record struct UserId;

/// <summary>An API token (PLAN §4.3, §5.2); audit actors named it from M2.</summary>
[TypedId("tok")]
public readonly partial record struct ApiTokenId;
