using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;

namespace Paduraksa.Contracts.Issuance;

/// <summary>Registers an ACME account over the API. The external-account-binding key goes in here and is stored in the vault: it is never returned.</summary>
/// <param name="DirectoryUrl">The CA's directory URL (https).</param>
/// <param name="TermsOfServiceAgreed">The operator agreed to the CA's terms; most CAs refuse an account without it.</param>
/// <param name="Contacts">Contact URIs, e.g. <c>mailto:admin@example.com</c>; may be empty.</param>
/// <param name="Algorithm">The account key's algorithm; the vault generates the key.</param>
/// <param name="EabKid">The CA's binding key identifier, for CAs that require one; needs <paramref name="EabHmacKey"/>.</param>
/// <param name="EabHmacKey">The binding's HMAC key as the CA shows it (base64url); needs <paramref name="EabKid"/>.</param>
/// <param name="Label">A display name.</param>
public sealed record CreateAcmeAccountBody(
    string DirectoryUrl,
    bool TermsOfServiceAgreed,
    IReadOnlyList<string>? Contacts = null,
    JwsAlgorithm Algorithm = JwsAlgorithm.ES256,
    string? EabKid = null,
    string? EabHmacKey = null,
    string? Label = null);

/// <summary>Adds a DNS provider over the API. The credential goes in here and is stored in the vault: it is never returned.</summary>
/// <param name="Kind">Which service.</param>
/// <param name="Label">A display name (required).</param>
/// <param name="Credential">The provider's API token.</param>
public sealed record CreateDnsProviderBody(DnsProviderKind Kind, string Label, string Credential);

/// <summary>Replaces a DNS provider's credential, keeping the provider (and everything that uses it).</summary>
/// <param name="Credential">The new API token.</param>
public sealed record RotateDnsCredentialBody(string Credential);
