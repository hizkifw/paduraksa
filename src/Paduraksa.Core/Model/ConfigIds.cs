using Paduraksa.Core.Ids;

namespace Paduraksa.Core.Model;

// Typed IDs of the config model. Defined in Core because every layer names them: the config document
// (Contracts), the EF entities (Data), the compiled snapshot (Proxy) and the command pipeline (Application).

/// <summary>A named preset of host-settings overrides.</summary>
[TypedId("prof")]
public readonly partial record struct ProfileId;

/// <summary>A host.</summary>
[TypedId("host")]
public readonly partial record struct HostId;

/// <summary>A route of a proxy host.</summary>
[TypedId("rte")]
public readonly partial record struct RouteId;

/// <summary>An upstream, compiled into a YARP cluster.</summary>
[TypedId("ups")]
public readonly partial record struct UpstreamId;

/// <summary>A destination of an upstream; must stay stable for YARP health state to survive.</summary>
[TypedId("dst")]
public readonly partial record struct DestinationId;

/// <summary>A certificate. Hosts bind to one; the key material stays in the vault.</summary>
[TypedId("crt")]
public readonly partial record struct CertificateId;

/// <summary>A committed config revision.</summary>
[TypedId("rev")]
public readonly partial record struct ConfigRevisionId;

/// <summary>An audit-log entry. Int64Ordered: admin-only and append-only.</summary>
[TypedId("aud", IdKind.Int64Ordered)]
public readonly partial record struct AuditEventId;

/// <summary>An admin user; its entity is <c>UserRow</c> in Data.</summary>
[TypedId("usr")]
public readonly partial record struct UserId;

/// <summary>An API token.</summary>
[TypedId("tok")]
public readonly partial record struct ApiTokenId;
