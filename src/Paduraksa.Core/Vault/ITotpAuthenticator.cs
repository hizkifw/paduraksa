using Paduraksa.Core.Model;

namespace Paduraksa.Core.Vault;

/// <summary>What starting a TOTP enrolment returns.</summary>
/// <param name="SecretId">The vault's reference to the new secret (kept in the user's record; the value is never returned).</param>
/// <param name="ProvisioningUri">
/// The <c>otpauth://</c> URI for the authenticator's QR code. It carries the shared secret, and it is the single deliberate
/// place a TOTP secret leaves the vault: once, to the person's own authenticator. Show it, do not store or log it.
/// </param>
public sealed record TotpEnrollment(SecretId SecretId, Uri ProvisioningUri);

/// <summary>
/// Two-factor codes (RFC 6238) for admin users. The vault creates the shared secret, keeps it sealed, and
/// verifies codes itself: HMAC is banned outside the vault, and a secret that a caller could read could be leaked. There is no
/// way to read a secret back after enrolment.
/// </summary>
/// <remarks>
/// <para>SHA-1, 6 digits, 30-second steps, one step of clock drift either way (the parameters every authenticator app
/// supports). <b>Replay protection:</b> a time step that has been accepted for a secret is never accepted again for it, so a
/// code observed over a shoulder or in a log is useless as soon as it has been used (and so is every earlier step).</para>
/// <para>A secret is <i>pending</i> until <see cref="ConfirmEnrollmentAsync"/> accepts a code from the person's authenticator,
/// and only then <i>confirmed</i>; <see cref="VerifyAsync"/> accepts codes for confirmed secrets only. Codes are exactly six
/// ASCII digits; anything else is simply wrong (no exception). An unknown ID is wrong too, so a caller cannot tell a missing
/// secret from a bad code.</para>
/// </remarks>
public interface ITotpAuthenticator
{
    /// <summary>
    /// Generates a 160-bit secret, stores it sealed (kind <see cref="SecretKind.TotpSecret"/>, pending) and returns its ID with the
    /// provisioning URI. <paramref name="accountName"/> and <paramref name="issuer"/> are the labels the authenticator shows.
    /// </summary>
    ValueTask<TotpEnrollment> BeginEnrollmentAsync(string accountName, string issuer, CancellationToken cancellationToken);

    /// <summary>Confirms a pending secret with a code from the authenticator. Returns false for a wrong or replayed code, a secret that is already confirmed, or an unknown ID.</summary>
    ValueTask<bool> ConfirmEnrollmentAsync(SecretId id, string code, CancellationToken cancellationToken);

    /// <summary>Checks a code against a confirmed secret and, if it is right and not a replay, consumes its time step.</summary>
    ValueTask<bool> VerifyAsync(SecretId id, string code, CancellationToken cancellationToken);

    /// <summary>Deletes a TOTP secret, pending or confirmed. Returns false if there was none (or it is not a TOTP secret).</summary>
    ValueTask<bool> DeleteAsync(SecretId id, CancellationToken cancellationToken);
}
