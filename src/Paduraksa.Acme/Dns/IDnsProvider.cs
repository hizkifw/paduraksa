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
/// Propagation (waiting until the CA's resolvers can see the record) is not this interface's concern: see <see cref="IDnsPropagationCheck"/>.
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
    /// Adds several TXT records. The orchestrator publishes a whole order's records in one call, so a provider whose API limits
    /// writes (deSEC) can send them together. The default adds them one by one.
    /// </summary>
    /// <exception cref="DnsProviderException">The provider refused or could not be reached.</exception>
    async ValueTask CreateTxtBatchAsync(IReadOnlyList<DnsChallengeRecord> records, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        foreach (DnsChallengeRecord record in records)
        {
            await CreateTxtAsync(record.Name, record.Value, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Removes several TXT records. The default removes them one by one, and does not stop at a failing one: the first failure
    /// is thrown after the others were tried (a stale record is clutter, and the rest should still go).
    /// </summary>
    /// <exception cref="DnsProviderException">The provider refused or could not be reached.</exception>
    async ValueTask DeleteTxtBatchAsync(IReadOnlyList<DnsChallengeRecord> records, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        DnsProviderException? first = null;
        foreach (DnsChallengeRecord record in records)
        {
            try
            {
                await DeleteTxtAsync(record.Name, record.Value, cancellationToken).ConfigureAwait(false);
            }
            catch (DnsProviderException ex)
            {
                first ??= ex;
            }
        }

        if (first is not null)
        {
            throw first;
        }
    }

    /// <summary>
    /// Checks that the credential works (the "Test credentials" button): a read-only call such as listing zones.
    /// A bad credential is a result, not an exception.
    /// </summary>
    ValueTask<DnsCredentialCheck> ValidateCredentialsAsync(CancellationToken cancellationToken);
}

/// <summary>A TXT record to publish or remove: its fully qualified name without a trailing dot and its value.</summary>
public sealed record DnsChallengeRecord(string Name, string Value);

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

    /// <summary>Whether the provider is throttling this account (HTTP 429) rather than failing; the schedule treats it like a CA rate limit.</summary>
    public bool IsRateLimited { get; init; }

    /// <summary>How long the provider asked to wait before the next call (a rate limit's <c>Retry-After</c>); null when it did not say.</summary>
    public TimeSpan? RetryAfter { get; init; }
}
