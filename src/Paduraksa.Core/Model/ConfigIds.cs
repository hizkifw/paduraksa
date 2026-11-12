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

/// <summary>A rate-limit policy: one token bucket's rate, burst and partition key, which hosts and routes name.</summary>
[TypedId("rlp")]
public readonly partial record struct RateLimitPolicyId;

/// <summary>An access list: IP allow and deny entries and basic-auth users, which hosts and routes name.</summary>
[TypedId("acl")]
public readonly partial record struct AccessListId;

/// <summary>A rule set: an ordered list of path, method and user-agent rules, which hosts and routes name. The built-in presets
/// have reserved IDs.</summary>
[TypedId("rs")]
public readonly partial record struct RuleSetId;

/// <summary>An authentication provider: a forward-auth endpoint (authentik, Authelia, oauth2-proxy or any other) that hosts and routes
/// name to ask who a request is before it is forwarded.</summary>
[TypedId("authp")]
public readonly partial record struct AuthProviderId;

/// <summary>A basic-auth user's password in the vault: the config document names it, the vault holds what verifies it.</summary>
[TypedId("pwc")]
public readonly partial record struct PasswordCredentialId;

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

/// <summary>A one-minute metric rollup row (a host's, an upstream's or the process's totals for one minute). Int64Ordered:
/// admin-only, append-only and high-volume.</summary>
[TypedId("mrl", IdKind.Int64Ordered)]
public readonly partial record struct MetricRollupId;

/// <summary>A five-minute or hourly traffic analytics rollup (one host's visitors, top paths and clients for a stretch of time, or the record that the
/// process watched it). Int64Ordered: admin-only, append-only and high-volume.</summary>
[TypedId("anr", IdKind.Int64Ordered)]
public readonly partial record struct AnalyticsRollupId;
