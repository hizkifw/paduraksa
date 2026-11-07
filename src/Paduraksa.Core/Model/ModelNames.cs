namespace Paduraksa.Core.Model;

/// <summary>
/// The stable, lowercase names of the model's enums. These names are what gets persisted (database columns) and
/// hashed (the audit chain), so they are part of the storage format: never rename one, only add new ones. The
/// numeric enum values are never stored.
/// </summary>
public static class ModelNames
{
    public static string ToName(this ActorKind kind) => kind switch
    {
        ActorKind.System => "system",
        ActorKind.Cli => "cli",
        ActorKind.User => "user",
        ActorKind.Token => "token",
        _ => throw Undefined(kind),
    };

    public static string ToName(this Channel channel) => channel switch
    {
        Channel.System => "system",
        Channel.Cli => "cli",
        Channel.Dashboard => "dashboard",
        Channel.Api => "api",
        Channel.Mcp => "mcp",
        _ => throw Undefined(channel),
    };

    public static string ToName(this AuditOutcome outcome) => outcome switch
    {
        AuditOutcome.Success => "success",
        AuditOutcome.Denied => "denied",
        AuditOutcome.Invalid => "invalid",
        AuditOutcome.Conflict => "conflict",
        AuditOutcome.Failed => "failed",
        _ => throw Undefined(outcome),
    };

    public static string ToName(this CertificateSource source) => source switch
    {
        CertificateSource.Acme => "acme",
        CertificateSource.Uploaded => "uploaded",
        CertificateSource.SelfSigned => "self_signed",
        CertificateSource.LocalCa => "local_ca",
        CertificateSource.LocalCaRoot => "local_ca_root",
        _ => throw Undefined(source),
    };

    public static string ToName(this CertificateStatus status) => status switch
    {
        CertificateStatus.Pending => "pending",
        CertificateStatus.Active => "active",
        CertificateStatus.Failed => "failed",
        _ => throw Undefined(status),
    };

    public static string ToName(this AcmeAccountStatus status) => status switch
    {
        AcmeAccountStatus.Active => "active",
        AcmeAccountStatus.Deactivated => "deactivated",
        _ => throw Undefined(status),
    };

    public static string ToName(this AcmeOrderStatus status) => status switch
    {
        AcmeOrderStatus.Pending => "pending",
        AcmeOrderStatus.Ordered => "ordered",
        AcmeOrderStatus.Finalizing => "finalizing",
        AcmeOrderStatus.Valid => "valid",
        AcmeOrderStatus.Failed => "failed",
        _ => throw Undefined(status),
    };

    public static string ToName(this AcmeChallengeType type) => type switch
    {
        AcmeChallengeType.Http01 => "http-01",
        AcmeChallengeType.Dns01 => "dns-01",
        _ => throw Undefined(type),
    };

    public static string ToName(this DnsProviderKind kind) => kind switch
    {
        DnsProviderKind.Cloudflare => "cloudflare",
        DnsProviderKind.DeSec => "desec",
        _ => throw Undefined(kind),
    };

    /// <exception cref="FormatException"><paramref name="name"/> is not one of the names above.</exception>
    public static ActorKind ParseActorKind(string name) => name switch
    {
        "system" => ActorKind.System,
        "cli" => ActorKind.Cli,
        "user" => ActorKind.User,
        "token" => ActorKind.Token,
        _ => throw NotAName<ActorKind>(name),
    };

    /// <inheritdoc cref="ParseActorKind"/>
    public static Channel ParseChannel(string name) => name switch
    {
        "system" => Channel.System,
        "cli" => Channel.Cli,
        "dashboard" => Channel.Dashboard,
        "api" => Channel.Api,
        "mcp" => Channel.Mcp,
        _ => throw NotAName<Channel>(name),
    };

    /// <inheritdoc cref="ParseActorKind"/>
    public static AuditOutcome ParseAuditOutcome(string name) => name switch
    {
        "success" => AuditOutcome.Success,
        "denied" => AuditOutcome.Denied,
        "invalid" => AuditOutcome.Invalid,
        "conflict" => AuditOutcome.Conflict,
        "failed" => AuditOutcome.Failed,
        _ => throw NotAName<AuditOutcome>(name),
    };

    /// <inheritdoc cref="ParseActorKind"/>
    public static CertificateSource ParseCertificateSource(string name) => name switch
    {
        "acme" => CertificateSource.Acme,
        "uploaded" => CertificateSource.Uploaded,
        "self_signed" => CertificateSource.SelfSigned,
        "local_ca" => CertificateSource.LocalCa,
        "local_ca_root" => CertificateSource.LocalCaRoot,
        _ => throw NotAName<CertificateSource>(name),
    };

    /// <inheritdoc cref="ParseActorKind"/>
    public static CertificateStatus ParseCertificateStatus(string name) => name switch
    {
        "pending" => CertificateStatus.Pending,
        "active" => CertificateStatus.Active,
        "failed" => CertificateStatus.Failed,
        _ => throw NotAName<CertificateStatus>(name),
    };

    /// <inheritdoc cref="ParseActorKind"/>
    public static AcmeAccountStatus ParseAcmeAccountStatus(string name) => name switch
    {
        "active" => AcmeAccountStatus.Active,
        "deactivated" => AcmeAccountStatus.Deactivated,
        _ => throw NotAName<AcmeAccountStatus>(name),
    };

    /// <inheritdoc cref="ParseActorKind"/>
    public static AcmeOrderStatus ParseAcmeOrderStatus(string name) => name switch
    {
        "pending" => AcmeOrderStatus.Pending,
        "ordered" => AcmeOrderStatus.Ordered,
        "finalizing" => AcmeOrderStatus.Finalizing,
        "valid" => AcmeOrderStatus.Valid,
        "failed" => AcmeOrderStatus.Failed,
        _ => throw NotAName<AcmeOrderStatus>(name),
    };

    /// <inheritdoc cref="ParseActorKind"/>
    public static AcmeChallengeType ParseAcmeChallengeType(string name) => name switch
    {
        "http-01" => AcmeChallengeType.Http01,
        "dns-01" => AcmeChallengeType.Dns01,
        _ => throw NotAName<AcmeChallengeType>(name),
    };

    /// <inheritdoc cref="ParseActorKind"/>
    public static DnsProviderKind ParseDnsProviderKind(string name) => name switch
    {
        "cloudflare" => DnsProviderKind.Cloudflare,
        "desec" => DnsProviderKind.DeSec,
        _ => throw NotAName<DnsProviderKind>(name),
    };

    private static FormatException NotAName<T>(string? name)
        where T : struct, Enum =>
        new($"'{name}' is not a {typeof(T).Name} name.");

    private static ArgumentOutOfRangeException Undefined<T>(T value)
        where T : struct, Enum =>
        new(nameof(value), value, $"Undefined {typeof(T).Name}.");
}
