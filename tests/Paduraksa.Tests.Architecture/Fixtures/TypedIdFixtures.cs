// Real generated typed IDs for the prefix-uniqueness sanity check. NOT production code; the uniqueness scan
// covers src assemblies only, so these never collide with src prefixes.
using Paduraksa.TypedIds;

namespace Paduraksa.Tests.Architecture.Fixtures;

[TypedId("fxhost")]
public readonly partial record struct FixtureHostId;

[TypedId("fxaud", IdKind.Int64Ordered)]
public readonly partial record struct FixtureAuditId;
