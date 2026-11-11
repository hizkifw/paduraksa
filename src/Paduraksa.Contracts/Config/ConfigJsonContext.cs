using System.Text.Json.Serialization;

using Paduraksa.Core.Ids;
using Paduraksa.Core.Json;
using Paduraksa.Core.Model;

namespace Paduraksa.Contracts.Config;

/// <summary>
/// The source-generated JSON contract of the config document, shared by revisions, the API, MCP and YAML. Writing is
/// deterministic: ASCII output (the default encoder escapes everything else), <see cref="TimeSpan"/> as
/// <c>[-][d.]hh:mm:ss[.fffffff]</c>, typed IDs as their string form and enums as camelCase names. Canonical bytes also
/// need the content in canonical order and spelling; <c>Paduraksa.Application.Config.CanonicalConfigJson</c> takes care
/// of that.
/// </summary>
/// <remarks>
/// Reading is strict, because a document that half-reads is worse than one that doesn't: unknown, duplicate or missing
/// required properties, null for a required value and anything but an exact enum name are all rejected. An explicit
/// null for an optional list or nested record reads as absent (empty). Compatibility is one way only: newer builds read
/// every older document, and an older build rejects a document with fields it doesn't know.
/// <para><b>The shared enums' converters.</b> The source generator can't see a
/// <see cref="ConfigEnumConverter{TEnum}"/> attribute that sits in a referenced assembly, so the enums' spellings are
/// registered here too; a new shared enum needs its line in this list (and in the admin API's), and
/// <c>SharedEnumWireTests</c> fails without it.</para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    Converters =
    [
        typeof(TypedIdJsonConverterFactory),
        typeof(ConfigEnumConverter<HostKind>),
        typeof(ConfigEnumConverter<RouteMatchKind>),
        typeof(ConfigEnumConverter<TrustedProxyMode>),
        typeof(ConfigEnumConverter<UnknownHostBehaviour>),
        typeof(ConfigEnumConverter<TlsMinimumVersion>),
        typeof(ConfigEnumConverter<TcpAlpn>),
        typeof(ConfigEnumConverter<ServerHeaderMode>),
        typeof(ConfigEnumConverter<UpstreamTlsTrust>),
        typeof(ConfigEnumConverter<RequestIdPropagation>),
        typeof(ConfigEnumConverter<RateLimitPeriod>),
        typeof(ConfigEnumConverter<RateLimitKey>),
        typeof(ConfigEnumConverter<AccessListSatisfy>),
        typeof(ConfigEnumConverter<RobotsMode>),
        typeof(ConfigEnumConverter<ErrorPageMode>),
        typeof(ConfigEnumConverter<UpstreamErrorReplacement>),
        typeof(ConfigEnumConverter<ErrorPageSource>),
        typeof(ConfigEnumConverter<OriginLockdownMode>),
        typeof(ConfigEnumConverter<ForwardAuthCacheScope>),
        typeof(ConfigEnumConverter<ForwardAuthPreset>),
        typeof(ConfigEnumConverter<RuleAction>),
        typeof(ConfigEnumConverter<RulePathKind>),
    ])]
[JsonSerializable(typeof(ConfigDocument))]
[JsonSerializable(typeof(RateLimitPolicyConfig))]
[JsonSerializable(typeof(AccessListConfig))]
[JsonSerializable(typeof(RuleSetConfig))]
[JsonSerializable(typeof(AuthProviderConfig))]
[JsonSerializable(typeof(ConfigError))]
[JsonSerializable(typeof(IReadOnlyList<ConfigError>))]
[JsonSerializable(typeof(IReadOnlyList<ConfigChange>))]
public sealed partial class ConfigJsonContext : JsonSerializerContext;
