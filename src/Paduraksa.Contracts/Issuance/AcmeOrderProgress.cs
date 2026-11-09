using System.Text.Json.Serialization;

using Paduraksa.Contracts.Config;
using Paduraksa.Core.Json;
using Paduraksa.Core.Model;

namespace Paduraksa.Contracts.Issuance;

/// <summary>
/// Where a running ACME issuance stands (PLAN §9.3), finer than the stored order status: the steps a person watching the dashboard sees
/// go by. In order; a run that resumes an order may start past the first steps, and a run whose authorizations are already valid skips
/// from <see cref="OrderCreated"/> to <see cref="Validated"/>.
/// </summary>
[JsonConverter(typeof(ConfigEnumConverter<AcmeProgressStage>))]
public enum AcmeProgressStage
{
    /// <summary>The run has begun: the certificate is registered as pending and the CA is about to be asked.</summary>
    Starting,

    /// <summary>The CA has the order (a new one, or the stored one it still accepts).</summary>
    OrderCreated,

    /// <summary>Reading the order's authorizations and the challenges they offer.</summary>
    Authorizing,

    /// <summary>The answers are up: HTTP-01 tokens in the gate's store, or DNS-01 TXT records at the provider.</summary>
    ChallengePublished,

    /// <summary>DNS-01: waiting until the zone's authoritative name servers return the TXT records (or the fixed delay).</summary>
    PropagationCheck,

    /// <summary>The CA was told to validate and its verdict is awaited.</summary>
    Validating,

    /// <summary>The CA accepted every name.</summary>
    Validated,

    /// <summary>The vault made the key and the CSR, and the CA is finalizing the order and signing the certificate.</summary>
    Finalizing,

    /// <summary>The chain is attached in the vault and the certificate is registered and serving.</summary>
    Issued,

    /// <summary>The attempt failed (<see cref="AcmeOrderProgress.Detail"/>); the scheduler retries at <see cref="AcmeOrderProgress.RetryAt"/>.</summary>
    Failed,
}

/// <summary>
/// One step of a running issuance, as <c>AcmeIssuanceService</c> reports it to the caller that started it and keeps it for anyone who
/// asks while the run lasts. Public facts only: names, counts and the CA's sanitised message, never a token, key authorization or key.
/// </summary>
/// <param name="CertificateId">The certificate being issued or renewed (known from the first report on, so a caller that requested a new
/// certificate learns its ID before the run ends).</param>
/// <param name="Stage">The step just reached.</param>
/// <param name="ChallengeType">How the names are proven.</param>
/// <param name="At">When the step was reached.</param>
/// <param name="Detail">A short line for an operator: how many answers were published, or the sanitised failure message.</param>
/// <param name="RetryAt">For <see cref="AcmeProgressStage.Failed"/>: when the scheduler tries again.</param>
public sealed record AcmeOrderProgress(
    CertificateId CertificateId,
    AcmeProgressStage Stage,
    AcmeChallengeType ChallengeType,
    DateTimeOffset At,
    string? Detail = null,
    DateTimeOffset? RetryAt = null);
