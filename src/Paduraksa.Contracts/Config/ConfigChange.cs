using System.Text.Json.Serialization;

using Paduraksa.Core.Json;
using Paduraksa.Core.Model;

namespace Paduraksa.Contracts.Config;

/// <summary>
/// One entry of the structural diff between two config documents: an object added, removed or modified,
/// keyed by its typed ID. It feeds the dashboard's review sheet, <c>dryRun</c> responses and revision summaries.
/// </summary>
/// <remarks>
/// <para>Routes and destinations are entries of their own, listed right after their parent and only when the parent
/// exists on both sides. Route order is config as the last tiebreaker (same match kind, path length and
/// <c>Order</c>): the canonical order is the one the host evaluates, so when the routes a host keeps come in a
/// different relative order, the host lists its <c>routes</c> field.</para>
/// <para>Version changes alone aren't changes: the version is concurrency metadata, not config.</para>
/// </remarks>
/// <param name="Kind">What happened to the object.</param>
/// <param name="Path">JSON pointer of the object: into the new document for added and modified objects, into the old
/// one for removed objects.</param>
/// <param name="Fields">Modified only: JSON pointers (into the new document) of the fields that changed, in
/// document order. A list counts as one field, so it is named as a whole; so is a nested object that is set on one side
/// only. Child objects (routes, destinations) aren't fields of their parent.</param>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "object")]
[JsonDerivedType(typeof(DocumentChange), "document")]
[JsonDerivedType(typeof(SettingsChange), "settings")]
[JsonDerivedType(typeof(ProfileChange), "profile")]
[JsonDerivedType(typeof(UpstreamChange), "upstream")]
[JsonDerivedType(typeof(DestinationChange), "destination")]
[JsonDerivedType(typeof(RateLimitPolicyChange), "rateLimitPolicy")]
[JsonDerivedType(typeof(AccessListChange), "accessList")]
[JsonDerivedType(typeof(RuleSetChange), "ruleSet")]
[JsonDerivedType(typeof(AuthProviderChange), "authProvider")]
[JsonDerivedType(typeof(HostChange), "host")]
[JsonDerivedType(typeof(RouteChange), "route")]
public abstract record ConfigChange(ConfigChangeKind Kind, string Path, IReadOnlyList<string> Fields);

/// <summary>What happened to an object.</summary>
[JsonConverter(typeof(ConfigEnumConverter<ConfigChangeKind>))]
public enum ConfigChangeKind
{
    Added,
    Removed,
    Modified,
}

/// <summary>The document itself changed: its <c>apiVersion</c> (always <see cref="ConfigChangeKind.Modified"/>, path
/// <c>""</c>).</summary>
public sealed record DocumentChange(ConfigChangeKind Kind, string Path, IReadOnlyList<string> Fields)
    : ConfigChange(Kind, Path, Fields);

/// <summary>The global settings changed (always <see cref="ConfigChangeKind.Modified"/>).</summary>
public sealed record SettingsChange(ConfigChangeKind Kind, string Path, IReadOnlyList<string> Fields)
    : ConfigChange(Kind, Path, Fields);

public sealed record ProfileChange(ConfigChangeKind Kind, ProfileId ProfileId, string Path, IReadOnlyList<string> Fields)
    : ConfigChange(Kind, Path, Fields);

public sealed record UpstreamChange(ConfigChangeKind Kind, UpstreamId UpstreamId, string Path, IReadOnlyList<string> Fields)
    : ConfigChange(Kind, Path, Fields);

public sealed record DestinationChange(
    ConfigChangeKind Kind,
    UpstreamId UpstreamId,
    DestinationId DestinationId,
    string Path,
    IReadOnlyList<string> Fields)
    : ConfigChange(Kind, Path, Fields);

public sealed record RateLimitPolicyChange(ConfigChangeKind Kind, RateLimitPolicyId RateLimitPolicyId, string Path, IReadOnlyList<string> Fields)
    : ConfigChange(Kind, Path, Fields);

public sealed record AccessListChange(ConfigChangeKind Kind, AccessListId AccessListId, string Path, IReadOnlyList<string> Fields)
    : ConfigChange(Kind, Path, Fields);

public sealed record RuleSetChange(ConfigChangeKind Kind, RuleSetId RuleSetId, string Path, IReadOnlyList<string> Fields)
    : ConfigChange(Kind, Path, Fields);

public sealed record AuthProviderChange(ConfigChangeKind Kind, AuthProviderId AuthProviderId, string Path, IReadOnlyList<string> Fields)
    : ConfigChange(Kind, Path, Fields);

public sealed record HostChange(ConfigChangeKind Kind, HostId HostId, string Path, IReadOnlyList<string> Fields)
    : ConfigChange(Kind, Path, Fields);

public sealed record RouteChange(ConfigChangeKind Kind, HostId HostId, RouteId RouteId, string Path, IReadOnlyList<string> Fields)
    : ConfigChange(Kind, Path, Fields);
