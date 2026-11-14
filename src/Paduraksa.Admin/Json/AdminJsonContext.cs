using System.Text.Json;
using System.Text.Json.Serialization;
using Paduraksa.Contracts.Analytics;
using Paduraksa.Contracts.Api;
using Paduraksa.Contracts.Audit;
using Paduraksa.Contracts.Backup;
using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Drafts;
using Paduraksa.Contracts.Issuance;
using Paduraksa.Contracts.Logging;
using Paduraksa.Contracts.Metrics;
using Paduraksa.Contracts.Notifications;
using Paduraksa.Contracts.Revisions;
using Paduraksa.Contracts.Security;
using Paduraksa.Contracts.Simulation;
using Paduraksa.Core.Ids;
using Paduraksa.Core.Json;
using Paduraksa.Core.Model;

namespace Paduraksa.Admin.Json;

/// <summary>
/// The source-generated JSON contract of the resource API (<c>/api/v1</c>): the same conventions as the config document
/// (<c>ConfigJsonContext</c>): camelCase, nulls omitted, enums as their stable names, typed IDs as their string form, and strict reading
/// (unknown and duplicate properties, missing required members and nulls for required values are errors). Endpoints read and write through
/// it directly, so what a client sends is judged by these rules whatever else is registered in the host's JSON options. No type of the
/// data layer is registered here, and none can be: Admin can't see them.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false,
    AllowOutOfOrderMetadataProperties = true,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    Converters =
    [
        typeof(TypedIdJsonConverterFactory),
        typeof(ActorKindWireConverter),
        typeof(ChannelWireConverter),
        typeof(AuditOutcomeWireConverter),
        typeof(CertificateSourceWireConverter),
        typeof(CertificateStatusWireConverter),
        typeof(AcmeAccountStatusWireConverter),
        typeof(AcmeOrderStatusWireConverter),
        typeof(AcmeChallengeTypeWireConverter),
        typeof(DnsProviderKindWireConverter),
        typeof(NotificationChannelKindWireConverter),
        typeof(SmtpSecurityWireConverter),
        typeof(NotificationEventKindWireConverter),
        typeof(NotificationDeliveryStateWireConverter),
        typeof(KeySpecWireConverter),
        typeof(JwsAlgorithmWireConverter),
        // The enums the config document and the proxy share live in Core, and the source generator can't see their own
        // attribute: the same list as ConfigJsonContext's, so a host setting's enum is spelled alike on every surface.
        typeof(ConfigEnumConverter<HostKind>),
        typeof(ConfigEnumConverter<RouteMatchKind>),
        typeof(ConfigEnumConverter<TrustedProxyMode>),
        typeof(ConfigEnumConverter<UnknownHostBehaviour>),
        typeof(ConfigEnumConverter<TlsMinimumVersion>),
        typeof(ConfigEnumConverter<TcpAlpn>),
        typeof(ConfigEnumConverter<ServerHeaderMode>),
        typeof(ConfigEnumConverter<HeaderOperation>),
        typeof(ConfigEnumConverter<UpstreamTlsTrust>),
        typeof(ConfigEnumConverter<UpstreamHostHeaderMode>),
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
[JsonSerializable(typeof(ApiProblem))]
[JsonSerializable(typeof(VersionInfo))]
[JsonSerializable(typeof(CurrentConfig))]
[JsonSerializable(typeof(ConfigCommandResult))]
[JsonSerializable(typeof(HostConfig))]
[JsonSerializable(typeof(Page<HostConfig>))]
[JsonSerializable(typeof(UpstreamConfig))]
[JsonSerializable(typeof(Page<UpstreamConfig>))]
[JsonSerializable(typeof(RateLimitPolicyConfig))]
[JsonSerializable(typeof(Page<RateLimitPolicyConfig>))]
[JsonSerializable(typeof(AccessListConfig))]
[JsonSerializable(typeof(Page<AccessListConfig>))]
[JsonSerializable(typeof(RuleSetConfig))]
[JsonSerializable(typeof(Page<RuleSetConfig>))]
[JsonSerializable(typeof(AuthProviderConfig))]
[JsonSerializable(typeof(Page<AuthProviderConfig>))]
[JsonSerializable(typeof(ProfileConfig))]
[JsonSerializable(typeof(Page<ProfileConfig>))]
[JsonSerializable(typeof(RouteConfig))]
[JsonSerializable(typeof(Page<RouteConfig>))]
[JsonSerializable(typeof(GlobalSettings))]
[JsonSerializable(typeof(ServerSettings))]
[JsonSerializable(typeof(ServerSettingsView))]
[JsonSerializable(typeof(ConfigDraft))]
[JsonSerializable(typeof(Page<ConfigDraft>))]
[JsonSerializable(typeof(DraftConfig))]
[JsonSerializable(typeof(DraftOutcome))]
[JsonSerializable(typeof(AddDraftEntriesRequest))]
[JsonSerializable(typeof(Page<RevisionSummary>))]
[JsonSerializable(typeof(RevisionDetail))]
[JsonSerializable(typeof(RevisionDiff))]
[JsonSerializable(typeof(Page<AuditEventSummary>))]
[JsonSerializable(typeof(IReadOnlyList<LogFileSummary>))]
[JsonSerializable(typeof(LogFileTail))]
[JsonSerializable(typeof(LogTailEntry))]
[JsonSerializable(typeof(IReadOnlyList<LogTailEntry>))]
[JsonSerializable(typeof(LogTailSkipped))]
[JsonSerializable(typeof(AuditVerification))]
[JsonSerializable(typeof(SecurityOverview))]
[JsonSerializable(typeof(MetricsOverview))]
[JsonSerializable(typeof(AnalyticsOverview))]
[JsonSerializable(typeof(CertificateSummary))]
[JsonSerializable(typeof(Page<CertificateSummary>))]
[JsonSerializable(typeof(CertificateDetail))]
[JsonSerializable(typeof(CertificateOperationResult))]
[JsonSerializable(typeof(SelfSignedCertificateRequest))]
[JsonSerializable(typeof(LocalCaRequest))]
[JsonSerializable(typeof(LocalCaRotateRequest))]
[JsonSerializable(typeof(LocalCaStatus))]
[JsonSerializable(typeof(LocalCaReissueResult))]
[JsonSerializable(typeof(LocalCaLeafRequest))]
[JsonSerializable(typeof(ReissueRequest))]
[JsonSerializable(typeof(CertificateIssueRequest))]
[JsonSerializable(typeof(IssuanceResult))]
[JsonSerializable(typeof(AcmeOrderSummary))]
[JsonSerializable(typeof(Page<AcmeOrderSummary>))]
[JsonSerializable(typeof(AcmeAccountSummary))]
[JsonSerializable(typeof(Page<AcmeAccountSummary>))]
[JsonSerializable(typeof(CreateAcmeAccountBody))]
[JsonSerializable(typeof(DnsProviderSummary))]
[JsonSerializable(typeof(Page<DnsProviderSummary>))]
[JsonSerializable(typeof(CreateDnsProviderBody))]
[JsonSerializable(typeof(RotateDnsCredentialBody))]
[JsonSerializable(typeof(DnsProviderTestOutcome))]
[JsonSerializable(typeof(NotificationChannelBody))]
[JsonSerializable(typeof(NotificationChannelSummary))]
[JsonSerializable(typeof(Page<NotificationChannelSummary>))]
[JsonSerializable(typeof(NotificationTestOutcome))]
[JsonSerializable(typeof(Page<NotificationHistoryEvent>))]
[JsonSerializable(typeof(NotificationRetryOutcome))]
[JsonSerializable(typeof(UpstreamHealthView))]
[JsonSerializable(typeof(IReadOnlyList<UpstreamHealthView>))]
[JsonSerializable(typeof(CaRootCertificate))]
[JsonSerializable(typeof(SimulateBody))]
[JsonSerializable(typeof(BackupRequest))]
[JsonSerializable(typeof(ErrorPagePreviewRequest))]
[JsonSerializable(typeof(ErrorPagePreview))]
[JsonSerializable(typeof(SimulationResult))]
public sealed partial class AdminJsonContext : JsonSerializerContext
{
    /// <summary>
    /// Makes the host's HTTP JSON options follow this contract: strict reading, and the typed-ID and enum-name converters applied (a
    /// context placed in the resolver chain brings neither its converters nor its options with it), with the context first in the chain.
    /// Whatever the host serializes through its HTTP options (an ASP.NET problem, a bound parameter) then writes typed IDs and enums as strings.
    /// </summary>
    public static void AddTo(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.RespectNullableAnnotations = true;
        options.RespectRequiredConstructorParameters = true;
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        options.AllowDuplicateProperties = false;
        var contract = Default.Options;
        foreach (var converter in contract.Converters)
        {
            if (!options.Converters.Any(c => c.GetType() == converter.GetType()))
            {
                options.Converters.Add(converter);
            }
        }

        if (!options.TypeInfoResolverChain.Contains(Default))
        {
            options.TypeInfoResolverChain.Insert(0, Default);
        }
    }
}
