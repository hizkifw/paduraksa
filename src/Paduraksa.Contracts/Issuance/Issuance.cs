using System.Text.Json.Serialization;

using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;

namespace Paduraksa.Contracts.Issuance;

/// <summary>How a change to an ACME account or DNS provider ended.</summary>
[JsonConverter(typeof(ConfigEnumConverter<IssuanceChangeStatus>))]
public enum IssuanceChangeStatus
{
    /// <summary>The change is recorded and audited.</summary>
    Committed,

    /// <summary>The account or provider doesn't exist.</summary>
    NotFound,

    /// <summary>The request is refused (<c>Errors</c>); nothing changed.</summary>
    Invalid,

    /// <summary>The CA refused or could not be reached (see the message); nothing was recorded, and a key created for the attempt was deleted.</summary>
    Failed,

    /// <summary>The config store is unavailable (a degraded start); nothing was read or changed.</summary>
    Unavailable,

    /// <summary>The origin lacks the permission (<c>providers:write</c>); nothing changed. Audited.</summary>
    Denied,
}

/// <summary>Registers an account with a CA (<c>AcmeAccountService.CreateAsync</c>).</summary>
/// <param name="DirectoryUrl">The CA's directory URL (https).</param>
/// <param name="Contacts">Contact URIs, e.g. <c>mailto:admin@example.com</c>; may be empty.</param>
/// <param name="TermsOfServiceAgreed">The operator agreed to the CA's terms; most CAs refuse an account without it.</param>
/// <param name="Algorithm">The account key's algorithm; the vault generates the key.</param>
/// <param name="EabKid">The CA's external-account-binding key identifier, for CAs that require one (ZeroSSL, Google Trust Services).</param>
/// <param name="EabSecretId">The vault secret holding the binding's HMAC key (<c>SecretKind.EabHmacKey</c>); required together with <paramref name="EabKid"/>.</param>
/// <param name="Label">A display name.</param>
public sealed record CreateAcmeAccountRequest(
    string DirectoryUrl,
    IReadOnlyList<string> Contacts,
    bool TermsOfServiceAgreed,
    JwsAlgorithm Algorithm = JwsAlgorithm.ES256,
    string? EabKid = null,
    SecretId? EabSecretId = null,
    string? Label = null);

