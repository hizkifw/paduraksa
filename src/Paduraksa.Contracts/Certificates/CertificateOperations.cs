using System.Text.Json.Serialization;

using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Json;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;

namespace Paduraksa.Contracts.Certificates;

/// <summary>
/// A new self-signed certificate. Public data only: the key is made in the vault and never appears here.
/// </summary>
/// <param name="Names">DNS names, <c>*.</c> wildcards and IP addresses; at least one. The first is the common name when it is a DNS name of at most 64 characters.</param>
/// <param name="KeySpec">The kind of key the vault generates, on the wire its stable name (<c>ecdsa-p256</c>, <c>ecdsa-p384</c>, <c>rsa-2048</c>, <c>rsa-3072</c>, <c>rsa-4096</c>).</param>
/// <param name="Validity">How long it is valid from now (at most 825 days); null for the default (one year).</param>
/// <param name="Label">A display name.</param>
public sealed record SelfSignedCertificateRequest(
    IReadOnlyList<string> Names,
    [property: JsonConverter(typeof(KeySpecNameConverter))] KeySpec KeySpec = KeySpec.EcdsaP256,
    TimeSpan? Validity = null,
    string? Label = null);

/// <summary>
/// Creates the local CA root, if there is none. There is one local CA: asking again returns the existing root.
/// </summary>
/// <param name="KeySpec">The kind of key the vault generates for the root.</param>
/// <param name="Validity">How long the root is valid from now; null for the default (ten years).</param>
/// <param name="Label">A display name.</param>
public sealed record LocalCaRequest([property: JsonConverter(typeof(KeySpecNameConverter))] KeySpec KeySpec = KeySpec.EcdsaP256, TimeSpan? Validity = null, string? Label = null);

/// <summary>A new leaf certificate signed by the local CA.</summary>
/// <param name="Names">DNS names, <c>*.</c> wildcards and IP addresses; at least one.</param>
/// <param name="KeySpec">The kind of key the vault generates.</param>
/// <param name="Validity">How long it is valid from now (the vault caps it at the root's remaining validity); null for the default (90 days).</param>
/// <param name="Label">A display name.</param>
public sealed record LocalCaLeafRequest(
    IReadOnlyList<string> Names,
    [property: JsonConverter(typeof(KeySpecNameConverter))] KeySpec KeySpec = KeySpec.EcdsaP256,
    TimeSpan? Validity = null,
    string? Label = null);

/// <summary>
/// Re-issues a self-signed or local-CA certificate under the same <see cref="CertificateId"/> and a new key: its names
/// stay, and Specific bindings survive.
/// </summary>
/// <param name="KeySpec">The new key's kind; null keeps the current kind.</param>
/// <param name="Validity">How long the new certificate is valid from now; null for the default of its source.</param>
public sealed record ReissueRequest([property: JsonConverter(typeof(NullableKeySpecNameConverter))] KeySpec? KeySpec = null, TimeSpan? Validity = null);

/// <summary>What a certificate operation (<c>CertificateOperations</c>) is, for authorization and audit.</summary>
[JsonConverter(typeof(ConfigEnumConverter<CertificateOperation>))]
public enum CertificateOperation
{
    /// <summary>Upload of a PEM or PFX with its key.</summary>
    Upload,

    /// <summary>A self-signed certificate.</summary>
    IssueSelfSigned,

    /// <summary>The local CA root.</summary>
    CreateLocalCa,

    /// <summary>A leaf signed by the local CA.</summary>
    IssueFromLocalCa,

    /// <summary>A new key and chain under an existing ID.</summary>
    Reissue,

    /// <summary>Removal of a certificate and its key.</summary>
    Delete,
}

/// <summary>How a certificate operation ended.</summary>
[JsonConverter(typeof(ConfigEnumConverter<CertificateOperationStatus>))]
public enum CertificateOperationStatus
{
    /// <summary>Done: the vault holds the key and chain (or no longer holds them, for a delete) and the inventory says so.</summary>
    Completed,

    /// <summary>Nothing changed because it is so already (the local CA exists).</summary>
    Unchanged,

    /// <summary>The certificate doesn't exist.</summary>
    NotFound,

    /// <summary>Refused, with the reasons in <see cref="CertificateOperationResult.Errors"/>: bad input, a rejected upload, a
    /// certificate a Specific binding still uses, an operation its source doesn't allow. Nothing was changed.</summary>
    Invalid,

    /// <summary>The caller may not do this.</summary>
    Forbidden,

    /// <summary>The config store is unavailable (a degraded start); nothing was changed.</summary>
    Unavailable,
}

/// <summary>The outcome of a certificate operation.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="Certificate">The certificate after the operation; null for a delete, or when it didn't complete.</param>
/// <param name="Errors">Why it was refused. For a certificate still bound by a host, the paths of those hosts.</param>
/// <param name="Publish">How publishing the recompiled config went, when the operation affected serving.</param>
/// <param name="Warnings">Things that went wrong after the operation was already in effect and that an operator should know:
/// the vault key of a deleted certificate that could not be removed, for example.</param>
public sealed record CertificateOperationResult(
    CertificateOperationStatus Status,
    CertificateSummary? Certificate = null,
    IReadOnlyList<ConfigError>? Errors = null,
    PublishOutcome? Publish = null,
    IReadOnlyList<ConfigError>? Warnings = null)
{
    public IReadOnlyList<ConfigError> Errors { get; init; } = Errors ?? [];

    public IReadOnlyList<ConfigError> Warnings { get; init; } = Warnings ?? [];
}
