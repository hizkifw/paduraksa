using Paduraksa.Core.Ids;

namespace Paduraksa.Core.Model;

// Typed IDs of the certificate and vault resources. They appear in admin UIs, APIs and audit
// entries, never in untrusted-client-facing places, and none is high-volume, so all are the default UuidV7: stored
// as a 16-byte BLOB, creation-ordered, and not Int64Ordered (which is predictable and meant for append-only tables).

/// <summary>
/// A secret in the vault: a DNS API token, an EAB HMAC key, a TOTP secret. Other resources hold this
/// reference, never the value.
/// </summary>
[TypedId("sec")]
public readonly partial record struct SecretId;

/// <summary>An ACME account. Its account key lives in the vault under this ID.</summary>
[TypedId("acct")]
public readonly partial record struct AcmeAccountId;

/// <summary>A configured DNS provider for DNS-01 challenges. Its credentials are a <see cref="SecretId"/>.</summary>
[TypedId("dnsp")]
public readonly partial record struct DnsProviderId;

/// <summary>An ACME order, i.e. one issuance or renewal attempt for a certificate.</summary>
[TypedId("ord")]
public readonly partial record struct AcmeOrderId;

/// <summary>
/// A key the vault staged for a certificate's next chain (<c>ICertificateIssuance.CreateKeyAndCsrAsync</c>).
/// Returned with the CSR and presented again when the chain is attached, so a second staging cannot silently take the
/// first one's place. A reference, not a secret.
/// </summary>
[TypedId("stk")]
public readonly partial record struct StagedKeyId;

/// <summary>
/// One element of the ASP.NET Core Data Protection key ring in the vault (<see cref="Core.Vault.IDataProtectionKeyStore"/>).
/// A reference only: the element itself is sealed, and only the admin app's <c>IXmlRepository</c> adapter reads it.
/// </summary>
[TypedId("dpk")]
public readonly partial record struct DataProtectionKeyId;