/// <summary>An ACME account as listed. No key material: the key lives in the vault under <paramref name="Id"/>.</summary>
public sealed record AcmeAccountSummary(
    AcmeAccountId Id,
    string DirectoryUrl,
    IReadOnlyList<string> Contacts,
    AcmeAccountStatus Status,
    string AccountUrl,
    JwsAlgorithm KeyAlgorithm,
    string? EabKid,
    SecretId? EabSecretId,
    bool TermsOfServiceAgreed,
    string? Label,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>The outcome of an account change.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="Account">The account after the change (for a deactivation, as it stands); null when refused, not found or failed.</param>
/// <param name="Errors">Why it was refused.</param>
/// <param name="Message">What the CA said, for <see cref="IssuanceChangeStatus.Failed"/> (sanitised, no secrets).</param>
public sealed record AcmeAccountResult(
    IssuanceChangeStatus Status,
    AcmeAccountSummary? Account = null,
    IReadOnlyList<ConfigError>? Errors = null,
    string? Message = null)
{
    public IReadOnlyList<ConfigError> Errors { get; init; } = Errors ?? [];
}

/// <summary>Adds a DNS provider (<c>DnsProviderService.CreateAsync</c>).</summary>
/// <param name="Kind">Which service.</param>
/// <param name="Label">A display name (required).</param>
/// <param name="CredentialSecretId">The vault secret holding the credential (<c>SecretKind.DnsApiToken</c>, stored with the hosts it may be sent to).</param>
public sealed record CreateDnsProviderRequest(DnsProviderKind Kind, string Label, SecretId CredentialSecretId);

/// <summary>A DNS provider as listed. No credential: only the vault secret's ID.</summary>
public sealed record DnsProviderSummary(
    DnsProviderId Id,
    DnsProviderKind Kind,
    string Label,
    SecretId CredentialSecretId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>The outcome of a DNS provider change.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="Provider">The provider after the change; null for a delete, a refusal or not found.</param>
/// <param name="Errors">Why it was refused (a provider that certificates still use can't be deleted).</param>
public sealed record DnsProviderResult(
    IssuanceChangeStatus Status,
    DnsProviderSummary? Provider = null,
    IReadOnlyList<ConfigError>? Errors = null)
{
    public IReadOnlyList<ConfigError> Errors { get; init; } = Errors ?? [];
}

/// <summary>The answer of "Test credentials".</summary>
/// <param name="Found">The provider exists.</param>
/// <param name="Valid">The provider accepted the credential.</param>
/// <param name="Message">Why not, for an operator.</param>
public sealed record DnsProviderTestResult(bool Found, bool Valid, string? Message = null);

/// <summary>Asks for a certificate from an ACME CA (<c>AcmeIssuanceService.RequestCertificateAsync</c>).</summary>
/// <param name="Names">DNS names (wildcards as <c>*.parent</c>) and IP addresses; at least one. HTTP-01 cannot prove a wildcard, DNS-01 cannot prove an IP address.</param>
/// <param name="AccountId">The ACME account to order with (active).</param>
/// <param name="ChallengeType">How the names are proven.</param>
/// <param name="KeySpec">The kind of key the vault generates for the certificate.</param>
/// <param name="DnsProviderId">The DNS provider for DNS-01; refused for HTTP-01.</param>
/// <param name="Label">A display name for the certificate.</param>
/// <param name="Profile">An ACME profile of the CA (for example <c>shortlived</c>); null for its default.</param>
/// <param name="PreferredChain">The issuer common name the chain should end in; null for the CA's default chain.</param>
public sealed record CertificateIssueRequest(
    IReadOnlyList<string> Names,
    AcmeAccountId AccountId,
    AcmeChallengeType ChallengeType = AcmeChallengeType.Http01,
    KeySpec KeySpec = KeySpec.EcdsaP256,
    DnsProviderId? DnsProviderId = null,
    string? Label = null,
    string? Profile = null,
    string? PreferredChain = null);

/// <summary>How an issuance, renewal or resume ended.</summary>
[JsonConverter(typeof(ConfigEnumConverter<IssuanceOutcome>))]
public enum IssuanceOutcome
{
    /// <summary>The chain is attached and the certificate is registered and serving.</summary>
    Issued,

    /// <summary>The attempt failed (<see cref="IssuanceResult.Message"/>); the scheduler retries.</summary>
    Failed,

    /// <summary>Another issuance of this certificate is running in this process; nothing was started.</summary>
    InProgress,

    /// <summary>The request is refused (<see cref="IssuanceResult.Errors"/>); nothing was started.</summary>
    Invalid,

    /// <summary>The certificate (or its order) doesn't exist.</summary>
    NotFound,

    /// <summary>The config store is unavailable (a degraded start); nothing was started.</summary>
    Unavailable,

    /// <summary>The origin lacks <c>certificates:write</c>; nothing was started (no order, no key, no pending certificate). Audited.</summary>
    Forbidden,
}

/// <summary>The outcome of an issuance, renewal or resume.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="CertificateId">The certificate (allocated by a request); null when refused before one existed.</param>
/// <param name="Certificate">The certificate's metadata after the attempt; null when there is none.</param>
/// <param name="Message">Why it failed (sanitised, no secrets).</param>
/// <param name="Errors">Why the request was refused.</param>
public sealed record IssuanceResult(
    IssuanceOutcome Outcome,
    CertificateId? CertificateId = null,
    CertificateSummary? Certificate = null,
    string? Message = null,
    IReadOnlyList<ConfigError>? Errors = null)
{
    public IReadOnlyList<ConfigError> Errors { get; init; } = Errors ?? [];
}

/// <summary>The ACME state of one certificate: what the last attempt did and when the scheduler acts next.</summary>
public sealed record AcmeOrderSummary(
    CertificateId CertificateId,
    AcmeOrderId OrderId,
    AcmeAccountId AccountId,
    AcmeOrderStatus Status,
    AcmeChallengeType ChallengeType,
    DnsProviderId? DnsProviderId,
    KeySpec KeySpec,
    IReadOnlyList<string> Names,
    string? Profile,
    string? PreferredChain,
    string? Error,
    int Attempts,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? RetryAt,
    DateTimeOffset? NextRenewalAt,
    DateTimeOffset? AriWindowStart,
    DateTimeOffset? AriWindowEnd,
    string? AriExplanationUrl,
    DateTimeOffset? AriCheckAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
