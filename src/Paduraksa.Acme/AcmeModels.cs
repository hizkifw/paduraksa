using System.Text.Json.Serialization;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;

namespace Paduraksa.Acme;

/// <summary>The status values of accounts, orders, authorizations and challenges (RFC 8555 §7.1.6). An unrecognised value reads as <see cref="Unknown"/>.</summary>
[JsonConverter(typeof(AcmeStatusConverter))]
public enum AcmeStatus
{
    Unknown = 0,
    Pending = 1,
    Ready = 2,
    Processing = 3,
    Valid = 4,
    Invalid = 5,
    Deactivated = 6,
    Expired = 7,
    Revoked = 8,
}

/// <summary>An ACME directory (RFC 8555 §7.1.1), plus the <c>renewalInfo</c> URL of RFC 9773.</summary>
public sealed record AcmeDirectory(
    Uri NewNonce,
    Uri NewAccount,
    Uri NewOrder,
    Uri? NewAuthz,
    Uri RevokeCert,
    Uri? KeyChange,
    Uri? RenewalInfo,
    AcmeDirectoryMeta? Meta);

/// <summary>
/// The directory's <c>meta</c> object: the terms to agree to, whether <c>newAccount</c> needs an external account binding
/// (RFC 8555 §7.3.4), and the profiles the CA offers (ACME profiles draft; name to description, <see langword="null"/> if none).
/// </summary>
public sealed record AcmeDirectoryMeta(
    Uri? TermsOfService,
    Uri? Website,
    IReadOnlyList<string>? CaaIdentities,
    bool ExternalAccountRequired,
    IReadOnlyDictionary<string, string>? Profiles);

/// <summary>
/// An account, as the CA reports it. <see cref="Ref"/> is what every later call needs. <c>Created</c> is
/// <see langword="true"/> if <c>newAccount</c> made it, <see langword="false"/> if it already existed or was only read.
/// </summary>
public sealed record AcmeAccount(
    AcmeAccountId Id,
    Uri Url,
    AcmeStatus Status,
    IReadOnlyList<string> Contacts,
    Uri? OrdersUrl,
    bool Created)
{
    public AcmeAccountRef Ref => new(Id, Url);
}

/// <summary>An account as later requests address it: the vault key's ID and the account URL, which is the JWS <c>kid</c> (RFC 8555 §6.2).</summary>
public readonly record struct AcmeAccountRef(AcmeAccountId Id, Uri Url);

/// <summary>An external account binding (RFC 8555 §7.3.4): the CA's key identifier, and the vault secret holding its MAC key.</summary>
/// <param name="KeyId">The key identifier the CA issued (sent as the inner JWS's <c>kid</c>).</param>
/// <param name="MacKeySecret">The <see cref="SecretKind.EabHmacKey"/> secret with the CA's HMAC key.</param>
public sealed record AcmeExternalAccountBinding(string KeyId, SecretId MacKeySecret);

/// <summary>Parameters of <c>newAccount</c>.</summary>
/// <param name="Contacts">Contact URIs, e.g. <c>mailto:admin@example.com</c>.</param>
/// <param name="TermsOfServiceAgreed">Whether the operator agreed to <see cref="AcmeDirectoryMeta.TermsOfService"/>.</param>
/// <param name="ExternalAccount">The binding, if the CA needs one.</param>
public sealed record AcmeNewAccountRequest(
    IReadOnlyList<string> Contacts,
    bool TermsOfServiceAgreed,
    AcmeExternalAccountBinding? ExternalAccount = null);

/// <summary>Parameters of <c>newOrder</c>.</summary>
/// <param name="Identifiers">What the certificate is for.</param>
/// <param name="Profile">An ACME profile name from <see cref="AcmeDirectoryMeta.Profiles"/>, or <see langword="null"/> for the CA's default.</param>
/// <param name="Replaces">The ARI certificate ID of the certificate this order renews (<see cref="AcmeRenewalInfoId"/>), or <see langword="null"/>.</param>
public sealed record AcmeNewOrderRequest(
    IReadOnlyList<AcmeIdentifier> Identifiers,
    string? Profile = null,
    string? Replaces = null);

