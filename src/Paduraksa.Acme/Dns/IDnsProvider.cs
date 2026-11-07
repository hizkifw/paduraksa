using Paduraksa.Core.Model;

namespace Paduraksa.Acme.Dns;

/// <summary>
/// The DNS side of DNS-01 (PLAN §9): publishes and removes the TXT record a challenge asks for. A provider holds a
/// credential only as a vault secret reference and sends it through the vault's authenticating handler, so no
/// implementation sees the token itself. Implementations arrive per service (Cloudflare, deSEC, ...); the issuance
/// orchestrator only knows this seam.
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotent.</b> The orchestrator may repeat a call after a restart, so <see cref="CreateTxtAsync"/> with a
/// name and value that already exist succeeds without adding a duplicate, and <see cref="DeleteTxtAsync"/> of a record
/// that is not there succeeds. A wildcard and its base name both validate at <c>_acme-challenge.example.com</c>, so
/// several records with different values may exist at one name; delete removes only the one with the given value.
/// </para>
/// <para>
/// Failures throw <see cref="DnsProviderException"/>, which says whether trying again later can help.
/// Propagation (waiting until the CA's resolvers can see the record) is not this interface's concern yet; the orchestrator waits a configured delay.
/// </para>
/// </remarks>
public interface IDnsProvider
{
    /// <summary>
    /// Adds a TXT record. <paramref name="recordName"/> is the fully qualified name without a trailing dot
    /// (<c>_acme-challenge.example.com</c>); the provider finds the zone it belongs to.
    /// </summary>
    /// <exception cref="DnsProviderException">The provider refused or could not be reached.</exception>
    ValueTask CreateTxtAsync(string recordName, string value, CancellationToken cancellationToken);

    /// <summary>Removes the TXT record with exactly this name and value, if there is one.</summary>
    /// <exception cref="DnsProviderException">The provider refused or could not be reached.</exception>
    ValueTask DeleteTxtAsync(string recordName, string value, CancellationToken cancellationToken);

    /// <summary>
    /// Checks that the credential works (the "Test credentials" button): a read-only call such as listing zones.
    /// A bad credential is a result, not an exception.
    /// </summary>
    ValueTask<DnsCredentialCheck> ValidateCredentialsAsync(CancellationToken cancellationToken);
}

/// <summary>The outcome of <see cref="IDnsProvider.ValidateCredentialsAsync"/>.</summary>
/// <param name="Valid">Whether the provider accepted the credential.</param>
/// <param name="Message">Why not, for an operator (provider text, no secrets); null when valid.</param>
public sealed record DnsCredentialCheck(bool Valid, string? Message = null);

/// <summary>Builds the provider for a configured DNS provider row.</summary>
public interface IDnsProviderFactory
{
    /// <summary>The provider for <paramref name="kind"/>, authenticated with the vault secret <paramref name="credential"/>.</summary>
    /// <exception cref="NotSupportedException">No implementation for this kind is available.</exception>
    IDnsProvider Create(DnsProviderKind kind, SecretId credential);
}

/// <summary>A DNS provider call failed.</summary>
/// <param name="message">What went wrong, for an operator; no secrets.</param>
/// <param name="isTransient">Whether trying again later can help (the provider or the network had trouble) rather than the request being refused.</param>
/// <param name="inner">The underlying failure.</param>
public sealed class DnsProviderException(string message, bool isTransient, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>Whether trying again later can help.</summary>
    public bool IsTransient { get; } = isTransient;
}
