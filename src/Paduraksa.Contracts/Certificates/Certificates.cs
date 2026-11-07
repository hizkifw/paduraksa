using System.Text.Json.Serialization;

using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;

namespace Paduraksa.Contracts.Certificates;

/// <summary>
/// Where a certificate stands against the clock (PLAN §9.2). Derived from <c>NotBefore</c>/<c>NotAfter</c> at the time of
/// the query, never stored.
/// </summary>
[JsonConverter(typeof(ConfigEnumConverter<CertificateValidity>))]
public enum CertificateValidity
{
    /// <summary>Its validity period hasn't begun.</summary>
    NotYetValid,

    /// <summary>Inside its validity period.</summary>
    Valid,

    /// <summary>Past <c>NotAfter</c>.</summary>
    Expired,
}

/// <summary>
/// What the vault (or the import flow) reports about a certificate it created, replaced or imported, to be recorded in
/// the inventory (<c>CertificateService.RegisterAsync</c>). Registering an ID that exists replaces its metadata:
/// renewals and re-issues keep their <see cref="Id"/> (PLAN §9.2). Public data only.
/// </summary>
/// <param name="Id">The certificate's ID, as chosen by the caller and used for the vault's key.</param>
/// <param name="Source">Where it came from.</param>
/// <param name="Info">The public facts, as <see cref="ICertificateIssuance"/> and <see cref="ICertificateImport"/> return them.</param>
/// <param name="Status">Normally <see cref="CertificateStatus.Active"/>: the vault holds a key and a chain.</param>
/// <param name="Label">A display name; null keeps the existing one on a replacement.</param>
/// <param name="Notes">Free text; null keeps the existing notes on a replacement.</param>
public sealed record CertificateRegistration(
    CertificateId Id,
    CertificateSource Source,
    CertificateInfo Info,
    CertificateStatus Status = CertificateStatus.Active,
    string? Label = null,
    string? Notes = null);

/// <summary>A certificate as listed: everything except the chain.</summary>
/// <param name="Id">The certificate.</param>
/// <param name="Source">Where it came from.</param>
/// <param name="Status">The stored state.</param>
/// <param name="StatusMessage">Why it failed, for <see cref="CertificateStatus.Failed"/>.</param>
/// <param name="Validity">Against the clock when the summary was made.</param>
/// <param name="Names">The names it covers (DNS names, <c>*.parent</c> wildcards, IP addresses), normalised.</param>
/// <param name="NotBefore">Start of validity.</param>
/// <param name="NotAfter">End of validity.</param>
/// <param name="Subject">The subject DN, or empty.</param>
/// <param name="Issuer">The issuer DN.</param>
/// <param name="SerialNumber">Hex, as <see cref="CertificateInfo.SerialNumber"/>.</param>
/// <param name="AuthorityKeyIdentifier">Hex, or null.</param>
/// <param name="Sha256Fingerprint">Of the leaf, lowercase hex.</param>
/// <param name="KeyAlgorithm">A display label, e.g. <c>ECDSA P-256</c>.</param>
/// <param name="Label">A display name, or null.</param>
/// <param name="Notes">Free text, or null.</param>
/// <param name="CreatedAt">When the ID was first registered.</param>
/// <param name="UpdatedAt">When the metadata last changed.</param>
public sealed record CertificateSummary(
    CertificateId Id,
    CertificateSource Source,
    CertificateStatus Status,
    string? StatusMessage,
    CertificateValidity Validity,
    IReadOnlyList<string> Names,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    string Subject,
    string Issuer,
    string SerialNumber,
    string? AuthorityKeyIdentifier,
    string Sha256Fingerprint,
    string KeyAlgorithm,
    string? Label,
    string? Notes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>A certificate with its public chain, for the detail view and the chain download.</summary>
/// <param name="Summary">The listing fields.</param>
/// <param name="ChainIssuers">The issuer DN of every certificate in the chain, in order.</param>
/// <param name="PublicChainPem">The leaf and its intermediates as PEM. Public.</param>
public sealed record CertificateDetail(CertificateSummary Summary, IReadOnlyList<string> ChainIssuers, string PublicChainPem);

/// <summary>Filters for listing certificates; every filter that is set must match.</summary>
/// <param name="Source">Only this source.</param>
/// <param name="Status">Only this stored state.</param>
/// <param name="CoversName">Only certificates covering this host name: it as an exact name, or a wildcard of its parent.</param>
public sealed record CertificateQuery(CertificateSource? Source = null, CertificateStatus? Status = null, string? CoversName = null);

/// <summary>How a certificate change ended.</summary>
[JsonConverter(typeof(ConfigEnumConverter<CertificateChangeStatus>))]
public enum CertificateChangeStatus
{
    /// <summary>The change is recorded (and audited). When it affects serving, the new inventory was compiled into the
    /// latest revision and published: <see cref="CertificateChangeResult.Publish"/> says how that went.</summary>
    Committed,

    /// <summary>The certificate is recorded exactly so already; nothing changed.</summary>
    Unchanged,

    /// <summary>The certificate doesn't exist.</summary>
    NotFound,

    /// <summary>The change is refused (<see cref="CertificateChangeResult.Errors"/>); nothing was changed. Deleting a
    /// certificate a Specific binding uses is refused this way, with the paths of the hosts that use it.</summary>
    Invalid,

    /// <summary>The config store is unavailable (a degraded start); nothing was read or changed.</summary>
    Unavailable,
}

/// <summary>The outcome of a certificate change (register, status change, delete).</summary>
/// <param name="Status">How it ended.</param>
/// <param name="Certificate">The certificate after the change; null for a delete, not found or invalid.</param>
/// <param name="Errors">Why it was refused. Paths point into the latest revision's document (<c>/hosts/3</c>) for a
/// certificate that is still bound; empty otherwise.</param>
/// <param name="Publish">How publishing the recompiled latest revision went; null when the change doesn't affect serving
/// or nothing could be published (the latest revision doesn't compile; logged).</param>
public sealed record CertificateChangeResult(
    CertificateChangeStatus Status,
    CertificateSummary? Certificate = null,
    IReadOnlyList<ConfigError>? Errors = null,
    PublishOutcome? Publish = null)
{
    public IReadOnlyList<ConfigError> Errors { get; init; } = Errors ?? [];
}