/// <summary>
/// An order. <see cref="Url"/> is its <c>Location</c>; poll it with <c>GetOrderAsync</c>. <c>RetryAfter</c> is the CA's
/// <c>Retry-After</c> on the response this was read from (not capped), for a scheduler that polls on its own clock.
/// </summary>
public sealed record AcmeOrder(
    Uri Url,
    AcmeStatus Status,
    DateTimeOffset? Expires,
    IReadOnlyList<AcmeIdentifier> Identifiers,
    IReadOnlyList<Uri> Authorizations,
    Uri Finalize,
    Uri? Certificate,
    string? Profile,
    string? Replaces,
    AcmeProblem? Error,
    TimeSpan? RetryAfter = null);

/// <summary>An authorization (RFC 8555 §7.1.4) with its challenges. <c>RetryAfter</c> is as for <see cref="AcmeOrder"/>.</summary>
public sealed record AcmeAuthorization(
    Uri Url,
    AcmeIdentifier Identifier,
    AcmeStatus Status,
    DateTimeOffset? Expires,
    bool Wildcard,
    IReadOnlyList<AcmeChallenge> Challenges,
    TimeSpan? RetryAfter = null)
{
    /// <summary>The challenge of the given type (<see cref="AcmeChallenges.Http01"/>, <see cref="AcmeChallenges.Dns01"/>), or <see langword="null"/>.</summary>
    public AcmeChallenge? FindChallenge(string type) => Challenges.FirstOrDefault(c => c.Type == type);

    /// <summary>The first offered challenge among <paramref name="preferredTypes"/>, in that order of preference, or <see langword="null"/>.</summary>
    public AcmeChallenge? SelectChallenge(IEnumerable<string> preferredTypes) =>
        preferredTypes.Select(FindChallenge).FirstOrDefault(c => c is not null);
}

/// <summary>A challenge (RFC 8555 §8). <see cref="Token"/> is checked to be base64url, so it is safe in a URL path. <c>RetryAfter</c> is as for <see cref="AcmeOrder"/>.</summary>
public sealed record AcmeChallenge(
    string Type,
    Uri Url,
    AcmeStatus Status,
    string? Token,
    DateTimeOffset? Validated,
    AcmeProblem? Error,
    TimeSpan? RetryAfter = null);

/// <summary>A downloaded certificate chain.</summary>
/// <param name="PemChain">The leaf first, then the intermediates, as the CA sent it (concatenated PEM).</param>
/// <param name="Url">Where this chain came from.</param>
/// <param name="Alternates">The <c>Link: rel="alternate"</c> chains the CA offers for the same certificate (RFC 8555 §7.4.2), on the CA's origin.</param>
/// <param name="IssuerCommonNames">The issuer common name of every certificate in <paramref name="PemChain"/>, in order. The last is the chain's top: what <c>preferred_chain</c> selection matches.</param>
public sealed record AcmeCertificateDownload(string PemChain, Uri Url, IReadOnlyList<Uri> Alternates, IReadOnlyList<string> IssuerCommonNames);

/// <summary>An ARI renewal window (RFC 9773 §4.2).</summary>
/// <param name="WindowStart">The window in which to renew.</param>
/// <param name="WindowEnd">The end of the window.</param>
/// <param name="ExplanationUrl">A page explaining an unusual window (for example a mass revocation), to show the operator.</param>
/// <param name="RetryAfter">When to ask again, from the response's <c>Retry-After</c> (not capped).</param>
public sealed record AcmeRenewalInfo(
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    Uri? ExplanationUrl,
    TimeSpan? RetryAfter)
{
    /// <summary>A time inside the window: <paramref name="fraction"/> 0 is its start, 1 its end. RFC 9773 §4.2 wants a uniformly random pick, so a caller passes a random fraction.</summary>
    public DateTimeOffset PickTime(double fraction)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(fraction, 0d);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(fraction, 1d);
        return WindowStart + ((WindowEnd - WindowStart) * fraction);
    }
}

/// <summary>
/// Revocation reason codes (RFC 5280 §5.3.1). RFC 8555 §7.6 allows any of them; the subset listed here is the one CAs
/// accept under the CA/Browser Forum baseline requirements and Let's Encrypt's policy.
/// </summary>
public enum AcmeRevocationReason
{
    Unspecified = 0,
    KeyCompromise = 1,
    AffiliationChanged = 3,
    Superseded = 4,
    CessationOfOperation = 5,
    PrivilegeWithdrawn = 9,
}
