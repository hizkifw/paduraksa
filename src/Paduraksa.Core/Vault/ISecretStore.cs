using Paduraksa.Core.Model;

namespace Paduraksa.Core.Vault;

/// <summary>
/// What a secret is for. Decides which capabilities may use it. Persisted by stable name (<see cref="VaultNames"/>),
/// never by number.
/// </summary>
public enum SecretKind
{
    /// <summary>
    /// A DNS provider API token or password, used through <see cref="ISecretHttpHandlerFactory"/>. Must be stored with
    /// <see cref="SecretOptions.AllowedHosts"/>.
    /// </summary>
    DnsApiToken = 0,

    /// <summary>The HMAC key of ACME external account binding (ZeroSSL, Google Trust Services), used through <see cref="IMacKeySource"/>.</summary>
    EabHmacKey = 1,

    /// <summary>
    /// A user's TOTP shared secret (PLAN §10.1). Because there is no get, the vault generates it: the vault creates the
    /// secret, returns only a one-time provisioning URI (<c>otpauth://</c>, for the QR code) at enrolment and verifies codes
    /// itself through <see cref="ITotpAuthenticator"/>. The enrolment URI is the single, deliberate place a TOTP secret
    /// leaves the vault, once, to the user's own authenticator.
    /// </summary>
    TotpSecret = 2,

    /// <summary>An RFC 2136 TSIG key, used through <see cref="IMacKeySource"/>.</summary>
    TsigKey = 3,

    /// <summary>A general HMAC key for tokens the proxy signs and verifies (forward-auth, PoW), used through <see cref="IMacKeySource"/>.</summary>
    HmacSigningKey = 4,
}

/// <summary>
/// Metadata the caller sets when it stores a secret (and may change when it replaces the value). It is not secret; it
/// tells the vault how the secret may be used.
/// </summary>
/// <remarks>Equality is by reference for <see cref="AllowedHosts"/>, as for any list member of a record.</remarks>
/// <param name="Label">A human label (e.g. <c>Cloudflare token for example.com</c>); never derive it from the value.</param>
/// <param name="MacAlgorithm">
/// Required for the MAC kinds (<see cref="SecretKind.EabHmacKey"/>, <see cref="SecretKind.TsigKey"/>,
/// <see cref="SecretKind.HmacSigningKey"/>), refused for the others. It is fixed when the secret is first stored: a
/// replacement cannot change it.
/// </param>
/// <param name="AllowedHosts">
/// Required (non-empty) for <see cref="SecretKind.DnsApiToken"/>, refused for the others: the hosts the token may ever be
/// sent to. The admin sets them when creating the DNS provider (Cloudflare's API host, the user's acme-dns server), they are
/// normalised with <see cref="VaultHostNames.NormalizeAuthority"/> (a host, optionally with the port it may be reached on; 443 otherwise), and the authenticating handler uses their intersection with the
/// caller's own list (<see cref="SecretInjection.AllowedHosts"/>), so provider code can narrow the destinations but never widen them.
/// </param>
public sealed record SecretOptions(string? Label, MacAlgorithm? MacAlgorithm, IReadOnlyList<string>? AllowedHosts);

/// <summary>What the vault will tell about a secret: everything except the value.</summary>
/// <remarks>Equality is by reference for <see cref="AllowedHosts"/>, as for any list member of a record.</remarks>
/// <param name="Id">The secret.</param>
/// <param name="Kind">What it is for.</param>
/// <param name="Label">The human label, if any.</param>
/// <param name="MacAlgorithm">The MAC algorithm of a MAC secret, otherwise <see langword="null"/>.</param>
/// <param name="AllowedHosts">The normalised hosts a <see cref="SecretKind.DnsApiToken"/> may be sent to; empty for other kinds.</param>
/// <param name="CreatedAt">When it was first stored.</param>
/// <param name="UpdatedAt">When its value was last replaced; equal to <paramref name="CreatedAt"/> if never.</param>
/// <param name="Version">1 for the first value, +1 on each replacement. Part of the AAD that binds the ciphertext (PLAN §8.3).</param>
public sealed record SecretMetadata(
    SecretId Id,
    SecretKind Kind,
    string? Label,
    MacAlgorithm? MacAlgorithm,
    IReadOnlyList<string> AllowedHosts,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Version);

/// <summary>
/// Stores secrets (PLAN §8.2). <b>There is no get.</b> Secrets go in, capabilities come out: a DNS token is used through
/// <see cref="ISecretHttpHandlerFactory"/>, an HMAC key through <see cref="IMacKeySource"/>. Other resources keep only the
/// <see cref="SecretId"/>.
/// </summary>
/// <remarks>
/// <para>
/// A value passes through the caller's memory exactly once, on the way in: the person pasted it into a form or an API
/// body. Callers pass it as <see cref="ReadOnlyMemory{T}"/> and clear their buffer afterwards (best effort); the vault copies it into its
/// own pinned buffer, encrypts it and zeroes that.
/// </para>
/// <para>
/// Failures throw <see cref="KeyNotFoundException"/> for an unknown ID and <see cref="ArgumentException"/> when the
/// <see cref="SecretOptions"/> do not fit the kind (see its members).
/// </para>
/// </remarks>
public interface ISecretStore
{
    /// <summary>Stores a new secret and returns its ID.</summary>
    ValueTask<SecretId> PutAsync(SecretKind kind, ReadOnlyMemory<byte> value, SecretOptions options, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the value of an existing secret (rotation), keeping its ID so every reference stays valid. Bumps the
    /// version. <paramref name="options"/> replaces the label and allowed hosts when given (<see langword="null"/> keeps
    /// them); its <see cref="SecretOptions.MacAlgorithm"/> must be <see langword="null"/> or the stored one.
    /// </summary>
    ValueTask<SecretMetadata> ReplaceAsync(SecretId id, ReadOnlyMemory<byte> value, SecretOptions? options, CancellationToken cancellationToken);

    /// <summary>Deletes a secret. Returns false if there was none. The caller has checked that nothing still references it.</summary>
    ValueTask<bool> DeleteAsync(SecretId id, CancellationToken cancellationToken);

    /// <summary>The metadata of a secret, or <see langword="null"/> if there is none. Async because it reads the vault database.</summary>
    ValueTask<SecretMetadata?> DescribeAsync(SecretId id, CancellationToken cancellationToken);
}
