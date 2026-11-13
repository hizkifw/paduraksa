# Vault, certificates and ACME

The vault is the only place key material lives, and it exposes capabilities (sign, decrypt-and-use, select a certificate), never bytes. Certificates and ACME sit on top of it: an inventory that the snapshot compiler reads, an ACME client whose account key never leaves the vault, DNS-01 providers that never see their own token, and a local CA. This page covers the vault's surface and storage, at-rest encryption, backup and restore, the certificate inventory, the local CA, ACME issuance and renewal, and DNS-01.

Contents:
- [What the vault holds](#what-the-vault-holds)
- [The public surface](#the-public-surface)
- [Certificate storage in the vault](#certificate-storage-in-the-vault-m3)
- [The Data Protection key ring](#the-data-protection-key-ring-m5)
- [Password credentials](#password-credentials-m7)
- [The DNS handler factory](#the-dns-handler-factory)
- [At-rest encryption](#at-rest-encryption)
- [Backup and restore](#backup-and-restore-m10)
- [Certificates and ACME](#certificates-and-acme)
- [The certificate inventory](#the-certificate-inventory-m3)
- [Local CA name constraints and root rotation](#local-ca-name-constraints-and-root-rotation)
- [ACME issuance and renewal](#acme-issuance-and-renewal-m3)
- [DNS-01 providers and the propagation check](#dns-01-providers-and-the-propagation-check-m3)

## What the vault holds

TLS private keys (server certs **and** upstream mTLS client certs), ACME account keys, EAB HMAC keys, DNS provider credentials, TOTP secrets, the internal CA key, the Data Protection key ring (the dashboard's antiforgery and circuit state), the proof-of-work HMAC key (forward-auth needed none), and access-list users' password verifiers (PBKDF2, one credential per user).

The vault lives in `Paduraksa.Vault`, has its own database (`vault.db`) and its own `DbContext`, and everything in it is `internal`. Other projects see only the capability interfaces in `Paduraksa.Core/Vault`: secrets go in, capabilities come out, and nothing returns key bytes.

## The public surface

The contracts are in `src/Paduraksa.Core/Vault`. This sketch includes the refinements made when they were written.

```csharp
interface IServerCertificateSource   // the ONE sanctioned egress for serving: Kestrel needs the key in-process
{   ValueTask<SslStreamCertificateContext?> LoadForServingAsync(CertificateId id, CancellationToken ct); }

interface IClientCertificateSource   // second sanctioned egress: upstream mTLS, for SslClientAuthenticationOptions.ClientCertificateContext
{   ValueTask<SslStreamCertificateContext?> LoadForUpstreamAsync(CertificateId id, CancellationToken ct); }

interface ICertificateIssuance       // keys are generated inside, only CSR / public data leave
{   ValueTask<CsrResult> CreateKeyAndCsrAsync(CertificateId id, KeySpec spec, IReadOnlyList<string> names, CancellationToken ct);   // CsrResult carries a StagedKeyId
    ValueTask<CertificateInfo> AttachChainAsync(CertificateId id, StagedKeyId stagedKey, ReadOnlyMemory<byte> pemChain, CancellationToken ct);
    ValueTask<CertificateInfo> CreateSelfSignedAsync(CertificateId id, KeySpec spec, IReadOnlyList<string> names, TimeSpan validity, CancellationToken ct);
    ValueTask<CertificateInfo> EnsureLocalCaAsync(CertificateId id, KeySpec spec, TimeSpan validity, CancellationToken ct);   // the root is a normal certificate
    ValueTask<CertificateInfo> IssueFromLocalCaAsync(CertificateId id, KeySpec spec, IReadOnlyList<string> names, TimeSpan validity, CancellationToken ct);
    ValueTask<bool> DeleteAsync(CertificateId id, CancellationToken ct); }   // a deleted certificate takes its key with it

interface ICertificateImport         // uploads stream straight in; returns public info only
{   ValueTask<CertificateInfo> ImportAsync(CertificateId id, Stream pemOrPfx, ReadOnlyMemory<char> password, CancellationToken ct); }

interface ICertificateCatalog        // the vault describes what it holds: public facts per ID, no key opened (after a DB loss, an ACME crash window)
{   ValueTask<IReadOnlyList<VaultCertificate>> ListAsync(CancellationToken ct);                  // VaultCertificate = (CertificateId Id, bool IsLocalCaRoot, CertificateInfo Info)
    ValueTask<VaultCertificate?> DescribeAsync(CertificateId id, CancellationToken ct); }

interface IJwsSigner                 // ACME: Acme builds the JWS, the vault only computes the signature over it
{   ValueTask<AcmeAccountKeyInfo> CreateAccountKeyAsync(AcmeAccountId id, JwsAlgorithm alg, CancellationToken ct);   // public JWK + RFC 7638 thumbprint
    ValueTask<AcmeAccountKeyInfo?> GetAccountKeyAsync(AcmeAccountId id, CancellationToken ct);
    ValueTask<JwsFlattened> SignAsync(AcmeAccountId id, ReadOnlyMemory<byte> protectedHeader, ReadOnlyMemory<byte> payload, CancellationToken ct);
    ValueTask<bool> DeleteAccountKeyAsync(AcmeAccountId id, CancellationToken ct); }

interface IMacKey { MacAlgorithm Algorithm { get; } int TagLength { get; } int Sign(ReadOnlySpan<byte> data, Span<byte> tag); bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> tag); }
interface IMacKeySource { ValueTask<IMacKey?> OpenAsync(SecretId id, SecretKind expected, CancellationToken ct); }   // refused on a kind mismatch

interface ISecretStore               // no Get(). Secrets go in, capabilities come out.
{   ValueTask<SecretId> PutAsync(SecretKind kind, ReadOnlyMemory<byte> value, SecretOptions options, CancellationToken ct);   // label, MAC algorithm, allowed hosts
    ValueTask<SecretMetadata> ReplaceAsync(SecretId id, ReadOnlyMemory<byte> value, SecretOptions? options, CancellationToken ct);
    ValueTask<bool> DeleteAsync(SecretId id, CancellationToken ct);
    ValueTask<SecretMetadata?> DescribeAsync(SecretId id, CancellationToken ct); }

interface ISecretHttpHandlerFactory  // DNS provider creds: the vault returns the whole transport; the credential never reaches the caller
{   HttpMessageHandler CreateAuthenticatingHandler(SecretId id, SecretInjection how, SecretHttpTransportOptions transport); }

interface IDataProtectionKeyStore    // the admin app's Data Protection ring: the element XML is the third sanctioned egress
{   ValueTask<IReadOnlyList<DataProtectionKey>> ReadAllAsync(CancellationToken ct);
    ValueTask StoreAsync(string friendlyName, string keyXml, CancellationToken ct); }

interface IPasswordCredential        // a handle on one user's password: it can be verified against and never read; it outlives a password change
{   PasswordCredentialId Id { get; }   bool IsSet { get; } }
interface IPasswordCredentials       // set, import, delete: no read of any kind
{   IPasswordCredential Open(PasswordCredentialId id);                                                       // sync, from memory
    ValueTask<PasswordChange> SetAsync(PasswordCredentialId id, ReadOnlyMemory<char> password, CancellationToken ct);
    ValueTask RevertAsync(IReadOnlyList<PasswordChange> changes, CancellationToken ct);   // puts each previous sealed row back exactly (or no row), last first
    void Keep(IReadOnlyList<PasswordChange> changes);                                      // the commit succeeded: forget what it took to undo
    ValueTask<IPasswordCredential> PrepareAsync(PasswordCredentialId id, ReadOnlyMemory<char> password, CancellationToken ct);   // a handle for a draft: nothing stored or cached
    IPasswordCredential PrepareImport(PasswordCredentialId id, string passwordVerifier);
    ValueTask<bool> ImportAsync(PasswordCredentialId id, string passwordVerifier, CancellationToken ct);   // an existing PBKDF2 string, as it is
    ValueTask<bool> DeleteAsync(PasswordCredentialId id, CancellationToken ct);
    ValueTask<IReadOnlyList<PasswordCredentialId>> ListAsync(CancellationToken ct); }
interface IPasswordVerifier          // the gate's check: a hit or miss without a lock or an allocation, and the slow PBKDF2 the caller schedules
{   PasswordCacheProbe TryVerifyCached(IPasswordCredential c, ReadOnlySpan<byte> username, ReadOnlySpan<byte> password, ReadOnlySpan<byte> client);
    PasswordVerdict Verify(IPasswordCredential? c, ReadOnlySpan<byte> username, ReadOnlySpan<byte> password, ReadOnlySpan<byte> client, bool remember); }
```

### Notes on the refinements

**Serving flow.**
- The snapshot compiler is synchronous. So Application preloads the serving contexts (`LoadForServingAsync`) for the certificate inventory before each compile, and gives the compiler an immutable `IServerCertificateResolver` over them. The proxy never sees the source.
- The vault returns the same context instance for an unchanged certificate (the proxy deduplicates by instance). The vault never disposes an instance it handed out; a replaced one is dropped and collected.
- The context's `TargetCertificate` carries the key, so the guard against exporting it is the key-material ban list (`BannedSymbols.KeyMaterial.txt`), not the type.

**Caller-chosen IDs, atomic replacement, staged keys.**
- Every way of replacing a certificate (`AttachChainAsync`, import, self-signed, local-CA re-issue) keeps its `CertificateId` and swaps key and chain in one transaction. Specific bindings therefore survive renewals.
- `CreateKeyAndCsrAsync` stages a key and returns a `StagedKeyId` that `AttachChainAsync` must present. A stale token throws `StagedKeyMismatchException`.

**Import.**
- PEM must carry a PKCS#8 key (plain or encrypted). Otherwise PFX under the vault's own `Pkcs12LoaderLimits`. At most 1 MiB is read.
- Every failure surfaces as `CertificateImportException` with a reason.
- The leaf is refused if it is an RSA key under 2048 bits (`WeakKey`) or a CA certificate (`CaCertificate`). An expired leaf is accepted: the inventory treats it as unusable until renewed.
- "Usable name" is decided by `ICertificateNameRules`, implemented by the inventory's own normaliser (`CertificateNameRules`) and handed to `AddPaduraksaVault`. The vault therefore never accepts names the inventory drops. A vault built without one uses its strict rule.
- The KDF cost of an encrypted PKCS#8 is bounded before it runs (PBKDF2 or PKCS#5/12 iteration count at most 600000, else `Unreadable`). It runs once per key type the certificates could have (normally once).
- Everything found wrong while reading the certificate's extensions after parsing is `Unreadable`, never an exception.

**Certificate metadata.** `CertificateInfo` has:
- the SHA-256 fingerprint;
- the serial number as the DER INTEGER content bytes (leading `00` kept);
- the Authority Key Identifier (with the serial it is the RFC 9773 ARI certificate ID);
- the issuers of the chain (for `preferred_chain` selection).

The subject has a CN only for a DNS name of at most 64 characters, otherwise it is empty.

**JWS.** Acme writes the protected header and payload. The vault base64url-encodes, signs (ES256 and ES384 as fixed-width `R||S`, RS256 as PKCS#1 v1.5) and returns the flattened-JWS pieces. It refuses a header whose `alg` is not the key's. EAB's inner JWS is built by Acme and signed with an `IMacKey` (HS256). Account key rollover is deferred (see [ACME](#acme)).

**MAC keys.**
- The algorithm is fixed in the secret's metadata when it is stored, and `OpenAsync` checks the expected kind.
- A handle resolves the current version on every call. `Verify` also accepts the previous version for a grace window after a rotation. `Sign` never does.
- `Verify` takes only whole tags. `VerifyTruncated` takes a prefix of 16 to full-length bytes (the proof-of-work token keeps the first 16 bytes of HMAC-SHA-256 on purpose). It is a separate method so a protocol whose tag length an attacker controls can never be checked against one byte.

**Secrets carry their destinations.** `SecretOptions.AllowedHosts` (required for `DnsApiToken`) is set by the admin when creating the DNS provider and stored with the secret. The authenticating handler uses its intersection with the provider code's own `SecretInjection.AllowedHosts` (normalised host names, compared with `Uri.IdnHost`).

**Async lookups, nullable misses.** `Describe` and the public-key lookups are async (they read the vault database), and a missing item returns `null`.

### TOTP (M4, `ITotpAuthenticator`)

There is no get on the vault, so TOTP needs vault-side generation. The vault creates the secret, returns only the one-time provisioning URI for the QR code, and verifies codes itself.

- `BeginEnrollmentAsync` generates 160 random bits, seals them as a `TotpSecret`, and returns the secret's ID and the `otpauth://` URI (a `Uri`: the one deliberate egress, once).
- `ConfirmEnrollmentAsync` turns a pending secret into a confirmed one with a code.
- `VerifyAsync` accepts codes for confirmed secrets only.
- `DeleteAsync` removes it.
- RFC 6238 with SHA-1, 6 digits, 30 s steps, one step of drift either way, every step compared in constant time.
- **Replay protection** is a `TotpStates.LastStep` column advanced by one conditional update. A time step (and every earlier one) is accepted once per secret, across restarts, processes and simultaneous requests (vault migration `Totp`).
- No call returns the secret after enrolment, and the architecture test holds the contract to plain data.
- Recovery codes are not the vault's: ten 16-character codes (about 79 bits), shown once, stored as SHA-256 in the config DB, single use by one atomic update.

## Certificate storage in the vault (M3)

- **Rows.** `CertificateKeys` holds one row per `CertificateId`:
  - the PKCS#8 key sealed by the envelope cipher (AAD purpose `certificate-key`, or `certificate-key/local-ca` for the root, so a leaf row edited into the root fails to open);
  - the public chain PEM (leaf first, intermediates, no root unless the certificate is its own root);
  - a version that is both in the AAD and a concurrency token;
  - a unique-where-true `IsLocalCaRoot`.
- A replacement is an update of that row in one `SaveChanges`, which is what makes key and chain swap atomically.
- `StagedKeys` holds at most one row per certificate: the key sealed to its own `StagedKeyId`, plus the requested names. Staging again updates the row in place, so the earlier token no longer names it.
- Every replacement route drops the ID's staged key in the same transaction. `DeleteAsync` takes both rows. KEK rotation re-wraps both tables (counted as `CertificateKeys` and `StagedKeys`).
- `CertificateInfo` is recomputed from the stored chain. No metadata is duplicated.

**Chains.** `AttachChainAsync`:
- takes the first certificate as the leaf;
- requires its public key to be the staged key's;
- requires its SANs to cover every requested name (exact, or the wildcard of the parent; a wildcard does not cover the apex or a deeper name);
- requires it to be valid now (not expired, not starting later than a five-minute tolerance);
- orders the rest behind it;
- throws `CryptographicException` otherwise (the staged key keeps waiting). No staged row for the token is `StagedKeyMismatchException`, including after a replacement or delete.

A certificate counts as the issuer of the one before only if the names link up, the key identifiers agree where both carry them, the issuer is a CA, and its key verifies the signature (ECDSA or RSA PKCS#1 v1.5 with SHA-2). Strays, same-name impostors, intermediates expired now and the root are dropped. Imports assemble the chain the same way.

**Vault-signed certificates.**
- Extensions: SAN (critical when the subject is empty), serverAuth EKU, key usage (`digitalSignature`, plus `keyEncipherment` for RSA), SKI, AKI (the root's SKI for a leaf), CA:false.
- Serials are 126 random bits (the top two bits of 128 are fixed so the integer is positive and needs no sign byte). Validity starts five minutes early.
- **The root** has CA:true with path length 0, `keyCertSign` and `cRLSign`, and the name `Paduraksa Local CA (<8 hex>)`. It has no name constraints. Name constraints (limiting it to the user's own domains) are deferred.
- A leaf's validity is capped at the root's remaining validity and, for local-CA leaves and self-signed certificates, silently at 825 days (what Apple's trust store accepts). The root is not capped.
- The root is never served (`LoadForServingAsync` of its ID is `null`), cannot be replaced or staged over, and `EnsureLocalCaAsync` returns the existing one regardless of the ID it is given.

**Contexts.** `CertificateContexts` implements both egresses over one cache of `SslStreamCertificateContext` (offline chain) per certificate.
- A load on a miss takes the vault's gate, and writers invalidate inside it. A context built from a replaced row can therefore never be cached afterwards. The vault never disposes what it handed out.
- The cache is per process. A second process writing the same database would not invalidate it. The proxy's vault is the only writer, and the KEK-rotating CLI does not change certificates.
- **Seals check the key.** Every operation that seals (certificate keys, staged keys, secrets, account keys) begins a transaction and re-reads the database's KEK check value first, refusing when it is not this process's KEK. A `rotate-kek` run while the service serves therefore cannot make it seal under the old key (a rotation committing after the check makes the write fail).
- **Staged keys** are sealed to `{certificateId}/{stagedKeyId}` and a digest of their names. A staging deletes staged keys older than 30 days (abandoned orders).
- A start whose generated `master.key` does not match the database names `master.key.old` or `master.key.new` when that one does (an interrupted rotation).
- `EnsureLocalCaAsync` for an ID other than the root's throws `LocalCaExistsException` carrying the real ID and info.

**Import.**
- The upload is read into one pinned buffer (at most 1 MiB, stops reading at the cap).
- PEM key blocks are decoded from bytes, never through a string. PFX is read under `CertificateImport.Limits` (32 certificates, one key, 600k iteration limits).
- Wrong password versus unreadable is told by the outer structure (ENCRYPTED PRIVATE KEY or PFX outer sequence parses: wrong password; otherwise unreadable), because the Pkcs12 and PKCS#8 info types are not in the shared framework.
- Contract addition: `CertificateImportFailure.NoUsableName`. A leaf with no DNS or IP SAN is refused before anything is stored, since nothing could ever select it.

## The Data Protection key ring (M5)

`IDataProtectionKeyStore` (`Paduraksa.Core/Vault`) backs the admin app's `Microsoft.AspNetCore.DataProtection.Repositories.IXmlRepository`.
- `ReadAllAsync` returns every element. `StoreAsync` seals one element under its friendly name. That is the whole surface. The optional `IDeletableXmlRepository` is not implemented, so a retired key stays in the ring until it expires.
- Each element is a row in the vault's own `DataProtectionKeys` table, sealed by the envelope cipher under the purpose `data-protection-key\n<friendly name>`, with its typed `DataProtectionKeyId` and version in the AAD, inside the KEK-check sealing transaction. Editing the name or moving the ciphertext fails to open it.
- `vault rotate-kek` re-wraps the table (counted as `DataProtectionKeys`). A restore keeps the ring because it is inside `vault.db`.
- A row that fails to open is logged at Error and skipped, so one damaged element does not take the ring down.

**The element XML is key material**, so `DataProtectionKey.KeyXml` is the **third sanctioned egress** (after Kestrel's and the upstream mTLS contexts). The admin's `VaultXmlRepository` is its only reader, and it hands each element straight back to Data Protection in-process. `DataProtectionKey`'s printing hides it.

Other decisions:
- No `IXmlEncryptor` is registered. The vault already encrypts at rest, so a second layer would only wrap ciphertext in ciphertext and need a key ring of its own to protect this one.
- No file-system repository is created (`KeyManagementOptions.XmlRepository` is set to the vault's). The framework's "No XML encryptor configured" warning is filtered from the log.
- **The ring is admin only.** The dashboard's antiforgery and circuit state need it to survive a restart. The public proxy gets YARP's session-affinity key from an in-memory `EphemeralDataProtectionProvider` (the affinity cookie is a destination ID, not a secret) and never reads or writes the vault. Its Data Protection key-ring warm-up hosted service is removed so no key is ever created.
- The admin session cookie itself is Paduraksa's own random value, not Data Protection.
- The core host resolves no protector.
- The adapter and the `AddPaduraksaDataProtection` extension live in `Paduraksa.Admin.DataProtection`.

## Password credentials (M7)

An access-list user's password is a **credential** in the vault, named by a `pwc_` ID that the config document carries (`{ username, credentialId }`). No hash, no password and nothing that verifies one is in the document, a revision, an audit entry, an API response, the simulator's trace, a log or an exception.

**The hash.**
- `PasswordHash` is the PHC string `$pbkdf2-sha256$i=210000$salt$hash`: a 16-byte salt and a 32-byte hash. A cost outside 100 000 to 10 000 000, a non-canonical encoding or another shape is refused.
- It left Core. It is `internal` to the vault, with its parsed form, the cache, the registry and the store. An architecture test holds each of them out of every other assembly.
- The proxy's own `BannedSymbols.txt` also bans `Rfc2898DeriveBytes`, so the gate cannot hash a password either.

**Storage.** `PasswordCredentials` (vault migration `PasswordCredentials`) has one row per credential.
- The verifier is sealed by the envelope cipher: AAD purpose `password-credential`, the version in the AAD and a concurrency token, inside the KEK-check sealing transaction like every seal.
- It is re-wrapped by `vault rotate-kek` (counted as `PasswordCredentials`) and carried by a backup of `vault.db`.
- A row that does not open is logged at Error with its ID and skipped. Its user cannot sign in until a password is set again.

**Memory.**
- The verifiers are loaded once, on first use. `Open` is synchronous because the snapshot compiler is, so it never starts the vault itself. Called before `IVaultLifecycle.InitializeAsync` it throws an `InvalidOperationException` that names the fix, instead of blocking a thread on an asynchronous start. The host initializes the vault long before a compile.
- They are parsed into a slot per credential ID that is never removed. A handle held by a compiled snapshot therefore sees a later `SetAsync` or `DeleteAsync` without a recompile.
- `SetAsync` hashes outside the vault's gate (a fresh salt, 210 000 iterations), then seals and swaps the slot's state inside it.
- `ImportAsync` keeps an existing verifier as it is (no re-hash) and does nothing if the credential already has a password.

**The verifier.**
- `TryVerifyCached` is the old cache, moved with its per-process key and its refresh on hit (a hit pushes the expiry to five minutes out once less than four are left).
- The cache key is a keyed SHA-256 over: the key, the credential's **stamp** (a hash of its salt and verifier, so a password change, a delete or a re-import makes every entry of the old verifier miss with nothing to sweep), the masked client address (16 bytes, as the caller masks it), and the user name and password as sent. The name is in it because a credential can be renamed without changing its password.
- It builds the key on the stack, so a hit and a miss are 0 B (`PasswordCredentialStoreTests` measures both with `AllocAssert`).
- It returns the probe's two facts: hit, and an **attempt key**, a 64-bit value from that hash, 0 for a credential with no password. The proxy's line uses it to run identical attempts one after the other (see "Access lists" in [waf.md](waf.md)). It says nothing about what was sent.
- `Verify` is the PBKDF2 path. A null credential (a name nobody has) and a credential with no password pay the same PBKDF2 against a dummy verifier, so neither can be told from a wrong password by timing. The verdicts differ (`Rejected` and `NoPassword`) for the simulator's trace. With `remember` a success is cached, a failure never is, and a simulation passes false.

**Taking a change back.**
- Every `SetAsync` keeps, in memory until it is kept or reverted, what it replaced: the previous sealed row as it was (bytes, version, times) or the fact that there was none, and the slot's previous verifier. It returns a `PasswordChange` that names it.
- `RevertAsync` writes exactly that back inside the vault's gate and the KEK-check transaction (a master key that changed since is a failure, not a silent re-seal) and puts the slot back. `Keep` forgets it.
- The config pipeline sets the passwords after validation and compile and before the commit, and on any failure of the commit reverts them. A command whose commit failed leaves every password as it was.
- If the revert itself fails, the pipeline logs at Error and writes an audit event of its own (`config.access_list.password.restore_failed`, outcome Failed). It names the lists and users whose password may be in an unknown state, never a password, because the commit's own audit event was never written.
- `PrepareAsync` and `PrepareImport` return a handle that exists for the caller alone: not stored, not reachable by `Open`, never seen by the cache. It is what a dry run or a draft compiles against.

### Lifecycle (decided)

A credential lives **as long as the live config names it**. Or, as a *pending value*, as long as an open draft's entry will name it. A password typed into a drafted access list is set as a credential no config names yet. Its ID is kept by the draft and nothing else of it is stored. It is deleted with the entry, the draft or the draft's expiry. The startup sweep leaves it alone (see [dashboard.md](dashboard.md)).

- After a command commits, the credentials its revision stopped naming (a removed user, a deleted list, a rollback to a document without them) are deleted.
- At startup, when the live config is the database's latest revision, so are the vault's credentials nothing names. That is what a command that failed after setting a password left behind.
- Revisions are not consulted. Keeping a credential for as long as any revision names it would keep a removed user's password for as long as the history lasts, and a rollback would bring that user back with it.
- The user a rollback restores gets **no password** (or the one the vault holds then, if a credential with that ID still exists). The config compiles with a warning, `accessList.user.noPassword`, and that user cannot sign in until one is set.
- A start on the last-known-good file, or with no revision, sweeps nothing. It does not know what is stray, and the config database may come back.

**Backup and export.** The hashes are in `vault.db` now, not in the config document. A config export carries none, and a full backup (the vault's database, KEK passphrase-wrapped) is what carries the passwords. Restoring only the config gives users with no password (the warning says so).

**Gaps.**
- The stamp and the key are per process, so a restart starts with an empty cache (one PBKDF2 per active user, as before).
- A slot is a few words per credential ID ever opened and is never reclaimed.
- The vault database is not part of the config commit, so the pipeline takes a failed commit's passwords back (above) rather than rolling them back with it. A process killed between the set and the revert leaves them changed. The next start's sweep deletes the credentials of a user that never made it into a revision.

## The DNS handler factory

**Why the factory returns the whole transport.**
- A caller-supplied inner handler would see every request header, including the credential.
- A returned `DelegatingHandler` would let the caller swap its `InnerHandler` until the first send.
- A response's `RequestMessage` would carry the injected header back.

So the factory returns a sealed `internal` vault handler over a `SocketsHttpHandler` the vault builds itself (no automatic redirects, https only, timeouts, DNS refresh and proxy from `SecretHttpTransportOptions`). The handler adds the credential to a **clone** of the request and sets `response.RequestMessage` back to the caller's original.

On every request it refuses:
- non-https requests;
- hosts outside the allowlist;
- secrets that are not `DnsApiToken`.

It never follows a redirect: providers handle 3xx themselves, and a redirect to another host fails the host check, so it never carries the credential. Tests substitute the transport through an internal seam in Vault, never through Core.

This is the key trick. A DNS-provider client in `Paduraksa.Acme` builds an `HttpClient` over a vault-owned handler. The Cloudflare API token is added to the outgoing request *inside* the vault assembly and never exists as a value in Acme code. RFC 2136 TSIG gets the same treatment through `IMacKey`.

## At-rest encryption

- **Envelope encryption.** Each secret has its own random DEK (AES-256-GCM). DEKs are wrapped by the **KEK**. The AAD is `secretId || kind || version`, which stops anyone swapping ciphertexts between rows.
- **KEK source**, in priority order:
  1. `PADURAKSA_MASTER_KEY_FILE` (Docker secrets, systemd `LoadCredential`);
  2. the `PADURAKSA_MASTER_KEY` env var;
  3. an auto-generated `data/master.key` (0600).
- **A KEK beside the database.** The dashboard warns when the KEK sits in the same directory as `vault.db`.
  - Built (threat model G3): the overview's "things need you" list carries it ("The master key is stored beside the vault"), with why (a copy of the data directory is a copy of every key) and a link to install.md "The master key".
  - It comes from `VaultStatus.KekBesideDatabase`, for a user who holds `vault:manage`. That is the Owner: only the person who runs the server can move the key.
  - The shipped compose example uses the Docker secret by default, with the generation commands in its comments.
- **`paduraksa vault rotate-kek --new-key-file <path|->`** re-wraps the DEKs.
  - The new key is read from a file or stdin, never argv.
  - With the generated `master.key`, the command stores the new key itself: the old file is kept as `master.key.old`, the new one is written through `master.key.new`, all 0600 and flushed with the directory, and the old file is put back if the rotation fails. Standard input that is a terminal is refused, and a group-readable key file is warned about.
  - With a key file or the environment as the source, the new key must come from a file the operator keeps.
- Plaintext buffers are pinned and zeroed (`CryptographicOperations.ZeroMemory`) after use, best effort.
- **Export.** Config export excludes secrets (and so password hashes: they are in the vault). An explicit "full backup" produces an archive with `vault.db` encrypted under a user passphrase (Argon2id-derived), and it is audited (see [Backup and restore](#backup-and-restore-m10)).
- The Data Protection key ring is one more sealed table (`DataProtectionKeys`), so it follows every rule above: its DEKs are re-wrapped by `vault rotate-kek`, its rows are protected by `secure_delete` and the WAL checkpoint, and it is covered by a backup of `vault.db` without any extra step.

### As built (M3 vault core)

**Sealed value format 1** is one BLOB: format byte, KEK ID (8 bytes), wrap nonce, DEK wrapped with AES-256-GCM, data nonce, ciphertext, tag. AES-KW does not exist in .NET, and GCM lets the wrap carry the AAD.

**The AAD** of both operations is `domain || format || (KEK ID) || len(id) || id || len(purpose) || purpose || version`.
- The ID is the typed-ID string (`sec_...`, `acct_...`).
- The purpose carries what decides how the value may be used: a secret's kind, MAC algorithm and allowed hosts, an account key's JWS algorithm.
- Editing that metadata in `vault.db` therefore makes the value fail to open. A change of hosts is a replace, which seals again.

**The KEK.**
- It is base64 of 32 bytes from all three sources, and never becomes a string inside the vault when written.
- A source that is set but unusable is an error, never a fall-through.
- `master.key` is created exclusively: written whole to a private 0600 temp file, then given its name with an atomic hard link that cannot replace an existing file. It is never overwritten. A concurrent reader replays its open and waits for the whole key. A new one is not generated if `vault.db` already holds data.
- An existing generated `master.key` readable by other users is refused (group access warns), judged on the open handle. An explicit key file is only warned about, since Docker secrets are mounted 0444.
- `vault.db` stores an HMAC check value of the KEK, compared at startup (`IVaultLifecycle.InitializeAsync`, which also reports whether the KEK sits beside the database). It runs with `secure_delete`.

**MAC rotation.** The previous value of a MAC secret verifies for 10 minutes after a replacement, then is deleted. `Sign` and `Verify` use cached key material and never wait for a rotation.

**KEK rotation** (`IVaultKekRotation`) re-wraps every DEK in one transaction and checkpoints the write-ahead log, so the old wraps are overwritten.
- It re-wraps only. It does not re-key the data, so a copy of the old database plus the old KEK still opens that copy. To cut that off, replace the secrets.
- The order is the caller's: store the new KEK durably at its source, rotate, then retire the old one. The vault never stores the new KEK itself, and a crash with the new KEK stored nowhere after the commit would lose the vault.

**The authenticating handler** allows only port 443 unless an allowed-host entry names the port (`host:8443`, in both the secret's and the caller's list). It refuses a request whose `Host` header differs from its URL (domain fronting) and never copies it.

Vault tests live in `Paduraksa.Tests.Vault`, a key-material boundary project.

**Future hardening.** The capability interfaces are the seam for moving the vault into a separate process (gRPC over a Unix socket with peer credentials) without changing any callers.

## Backup and restore (M10)

**The owner's decision:** a backup contains **everything** (the config database, the vault database, the last-known-good file), encrypted under a passphrase. It restores on a fresh machine with that machine's own master key, and it is useless without the passphrase.

Code: `Paduraksa.Vault/Backup` (the archive and its crypto), the contracts `IVaultBackup` and `IStagedBackup` in `Paduraksa.Core/Vault`, `BackupService` (Application; the permission check, the audit), `POST /api/v1/backup`, Settings > Backup, and `paduraksa backup|restore` (`Server/Cli/BackupCommands`).

### The archive (format 1, `*.pdkbak`)

A plain 48-byte header, then everything else sealed.

**The header** is:
- magic `PDKBAK 1A 0A` (the `1A 0A` catch a text-mode transfer, as PNG's do);
- the format version, the key-derivation and cipher ids;
- the Argon2 memory (KiB), passes and lanes;
- a random 16-byte salt and the chunk size (64 KiB).

A parser refuses an unknown version or algorithm, non-zero reserved bytes, and costs outside 8 KiB x lanes to 1 GiB, 1 to 16 passes, 1 to 16 lanes. A header therefore cannot make a restore spend a gigabyte of memory twice.

**The sealed stream** is a **chunked AES-256-GCM stream** (the STREAM construction of age and Tink).
- 64 KiB plaintext chunks, each sealed with nonce = four zero bytes and the chunk index (64-bit big-endian).
- The whole header plus a last-chunk flag is the associated data. A 16-byte tag follows each chunk.
- Every chunk but the last is exactly full, and the last is shorter (possibly empty). A reader therefore knows the last one from its length and authenticates it as such.
- A cut at a chunk boundary leaves no last chunk (refused as truncated). A cut inside a chunk fails its tag. A moved, dropped or appended chunk fails its index or its flag.
- A tag that fails on the first chunk is reported as a wrong passphrase (or an altered header, which cannot be told apart). Anywhere later it is a damaged archive.

**The contents** are a **tar** (ustar) of fixed entries, in this order: `manifest.json`, `paduraksa.db`, `vault.db`, `last-good.json` (when the source had one), `vault-backup.key`. No other name is ever accepted and no name is ever used as a path.

**Why tar and not zip.**
- Both ends are forward-only streams. The writer produces the archive in constant memory while it encrypts, and the reader decrypts and unpacks in one pass, authenticating every chunk before any byte of it is believed.
- A zip needs to seek (its index is at the end), which means buffering the plaintext archive or the ciphertext in memory or in a second temporary file.
- Zip adds nothing here, since SQLite files do not compress usefully once sealed and the compression would be before encryption. `System.Formats.Tar` is in the framework.

**The manifest** (`manifest.json`) is the first entry, so it is read before any file. It holds:
- `format`;
- `paduraksa` (the build, as its informational version);
- `createdAt`;
- `databases.config` and `databases.vault` (the EF migration IDs applied to each snapshot, oldest first);
- `entries` (name, size, SHA-256 of each file, including `vault-backup.key`);
- `kdf` (algorithm, version, memory, passes, lanes; it repeats the header, and a disagreement is a damaged archive).

It is sealed with the rest, so it is readable only with the passphrase. The brief's "source instance name" is not recorded: Paduraksa has no such name, and the machine's host name would only be noise in a file that moves between machines.

### Key derivation

- Argon2id (RFC 9106), **64 MiB, three passes, four lanes** (RFC 9106's second recommended option). It is sized for a homelab box: about half a second on a small CPU, and 64 MiB freed afterwards.
- A random salt per archive. The 32-byte result is used directly as the AES key.
- The parameters are in the header, so a later build can raise them without breaking old archives. A restore accepts what the header says within the ranges above.
- **Library:** `Konscious.Security.Cryptography.Argon2` 1.3.1 (MIT). It is a managed implementation, so there is no native library per RID to ship in a multi-arch image or to find on a bare-metal box, which libsodium bindings would need. It is widely used, and the only maintained managed Argon2id with the associated-data and secret inputs.
- A test (`BackupKeyDerivationTests`) pins it to RFC 9106's own Argon2id vector. `BackupBoundaryTests` fails if any project but `Paduraksa.Vault` references it.
- The library's internal buffers, and the passphrase string a caller started from, are not ours to zero. The pinned copies Paduraksa makes are zeroed.
- The passphrase is used as its UTF-8 bytes exactly as typed. There is no Unicode normalization: the platform's can differ, and a rule that differs between the machine that made an archive and the one that opens it would lock people out.
- It must be 12 to 1024 characters (`BackupPassphrase`, one rule for the CLI, the API and the dashboard). No composition rules, as for passwords. The dashboard shows a hint by length alone.

### Decisions

**The vault's keys are re-wrapped under a backup-only key (option b).**
- At backup time the vault takes its snapshot of `vault.db`, then re-wraps every data-encryption key in the snapshot under a fresh random **backup key** (`KekRotation.RewrapAllAsync`, the one list of what holds a sealed value, shared with `vault rotate-kek`). It puts that key in the archive as `vault-backup.key`.
- So **the source's master key never enters the archive**, a restore never installs it, and an archive stays restorable after the source's master key is rotated or lost.
- On restore the DEKs are re-wrapped again under the target's own master key (`StagedBackup.WriteVaultDatabaseAsync`).
- Option (a), carrying the source's KEK wrapped by the passphrase, was rejected. It would put the one key that opens every copy of the source's `vault.db` into the archive.
- The cost of (b) is nothing the owner notices. The backup key is protected by the same passphrase as everything beside it, and it exists only so that the vault file inside is not wrapped under a key that exists nowhere else.
- The re-wrapped snapshot is compacted with `VACUUM INTO`, so the pages of the old wraps are not in it (and the vault runs with `secure_delete`).

**The config database is encrypted too.** It holds the users' password hashes, TOTP references, the audit log, sessions and client addresses. So the whole archive is sealed, not just the vault. Nothing is readable without the passphrase, the manifest included.

### Online and consistent

- `SqliteConnection.BackupDatabase` (SQLite's online backup API) reads each database in one step inside one read transaction. Each snapshot is therefore the state at one instant while the server keeps writing. A file copy of a live WAL database can miss pages that are only in the log.
- The vault snapshot is taken under the vault's gate (no KEK rotation or seal in between). The database's key check is compared with the key this process holds: a rotation run elsewhere is refused with a message saying to restart.
- The two databases are **not** one instant. The config is read first and the vault second, so every key a certificate row points at is already in the vault snapshot (issuance writes the key first).
- A certificate deleted in the few milliseconds between the two reads can come out as a row without its key. The startup certificate recovery already handles that: an inventory row whose key the vault lacks is retired and re-issued.
- Scratch files live in `<data dir>/run/backup-<random>/` (0700, files 0600) and are removed afterwards. Each directory holds an exclusive lock on a `.lock` marker for the backup's life. The sweep of leftovers (any process's, at the start of a backup) therefore removes only a directory nobody holds (or a markerless one over an hour old), and never a long-running backup's.
- One backup runs at a time per process. A second gets `Busy` (409).

### API

`POST /api/v1/backup` with `{"passphrase": "..."}` in the JSON body (over the admin's HTTPS, never a URL).
- It answers `application/octet-stream` with `Content-Disposition: attachment; filename=paduraksa-backup-<UTC stamp>.pdkbak`, `Cache-Control: no-store` and the length.
- The archive is complete in a private temporary file (0600, deleted when the response ends) before a byte is sent. A failure is therefore an ordinary problem and never a half download.
- Errors: 422 `backup.passphrase` (too short or too long), 409 `backup.busy`, 500 `backup.failed` (the log says why; the answer names nothing).
- There is **no restore endpoint**: a restore replaces the databases and needs the server stopped.

**Permission.** The new permission **`backup:create`** is held by the **Owner only** (`RolePermissions`: Owner has every permission; Admin, Operator, Viewer and Auditor do not).
- An API token can carry it as a scope, but only if its owner holds it. A token never exceeds its owner, and an Admin cannot mint one.
- The refusal of a caller without it is the endpoint filter's 403 (not audited, like every filter refusal). A refusal inside the service (the dashboard, the CLI's evaluator) is audited as `denied`.

**Audit.** `backup.create`: success with `{"bytes": N}` (the actor, the channel, the client address and the time are the event's own), `denied`, `invalid` (a weak passphrase) and `failed`. Never the passphrase: a test searches the audit log, every log line, the output of the CLI and the archive for it. The CLI audits under the `cli` actor.

### CLI

**`paduraksa backup --out <file>`**
- The passphrase comes from, in order: `--passphrase-file <path>` (`-` is standard input, refused when that is a terminal; a file other users can read is used and warned about), else `PADURAKSA_BACKUP_PASSPHRASE` (ignored, with a warning, beside a file), else a prompt without echo, **twice**. Never an argument: a test parses the command line and finds no option that takes it.
- It works while the server runs, and needs the master key the way the server does (the vault opens its database with it to re-wrap).
- It refuses an existing `--out` and a data directory with no config database, and creates the output's directory.
- It writes to a private `<out>.partial-<random>` that it flushes and renames, so a file at `--out` is always whole.

**`paduraksa restore <file>`**
1. It first claims the data directory (see [One server per data directory](#one-server-per-data-directory)) and refuses while a server holds it.
2. It reads the passphrase (once) and **verifies the whole archive before touching anything**: header, passphrase, every chunk's tag, the manifest, every hash, that both databases are sound SQLite files (`quick_check`), and that no migration in either is unknown to this build. **A newer schema is refused**, naming the migration (the vault's own list is this build's `VaultDbContext`, the config's is `ConfigDbContext`). An older one is accepted.
3. Only then it moves the current `paduraksa.db`, `vault.db`, their `-wal` and `-shm` files and `last-good.json` to `<data dir>/pre-restore-<UTC time>/` (0700). Nothing is deleted, and `master.key` stays.
4. It migrates the staged vault to this build's schema, and re-wraps it under this machine's master key (resolved as the server does). **A `master.key` is generated when there is none**, and the command says to keep a copy of it apart.
5. It installs the three files, and runs what a start checks: the vault opens under that key, and the config database opens through `ConfigDatabase.InitializeAsync` (which applies an older schema's migrations, reported) and has its revisions. The latest revision's number and hash are printed.

Any failure after the first move **undoes it all**: what was placed is removed, what was moved comes back, a `master.key` this run generated goes, and it says so. A rollback that cannot move something back names where it is. Staging is `<data dir>/run/restore-<random>/` (0700) and is removed at the end.

### One server per data directory

The existing mechanism was the internal listener's socket probe, which cannot see a server with `Listen:AdminInternal` off or on the loopback fallback. So the server now claims `<data dir>/run/server.lock` (`DataDirectoryLock`, `Paduraksa.Core/Hosting`).

- It is an exclusive file lock held for the process's life (`flock` on Unix, a share mode on Windows). The operating system releases it however the process ends: no stale lock, no pid to believe.
- `SharedServices` takes it before anything else uses the directory, and releases it with the last shared service.
- A second server on the directory now fails to start (exit 2, Critical log). `restore` holds the same claim for its whole run, so a server started meanwhile finds it taken.
- On a file system that cannot lock (some network mounts, where .NET ignores a failing `flock`) the claim is not enforced, as before. The lock detects that by finding that a second open of the file succeeds (`Enforced`). The server logs a Warning at startup (and `restore` prints one) so the operator knows.

### Container

- `docker exec paduraksa paduraksa backup --out /data/backups/x.pdkbak`. Add `-it` for the prompt, or give the passphrase as `PADURAKSA_BACKUP_PASSPHRASE` or a mounted file.
- With the service stopped: `docker compose run --rm paduraksa restore /data/backups/x.pdkbak`.
- `docs/install.md` ("Backups") has both and the homelab routine.

### Dashboard

Settings > Backup (`/settings/backup`, visible with `backup:create`, so the Owner's) has:
- a warning that only the passphrase protects the file and nothing recovers a lost one;
- the passphrase twice with a length hint (`PassphraseStrength`);
- **Download backup**, which hands the archive to the browser over the circuit (`DashboardBrowser.DownloadAsync`, as the audit export does). The file is deleted when the page is done with it, and the fields are emptied as the request goes out.

A second card names the restore command and says it needs the service stopped (restore stays CLI-only). `IDashboardBackup` is the seam bUnit fakes. The Audit page words the action as "Made a full backup".

### Boundary

The vault does all the cryptography: `AesGcm`, Argon2 and the backup key stay in `Paduraksa.Vault` (the banned-symbol rules and `BackupBoundaryTests` hold).

`IVaultBackup` crosses the contract test's deny-by-default rule at exactly two allowed positions, each with its reason in `VaultContractTests`:
- `CreateAsync(destination)`: ciphertext going out;
- `OpenAsync(archive)`: sealed bytes coming in.

The plain config database and `last-good.json` pass through the vault only as files in a staging directory. The vault's architecture tests and banned-symbol rules stay green.

### Left out

- The wizard's backup reminder. It needs a record of whether a backup was ever made and a place for it in the checklist, which is not small. The audit log already says (`backup.create`).
- Scheduled backups and retention. The install guide shows a cron line; a scheduler inside the process would have to hold the passphrase.
- Incremental backups.
- A restore endpoint or button.
- Including the log files (they are not state).
- The download goes through the circuit and the browser holds it in memory. That is fine for a homelab's databases and not for a multi-gigabyte one. The CLI and the API stream.

### Tests

- **Vault:**
  - the RFC 9106 vector, header parsing;
  - the chunked stream: every size around a chunk, a wrong key, a changed byte per position, cuts at and inside chunks, swapped, dropped and appended chunks, a changed header;
  - the archive round trip with every kind of sealed value (secrets with a previous value, account keys, certificate keys and the local CA, the Data Protection ring, password credentials, TOTP) opened under another master key;
  - the source key absent from the archive, a generated key when the target has none;
  - a wrong passphrase, tampered bytes at sampled positions, truncations, a non-archive, a newer schema;
  - private modes, one backup at a time, a consistent snapshot while a writer replaces secrets.
- **Data:** the service's permissions per role, the token scope, the audit of every outcome, the download file's lifetime.
- **Integration:**
  - a real host backed up while it serves and restored on a data directory with another master key (the revision hash, the certificate's key, an access-list password, an authenticator-protected sign-in);
  - the CLI's passphrase sources, no overwrite, refusal while a server runs, a second server refused;
  - the old databases kept aside, a wrong passphrase, a tampered and a truncated file, a newer schema, and a failure after the move undone;
  - the API's roles, scope, validation, 409, audit and the passphrase's absence from logs.
- **Dashboard:** bUnit for the page and a Playwright download.

## Certificates and ACME

### The design in brief

- **Own ACME client (RFC 8555)** in `Paduraksa.Acme`, about 1.5k LOC. We write our own instead of using Certes or ACMESharp, because the account key must stay in the vault (`IJwsSigner`) and we want first-class **ARI (RFC 9773)** and **ACME profiles** (for example Let's Encrypt `shortlived`).
- **Challenges.** HTTP-01 (served by the public app's gate from an in-memory store). DNS-01 (provider plugins plus a propagation check by querying authoritative NS directly). TLS-ALPN-01 later.
- **DNS providers (v1 plan):** Cloudflare, deSEC, Hetzner, DigitalOcean, Porkbun, DuckDNS, Route53, RFC2136, acme-dns, plus "manual" (show the TXT record and wait for confirmation) and a webhook provider. Interface: `CreateTxtAsync`, `DeleteTxtAsync`, `ValidateCredentialsAsync`. Every provider needs a **"Test credentials"** button in the UI. Built so far: Cloudflare and deSEC.
- **CAs:** Let's Encrypt (prod and staging), ZeroSSL (EAB), Google Trust Services (EAB), Buypass, custom directory URL.
- **Account key rollover (RFC 8555 section 7.3.5) is deferred past v1.** Recovery from a lost or compromised account key is a new account. The shape when it comes is additive on `IJwsSigner`: `StageAccountKeyAsync` (generate the new key, return its public JWK), `SignWithStagedAsync` (sign the inner `keyChange` JWS with it) and `CommitStagedAccountKeyAsync` (swap it in once the CA accepted the change). Nothing existing changes.
- **Renewal.** A background scheduler uses the ARI window if offered, otherwise renews at 1/3 of lifetime remaining, with jitter, exponential backoff and rate-limit awareness. On success the snapshot is recompiled and published (zero-downtime cert swap). On failure: notification plus dashboard banner.
- **Auto-matching.** Hosts with `CertificateBinding = Auto` get the most specific valid cert covering their names (exact beats wildcard). The dashboard flags hosts without coverage and offers "Issue cert for these names".
- **Other sources.**
  - Uploaded PEM or PFX goes straight to the vault through the API's streaming endpoint, so the dashboard upload goes to that endpoint rather than through the Blazor circuit.
  - Self-signed.
  - **Local CA:** an internal root generated in the vault, issuing leaves for LAN-only hosts, with the root cert downloadable for installing as trusted.
- **Serving.** `SslStreamCertificateContext.Create(cert, chain, offline: true)` is prebuilt per snapshot, so no chain building happens during a handshake. **OCSP stapling is out of scope for v1.** Let's Encrypt dropped OCSP in 2025, and we do not want to promise cross-platform server-side stapling behaviour.
- **Cloudflare's origin-pull CA** (Authenticated Origin Pulls, see [cloudflare.md](cloudflare.md)) is public data shipped as an embedded resource and replaceable by a PEM file setting. It is a trust anchor, not an inventory certificate, and no client certificate is ever stored.
- **Expiry monitoring** covers every certificate, not just ACME ones. Uploaded and self-signed certs have no renewal path, so they get the same 30/14/7/1-day notifications and a dashboard badge. Local-CA leaves can be re-issued in one click (or automatically).

### Storage and compile-time decisions (decided before M3)

- **Certificates, ACME accounts and DNS providers are not part of the config document.** They are operational resources in the config database (`Certificates`, `AcmeAccounts`, `AcmeOrders`, `DnsProviders`): metadata, the public chain and renewal state.
  - Secrets appear only as `SecretId` references into the vault.
  - Application services change them and audit every change. They do not create config revisions.
  - Config export carries certificate metadata only.
- **The document references certificates only by binding:** Auto, Specific (`CertificateId`) or None. The compiler resolves bindings against the **certificate inventory** (the valid certificates and the names they cover), passed in alongside the document. Auto picks the most specific valid certificate covering the host's names: an exact name beats a wildcard, then the latest `NotAfter` wins.
- **A certificate change (issued, renewed, uploaded or deleted) recompiles the latest revision with the new inventory and publishes it**, without creating a revision. Deleting a certificate that a Specific binding uses is refused.
- **Replacing a certificate keeps its ID and is atomic.** Renewal, re-issue, a new upload and a new self-signed certificate on an existing `CertificateId` swap key and chain together in one vault transaction, so Specific bindings survive. ACME and manual CSR flows stage the new key first: `CreateKeyAndCsrAsync` returns a `StagedKeyId` that `AttachChainAsync` must present. An overlapping second staging cannot silently take the first one's place; it fails with a documented exception instead.
- **The local CA root is a normal certificate.** `EnsureLocalCaAsync(CertificateId, ...)` creates it under a caller-chosen ID, so it has a Certificates row, expiry monitoring and a downloadable public chain like any other.
  - Rotation is built (see [Local CA name constraints and root rotation](#local-ca-name-constraints-and-root-rotation)).
  - **Nothing renews it yet:** its last 90 days shorten every leaf it issues, the admin listener's own included (see [hosting.md](hosting.md)). That is the open item the hosting page refers to.
- **DNS credentials carry their own destinations.** The allowed hosts of a DNS token are metadata the admin sets when creating the DNS provider, stored with the secret (`SecretOptions.AllowedHosts`). The vault's authenticating handler uses their intersection with the provider code's own list, speaks only https, never follows redirects, and is the whole transport.
- **Shared EF/SQLite infrastructure lives in `Paduraksa.Storage`:** typed-ID conventions and converters, SQLite pragmas and the connection-string builder, and the UTC timestamp converter. Vault may reference only Core and Storage, so it gets these without seeing any config entity.

## The certificate inventory (M3)

### Storage and the service

**Storage.**
- `Certificates` has one row per `CertificateId`: source (`acme`, `uploaded`, `self_signed`, `local_ca`, `local_ca_root`), status (`pending`, `active`, `failed`) with a message, subject, issuer, serial, AKI, SHA-256 fingerprint, key algorithm label, `NotBefore` and `NotAfter`, chain issuers, the **public** chain PEM, label, notes, created and updated.
- `CertificateNames` has one normalised row per SAN (`NOCASE`, indexed).
- Expiry is not a status. It follows from `NotAfter` and the clock.
- It was added as a new migration (`Certificates`), not a regenerated `Initial`, so a development database upgrades and parallel branches do not collide.
- No private key column exists. Host bindings reference a certificate by ID with no foreign key (the document that names it is a revision, not a row).

**`CertificateService`** (Application, DTOs in `Contracts.Certificates`) has `RegisterAsync` (add, or replace the metadata under the same ID: a renewal), `RegisterPendingAsync`, `SetStatusAsync`, `UpdateDetailsAsync`, `DeleteAsync`, `ListAsync` (by source, status, or "covers this name") and `GetAsync` (with the chain).
- It never calls the vault. The vault flows call it after they created the key and chain.
- Every change is audited in its own transaction (`certificate.add`, `certificate.update`, `certificate.delete`; refusals too) and creates **no revision**.
- When a change affects serving (validity, names, chain, status), it recompiles the latest revision with the new inventory and publishes it through the config writer (`ConfigService.ApplyCertificateChangeAsync`), so publishes stay ordered.
- Deleting a certificate that a Specific binding or the default certificate uses is refused, with the paths of the referencing hosts.
- Names that are no DNS name, wildcard or IP address are dropped. None left is invalid.

### Preload and selection

**Preload.** Before each compile, `CertificateInventoryLoader`:
1. reads the metadata (plus `IBuiltInCertificates`);
2. judges each certificate against `TimeProvider` (active and inside its validity period);
3. works out which valid certificates the document can choose: the named default, Specific bindings, and the best match per name of every enabled Auto host;
4. loads exactly those through `IServerCertificateSource`;
5. hands the compiler an immutable `CertificateInventory` whose resolver covers the loaded ones.

A certificate the source cannot load (null or an exception) is left out and the selection runs again, so Auto falls to the next best certificate. Contexts are used as the source returns them: an unchanged certificate keeps its instance, so the proxy's certificate table keeps one options object per certificate. A renewal (a new instance) takes effect with the next publish. The inventory is a compile input, never mutated.

**Selection is per name.** The proxy's certificate table maps names to certificates, so each DNS name of a host gets its own best certificate (`HostSpec.NameCertificates`), not one certificate for the whole host.
- A certificate covers a name when it lists it, or lists the wildcard of its parent. `*.example.com` covers `www.example.com`, not `example.com` or `a.b.example.com`. A wildcard host name is covered by the same wildcard.
- The best match is exact over wildcard, then the latest `NotAfter`, then the latest `NotBefore`, then the smallest ID.
- IP names never reach the table and ride the default certificate.
- Specific gives every name of the host that certificate. A certificate that does not cover them is a warning.

**Degraded, never failed.**
- A name no valid certificate covers gets no certificate of its own. It is served with the default certificate, or rejected when there is none. The compile reports `certificate.auto.noCoverage` (a `ConfigWarning` on the compilation, `ConfigService.Warnings` and the command result).
- A Specific binding, or a named default, whose certificate is missing, expired, pending, failed or without a key degrades the same way (`certificate.specific.unusable`, `certificate.default.unusable`). That holds at startup, for the last-known-good fallback and for republishes. One bad certificate never fails the whole config.
- An unreadable certificate table gives an inventory of the built-in certificates only.
- A **command** may not introduce such a binding. A new or changed Specific binding, or a changed default, naming an unusable certificate is `host.certificate.unusable` or `settings.defaultCertificate.unusable`, also in dry runs. But a command is not blocked by a binding that went bad since.
- A rollback and the system's restore after a lost database are exempt. They put back a config that was accepted once, and degrade with warnings like startup does (the inventory may be gone with the database).

**The placeholder.** It has a year of validity: it is dev-only and new in every process, so only a process that runs for a year outlives it, and a restart replaces it.
- `PlaceholderCertificates` is a source and a built-in inventory entry for the in-memory certificate under a well-known stable ID, and the inventory's fallback default.
- It is composed **after** the vault (`CompositeServerCertificateSource`: a certificate the vault holds is never shadowed). It is wired in at all only when `Paduraksa:Development:PlaceholderCertificate` is on (default: Debug builds yes, release builds no; see [hosting.md](hosting.md)).
- Off, the source falls back to nothing, there is no built-in entry, and a document that still names the ID degrades like any missing certificate.

**Built-in IDs** (`IBuiltInCertificates`) cannot be registered in the table: the built-in would silently win.

### Validity follows the clock

- A certificate counts as valid from five minutes before its `NotBefore` (one named tolerance, for clock skew) until a second after its `NotAfter`.
- The inventory reports the earliest such boundary still ahead (`NextBoundary`, carried on the compilation as `ValidityChangesAt`). When a snapshot goes live, the config service arms one `TimeProvider` timer for it (replaced by every newer live snapshot, cancelled on dispose). When it fires it republishes the live config under the writer lock, coalesced with other publishes. Expiry and activation need no other trigger.
- A renewal under the same ID whose `NotBefore` is in the future leaves the names uncovered (with a warning) until that boundary, because the one row now describes the new certificate.

**Republishing never publishes the empty config.** A certificate change or a validity republish compiles the database's latest revision. When the start is degraded on the last-known-good file, or the database has no revision (it was lost), it compiles the last-known-good document. Without either it publishes nothing and logs why. A last-known-good republish does not end a degraded start. The compile happens inside the change's transaction, before the commit. After the commit nothing can fail the call.

### After a database loss

The certificate rows are gone while the vault still holds the keys.

**The catalog.** The vault re-describes what it holds (`ICertificateCatalog`: `ListAsync` and `DescribeAsync` return the ID, whether it is the local CA root and the `CertificateInfo` of the stored chain).
- It is public data, read from the chain stored beside the sealed key, so no key is opened and no gate is taken.
- A row whose chain cannot be read is left out and logged. The query never names the sealed column, which a test pins.

**Recovery.** `CertificateRecovery` (Application) registers every certificate the vault holds that the inventory has no row for, through `CertificateService.RegisterRecoveredAsync`.
- It acts as the system (audited). Registrations are quiet, and the startup step queues one publish afterwards, so restored Specific bindings serve again.
- **Register-if-absent under the writer's lock.** Per certificate, right before registering, the row's absence and the audit trail are checked inside the config writer's lock. A registration racing with this (a vault-first upload) is never overwritten, and an existing row, its label included, is never changed.
- **The source is not known**, so it is inferred from public facts and never guessed towards more capability:
  - the root is `local_ca_root`;
  - a certificate whose **signature verifies under that root's public key** is `local_ca` (a custom-root `X509Chain`, time and revocation not judged; a matching issuer name alone is not enough);
  - everything else is `uploaded`. That includes a lost self-signed one, since it cannot be told from an uploaded self-signed one and an upload is never replaced by a re-issue. It also includes a lost ACME one: its order, account and schedule were in the lost database, so it is not renewed until requested again.
- Each recovered row has the label `Recovered from the vault`.

**A deleted certificate is not resurrected.**
- Deleting removes the row (audited as `certificate.delete`) and then the key. If the key's deletion failed, the vault still holds it while the database is intact. A successful `certificate.delete` on record for the ID therefore means "finish the deletion" (`ICertificateIssuance.DeleteAsync`, retried at the next start if it fails) instead of registering.
- The same trail covers a creation that was rolled back (its pending row was deleted and audited).
- A key with no such trail (a registration that was refused, an upload interrupted between the vault and the row) is registered, visible and deletable. A lost database loses the trail with it, so nothing is treated as deleted then.
- One window remains: an upload that reached the vault but has not registered when this runs is registered as recovered, and its own registration then replaces the metadata (an upload without a label keeps the recovered one).

**When it runs.** Once per start, in the core host (`CertificateRecoveryStartup`), as an **awaited start step after the config store and before the web apps**. Registrations are quiet (no republish per certificate), then one publish is queued, so the first snapshot the listeners serve already includes the recovered certificates.
- The step is bounded by `Paduraksa:Certificates:RecoveryStartupTimeout` (10 s; 1 s to 5 min). Past it the run continues in the background and the start goes on.
- A stop is checked between certificates. Registrations and key deletions run with `CancellationToken.None`, so an abort never cancels a transaction partway through (and EF never logs it at Error).
- `SharedServices.RecoveryCompletion` completes when the run has finished, which tests await instead of sleeping.
- It skips a store that does not accept changes, never stops the process, logs one summary line, and leaves certificates that have a row alone whatever the row says.

### The vault flows (`CertificateOperations`)

DTOs are in `Contracts.Certificates`; the code is in Application. The flows are: upload (optionally over an existing uploaded certificate), self-signed, local CA root, local-CA leaf, re-issue (self-signed and local-CA only, same ID and names) and delete.

Each is one authorizer call (`ICertificateOperationAuthorizer`, allow-all until M4), the vault call, and the `CertificateService` registration (so audit and republish are the service's).

**Ordering decides what a failure leaves.**
- A new certificate whose names are known (self-signed, leaf, root) is registered *pending* first (validates names, visible, never served). Then the vault creates key and chain. Then the same row is registered active.
- A failed vault call or a refused registration deletes the pending row and the key again (not cancellable). A crash between leaves a visible pending row, never a hidden key.
- A new upload learns its names from the vault, so it goes vault first and is rolled back (key deleted) if the registration is refused. A key with no row is never loaded.
- Replacing an existing ID swaps in the vault first (it cannot be undone) and then updates the row. If that fails, the result says so, it is logged, and repeating the operation repairs it.
- Delete asks the inventory first, which refuses (nothing touched in the vault) while a Specific binding or the default certificate uses it. Then it deletes the key. A failed key deletion is a warning in the result: the certificate is already gone from the inventory, and the key is unreachable.
- Deleting the local CA root is refused while local-CA leaves exist. A delete with no row still removes an orphaned key.
- The root has no names, so `RegisterAsync` and `RegisterPendingAsync` accept an empty name list for `CertificateSource.LocalCaRoot`.
- The label (and what the inventory would refuse about the request) is checked before the vault is called, so a replacement is never reported invalid after the vault took it.
- Only an *active* root row counts as the local CA. A root row a crash left pending is completed under its own ID. When the vault holds the root under another ID (the inventory was lost), it is recorded under that ID (`LocalCaExistsException`), which also repairs the database-loss case for the root.
- The key deletions after a commit and the rollbacks run with `CancellationToken.None`, and a failed creation always deletes the key (idempotent), so a cancelled request leaves no orphan.
- Request DTOs carry `KeySpec` as its stable name (`ecdsa-p256`, `rsa-2048`, ...).

### The process's own admin certificate

`AdminCertificate.Id` is an ordinary `local_ca` row with a stable ID and the label `Admin (internal CA)`, plus `certificate.managed`. Deleting it, re-issuing it by hand and binding it (Specific or as the default) are all refused, and the inventory loader leaves it out of what the compiler sees. The lifecycle itself is in [hosting.md](hosting.md).

Here is how the registry side works:
- `CertificateService.RegisterQuietlyAsync` is `RegisterAsync` without the republish. It is what the core host's `AdminCertificateService` uses.
- That service adopts the vault's existing leaf (via `ICertificateCatalog.DescribeAsync`) instead of issuing a new one when the row is lost. It does so only when that leaf carries exactly the names the run would ask for (or the next run would re-issue it anyway). A database loss with unchanged names therefore leaves the served fingerprint alone.
- The other way round (the row is there and the vault's key is gone: `vault.db` deleted or restored from an older copy), it retires a stale root as `failed`, makes a new root and issues a new leaf. The admin port comes back with no operator step.
- `CertificateRecovery` skips the ID entirely. The service owns it, so the two writers cannot race over one fixed ID.
- A rollback of the admin leaf still removes its row, which writes a `certificate.delete` audit event (a refused one is `invalid`, a successful one `success`). It never deletes the vault's key: the ID is fixed, so the pair the vault holds may be the only servable one, and the next run adopts it.
- The local CA root is adopted the same way (registered under `CertificateRecovery.Label`). `local_ca.inUse` tells the user the root is in use, rather than telling them to delete the admin leaf, which they cannot.

## Local CA name constraints and root rotation

Built in the host-header-acme branch.

### Name constraints

**Decision: a root is unlimited unless the operator chooses names.** A root created at first run, by the admin certificate's flow, or by "Create the local CA" without choosing names has **no** name constraints and may issue for any name. The owner decided this so that a first-run root never refuses a name the operator uses.

**Limiting a root.** The operator chooses names with the names dialog's "Limit what this root may issue for" switch (off by default), at creation or rotation. In the API these are `LocalCaRequest` and `LocalCaRotateRequest`, `PermittedDomains` and `PermittedAddresses`.
- The root then carries a critical X.509 name-constraints extension (RFC 5280 4.2.1.10, permitted subtrees only). It holds exactly the list chosen plus what the admin listener's certificate is wanted for.
- A device that trusts it accepts a certificate from it only for those names: a name equal to a listed domain or under it (a wildcard is as wide as its parent), a network, or a single address.
- An empty list means unlimited.

**Suggestions.** The dialog seeds the list (`LocalCaStatus.SuggestedDomains` and `SuggestedAddresses`, also in the API) with:
- the private-use domains `home.arpa`, `lan`, `local`, `localdomain`, `localhost`, `internal`, `home`, `corp`, `intranet`, `private` and `test`;
- the private networks `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`, `100.64.0.0/10`, `169.254.0.0/16`, `127.0.0.0/8`, `fc00::/7`, `fe80::/10` and `::1/128`;
- the admin certificate's names;
- the names of the certificates the local CA has issued (the hosts that use it).

Unlimited stays the default.

**The admin names are always kept in a limited root.** They are the **desired** ones (`CertificateOperations.DesiredAdminNames`, which the service that serves the certificate sets from the process's settings and the machine's current name), not those the certificate was last issued for. A machine renamed since therefore cannot be locked out of its own admin port by a rotation.

**Enforcement.**
- The vault enforces the limits on every leaf it signs (`LocalCaNameNotPermittedException`, answered as `certificate.localCa.nameNotPermitted` with the permitted list).
- A root without them issues for any name, as every root did before. The four-argument `EnsureLocalCaAsync` makes one.
- The extension is encoded and read by hand in the vault (`NameConstraintsExtension`: .NET has no type for it). A test shows a client's chain builder refuses what the vault would refuse.
- An IP-only certificate has no subject, since a client judges a common name as a DNS name.
- The Local CA page says "Any name. Rotate the root to limit it." for an unlimited root.

### Rotation

`CertificateOperations.RotateLocalCaAsync` (`POST /api/v1/certificates/local-ca/rotate`, dashboard "Rotate the root" with a warning dialog):
- The new root is unlimited or limited as chosen above.
- The vault makes it and **destroys the old root's key in the same transaction** (`ICertificateIssuance.RotateLocalCaAsync`). Nothing needs the old key again, a rotation made because it leaked must not leave it, and the unique index guarantees one signing root.
- The new root's row is recorded (`certificate.add`). If that fails it is tried once more. If it fails again, the result says the vault holds the new root and the old one stays shown until the next restart registers it.
- The rotation is audited (`certificate.local_ca.rotate`, with the retired ID and what the new root is limited to).
- The admin listener's certificate is issued again from the new root at once. An event makes `AdminCertificateService` serve it now, not at the daily check.

**The old root's row stays Active and downloadable.**
- A root row that is not the newest active one is **retiring** (`GetLocalCaAsync`, `GET /api/v1/certificates/local-ca`: the root, what it is limited to, and each retiring root with the number of certificates it signed, counted by issuer name, which is random per root).
- Until then everything the old root signed keeps serving unchanged (the proxy's chain is the leaf and its issuer, not the root). Nothing breaks for devices that have not installed the new root yet.

**Re-issuing leaves.** `ReissueLocalCaLeavesAsync` (`POST .../local-ca/reissue`, the page's "Re-issue N certificates") issues those leaves again from the current root under the same IDs (Specific bindings survive). One whose names the new constraints do not permit is left as it is and listed.

**Retiring** is the existing delete. It is refused (`certificate.localCa.inUse`) while a certificate that root signed is still in use, and allowed after. The page's Replaced roots card offers PEM and .crt downloads of each retiring root until then. The anonymous `/api/v1/ca/root.*` endpoints keep serving the current root only. The newest active root is the one `EnsureLocalCaAsync` and the admin certificate's own flow treat as the root.

### Left out

- Cross-signing the new root with the old one. It would let devices that trust only the old root accept the new one's leaves, but a leaked old key could then sign for it too, which is what a rotation is meant to end.
- Changing a root's permitted names without rotating (a root's extension cannot change).
- A name-constraints editor for an existing root. A root that has been limited can be made unlimited only by rotating with no names.

### Tests

- Vault: `LocalCaConstraintsTests` (the critical extension and its description, leaves inside and outside, a client's chain builder, bad constraints, the unconstrained four-argument root, rotation, rotation to an unlimited root, and its refusals).
- Integration: `LocalCaRotationEndToEndTests` (an unlimited first-run root, limiting it by rotating, the admin names kept as wanted now, the overlap with the proxy serving the old chain, the admin certificate from the new root, refusal to retire while in use, re-issue, retire, the new limits).
- Data: the audit event and the registration retry.
- Dashboard: `LocalCaEndToEndTests` in a real browser (dialog, rotation, both roots, retire; dark, light and phone screenshots).

## ACME issuance and renewal (M3)

### Storage

Migration `AcmeIssuance`:
- `AcmeAccounts`: directory URL, contacts, status (`active` or `deactivated`), account URL, key algorithm, EAB key id (`EabKid`, the CA's identifier, not a typed ID) and `EabSecretId`, ToS agreed, label.
- `DnsProviders`: kind (`cloudflare` or `desec`), label, `CredentialSecretId`.
- `AcmeOrders`: **one row per certificate** (unique `CertificateId`, cascade with the certificate; account and provider are `Restrict`; DNS-01 requires a provider by a check constraint). It is reused by every issuance and renewal, so "one issuance per certificate" is a property of the table.
- Enums are stored by stable name with check constraints (a new DNS kind is an enum member plus a migration).
- The `Certificates` row stays the serving metadata (`source = acme`).
- Secrets appear only as `SecretId`. The staged key is a token, and the stored CSR is public.

### Services

Application, DTOs in `Contracts.Issuance`:
- `AcmeAccountService`:
  - create: `IJwsSigner.CreateAccountKeyAsync`, then `newAccount`, with EAB through `IMacKeySource`. A refused registration deletes the key again and records nothing;
  - deactivate is final, refused while certificates use the account, and deletes the vault key;
  - list and get.
- `DnsProviderService`: create over an existing `DnsApiToken` secret, delete (refused while in use), and "Test credentials".
- `AcmeIssuanceService` (below).
- `AcmeRenewalScheduler` (hosted service).
- `AddPaduraksaIssuance` for the container.

Accounts and providers are audited through `ConfigService.ApplyOperationalChangeAsync`: the config writer's lock and transaction, so every audit event stays on the one hash chain, with no revision and no publish.

### The orchestrator's state machine

**`RequestCertificateAsync`**:
1. validates: names normalised like SANs; HTTP-01 refuses wildcards; DNS-01 refuses IPs and needs a provider; the account must be active;
2. registers a pending certificate (`CertificateService.RegisterPendingAsync`);
3. inserts the order row;
4. runs.

**`RenewAsync`** is the one entry for "do the next step".
- A `valid` row starts a new order, decided from the row read under the certificate's guard. The CA is told `replaces` = the ARI ID of the certificate being renewed. Only `alreadyReplaced`, or a bare `serverInternal` 500 with no `Retry-After`, repeats the order without that hint. A 503, 408, bad nonce, rate limit or any `Retry-After` keeps the normal backoff, so the CA's timing, its load and the ARI renewals' rate-limit exemption are respected. The retry is logged at Warning with the certificate ID and problem type only.
- An unfinished or `failed` row continues.

**A run follows the CA's order status.**
1. Read the stored order. An order that is `invalid` or `expired`, or that the CA refuses to show with a permanent problem such as 404, is forgotten. `accountDoesNotExist` is not: it marks the account deactivated.
2. Else `newOrder` (URL saved, row `ordered`).
3. `pending`: fulfil every pending authorization (publish, respond, wait, always clean up).
4. `ready`: `CreateKeyAndCsrAsync`, save the staged-key token and the CSR (row `finalizing`) **before** `finalize`.
5. `processing`: wait.
6. `valid`: download (preferred chain by issuer CN), `AttachChainAsync`, then **save the returned public `CertificateInfo` on the row (`AttachedInfo`)** and finish with `CancellationToken.None`: `CertificateService.RegisterAsync` (active; existing republish; retried a few times if the store is busy), plan the renewal, row `valid` (`AttachedInfo` cleared), audit `acme.order.issued`.

A run does not start when the config store is degraded (nothing could be recorded). Row statuses: `pending`, `ordered`, `finalizing`, `valid`, `failed`.

### Resume

Because the CA's state decides, a restart (or a retry) continues the same order where it stands.
- Finalize is submitted from the saved CSR.
- A `processing` or `valid` order is attached to the saved staged key.
- A challenge already `processing` is only waited for.
- Cancellation (shutdown) leaves the row as it was. Every other failure is recorded.
- A row with `AttachedInfo` skips the download and attach and only registers. Cancellation or a refused registration after the attach therefore cannot strand the new certificate.

**The hard-crash gap** is the process dying between the vault's attach returning and `AttachedInfo` being saved. It left the vault serving the new certificate under the old metadata with a spent staged key. It is closed by `ICertificateCatalog`: when attaching meets `StagedKeyMismatchException`, the service asks the vault to describe the certificate. If the leaf it holds is this order's downloaded leaf (the described SHA-256 fingerprint equals the hash of the leaf's DER), it takes its `CertificateInfo` as the attach result (a warning is logged) and goes on to register. Another leaf is a real mismatch and fails as before.

### Failures

- They are classified by `AcmeFailureKind` (and DNS provider and own failures), stored on the row (message, `Attempts`, `LastAttemptAt`, `RetryAt`) and audited (`acme.order.failed`).
- A certificate that was never issued (or has expired) becomes `failed` with the message. A renewal that fails while the old certificate is valid leaves it `active` and serving.
- An order that cannot be continued (invalid or deactivated authorization, invalid order, spent staged key, CSR refused) is dropped so the next attempt makes a new one. Anything else resumes. A finalize that races with another and meets `orderNotReady` on a processing order continues with it.
- An account the CA no longer knows is marked deactivated.
- Messages are sanitised and cut to 1024 characters.

### HTTP-01

`AcmeChallengeStore` (Proxy; `IAcmeChallengeStore` in Core is the publish side) is in memory, holds at most 1024 tokens, and counts each by its holders (two issuances sharing a pending authorization keep it answered until both removed it). Entries are validated (base64url token, printable-ASCII key authorization) and stored as a ready `PrecomputedResponse`.

The gate (`GateMiddleware`, optional constructor argument, registered once in `SharedServices`) answers **plain-HTTP GET/HEAD** `/.well-known/acme-challenge/{token}` for an exactly matching published token with 200 `text/plain`, before host lookup and every policy, 0 B on hit and miss.

Any other request under the path (unknown token, another method, HTTPS) falls through to the normal pipeline rather than a blanket 404. An upstream that runs its own ACME client therefore keeps serving its own challenges. A hit is never logged.

### The DNS-01 seam

`IDnsProvider` (`CreateTxtAsync`, `DeleteTxtAsync`, `ValidateCredentialsAsync`, idempotent, `DnsProviderException` says transient or not) and `IDnsProviderFactory` live in `Paduraksa.Acme.Dns`.
- A wildcard and its base name publish two TXT records at one name.
- All are created. The orchestrator waits (the authoritative-NS propagation check, or `DnsPropagationDelay`, 30 s, as the fallback), responds, and deletes the records afterwards even on failure.
- The Cloudflare and deSEC providers are described below.

### Scheduler timing

`RenewalSchedule` is pure arithmetic. `TimeProvider` and an injectable random source drive the rest. The row says what is due.

**`NextRenewalAt`**:
- With an ARI window: a uniformly random point in it (never after `NotAfter`; a window in the past is due now).
- Without: `NotAfter` minus 1/3 of the lifetime, moved earlier by up to 10 % of that third (30 days of 90, 2 days of 6).
- An ARI pick is never later than that 1/3 rule. A window at the end of life must not become an outage.

**`AriCheckAt`**: the response's `Retry-After` bounded to 15 min to 24 h (6 h without). A recheck keeps the old pick if it is still inside the window, else picks again.

**`RetryAt`** after a failure is exponential from the kind's base, with +/-20 % jitter that stays symmetric and never passes the cap (the doubling stops at cap/1.2):

| Failure kind | Base to cap |
|---|---|
| Transient | 5 min to 6 h |
| Permanent | 1 h to 24 h (a retry still happens: a DNS record may be added later) |
| Protocol violation | 30 min to 24 h |
| Rate limited | 1 h to 7 d, but never less than the CA's (or a DNS provider's 429) `Retry-After` |

A transient failure with a `Retry-After` (a 5xx, say) is floored by it too, up to its cap.

**The loop.** `RunOnceAsync` runs every minute, after a start delay, one certificate at a time. It lists the orders and calls `AcmeIssuanceService.RunDueAsync` for each. That **re-reads the row under the certificate's guard** and applies the due rules to the fresh row (the listing can be minutes old):
- unfinished order: resume;
- `failed` and `RetryAt` passed: retry;
- `valid` and `NextRenewalAt` passed: renew;
- else `AriCheckAt` passed: refresh ARI (also under the guard).

Failures are logged and kept on the order and the certificate. Notifications come with the channels (see [observability.md](observability.md)).

### Server wiring

`IssuanceStore` is composed in `SharedServices.Create` after the vault, the config store and the shared `AcmeChallengeStore`. The public gate reads that store and the issuance service publishes to it through `IAcmeChallengeStore`.

`AddPaduraksaIssuance` runs in a container `SharedServices` owns, over the real vault (`IJwsSigner`, `IMacKeySource`, `ISecretStore`, `ICertificateIssuance`, `ICertificateCatalog`), the config store's context factory, `ConfigService` and `CertificateService`. The account, DNS provider and issuance services and the scheduler are shared instances (`SharedServices.AcmeAccounts`, `DnsProviders`, `AcmeIssuance`, `AcmeScheduler`).

**Options** are `Paduraksa:Acme` (`PaduraksaAcmeOptions`, validated at startup):

| Option | Default | Range |
|---|---|---|
| `Enabled` (the scheduler) | on | |
| `TrustedRootsFile` | | |
| `SchedulerStartDelay` | 15 s | 0 to 1 h |
| `SchedulerPollInterval` | 1 min | 1 s to 1 day |
| `DnsPropagationDelay` | 30 s | 0 to 1 h |
| `Paduraksa:Development:AcmeInsecureHttp` | off | |

- `AcmeInsecureHttp` is `AcmeClientOptions.AllowInsecureHttp`. It admits `http://` only for **loopback hosts**, so any other host's `http://` URL is refused even with it on. A startup warning appears when on.
- The bounds keep a typo from becoming a delay `Task.Delay` cannot take. The scheduler also clamps its pauses to a day and catches whatever a pause or a look trips over, so the loop never faults the host.

**CA trust.** One transport serves every ACME client of the process (`AcmeHttp.CreateHandler(roots)`).
- It never follows redirects.
- The chain check also accepts a chain that ends at one of the roots in `TrustedRootsFile`: a PEM file of at most 1 MiB, relative paths against the content root.
- The system's trust is tried first, then the roots'. For the roots, the chain must be valid now, with the intermediates the server sent, the `serverAuth` extended key usage required and the name still matching. Revocation is not checked for either kind, the transport's default.
- Every root must be a **self-signed CA certificate**. The file is read once, at startup, and an intermediate, a leaf or no certificate stops the process with the file named.
- The handler keeps copies, is owned by the shared services, and is disposed after the container holding the clients.

**Scheduler lifecycle.**
- It is a hosted service of the core host only (`CoreHost`, when `Enabled`), registered after `VaultStartup`, `ConfigStartup` and `CertificateRecoveryStartup`, so the vault and the config store are up first.
- Its first look waits `SchedulerStartDelay` and then `IProcessReadiness` (every listener bound), so an HTTP-01 validation never runs before the public listener can answer it.
- **On shutdown it is drained first**, before the public app stops (`PaduraksaHost.StopHostsAsync`). `AcmeRenewalScheduler.DrainAsync` starts no new look or certificate, and lets the one in flight finish for up to 10 s (an HTTP-01 validation needs the public listener to answer). Then it cancels what is left (a cancelled run leaves the order as it was and resumes at the next start). An idle scheduler stops at once. The core host's own stop then finds it finished.

**DNS providers.** `AddPaduraksaDnsProviders` (with the Cloudflare and deSEC providers) goes in the `IssuanceStore` chain before `AddPaduraksaIssuance`, so a DNS-01 request is not "not supported" (see below).

### Tests

The orchestrator, scheduler and services run in `Tests.Issuance`: its own project, because it alone lifts the key-material ban for its test code. They use real SQLite and the real config writer against the ACME client's fake CA, a fake vault and a virtual clock.
- The fake vault is `ICertificateIssuance` and `ICertificateCatalog` with staging rules. Its reported fingerprint is the SHA-256 of the leaf's DER, as the vault's is.
- `CertificateRecoveryTests` there run recovery over real certificates: sources by signature, a same-name impostor, rows left alone, a deleted certificate's key deleted, a refused deletion.

Other projects:
- `Tests.Acme` has loopback-TLS tests of the trusted-roots handler (accepted: root plus intermediate; refused: name mismatch, expired leaf, another root, clientAuth-only leaf, missing intermediate, an intermediate as a root) and of the loopback-only plain-http rule.
- `Tests.Proxy` covers the gate. `Tests.Unit` covers the schedule arithmetic.
- `AcmePebbleTests` run the orchestrator against a real **Pebble** (Docker, `PEBBLE_VA_ALWAYS_VALID`, random ports, skipped when Docker or the image is unavailable; `PADURAKSA_PEBBLE=off` skips, `PADURAKSA_PEBBLE_DIRECTORY` uses a running one). They cover real CSRs from a stand-in issuance, ARI by the computed certificate ID, `replaces`, and IP and wildcard identifiers.

**`PebbleIssuanceEndToEndTests`** (`Tests.Integration`) are M3's exit criterion with the real stack: a `PaduraksaHost` (real vault, config database, scheduler and public listener) against a **validating** Pebble (`PEBBLE_VA_ALWAYS_VALID=0`).

Topology:
- Pebble v2.10.1, pinned by digest, labelled with the test process's id so a killed run's leftovers are removed at the next one. It is not `--rm`, so a container that exits can be inspected and its logs reported.
- It runs in Docker on the host's network (`--network host`), listening on loopback ports picked per test, with a config the test writes (`httpPort` = the process's public HTTP port, retry hints of one second).
- The test names resolve to 127.0.0.1 through the container's `/etc/hosts` (`--add-host`), so its validation authority fetches `http://name:port/.well-known/acme-challenge/token` from the **gate**.
- The bridge network with `host-gateway` does not work behind a typical Linux host firewall, and Docker Desktop has no host network. The tests skip on non-Linux.
- The process trusts Pebble through `Paduraksa:Acme:TrustedRootsFile` (its minica root, copied out of the image once per run).

Cases:
- Request via HTTP-01, and the proxy serves the Pebble-issued leaf for every name (TLS handshake).
- Renewal keeps the `CertificateId` and the revision, and the served certificate changes, with the ARI window recorded and the renewal scheduled inside it.
- A restarted process whose renewal came due while it was down renews it with its own scheduler.
- A failed validation leaves a failed row with a retry, a coverage warning and nothing served (the handshake is refused).
- Without the root the directory is not trusted.

`AcmeHostingTests` cover the scheduler's switch (`Enabled=false` hosts none) and drain, the pacing bounds, the trusted-roots file's refusals and the plain-http warning.

## DNS-01 providers and the propagation check (M3)

### The credential path

- A provider (`Paduraksa.Acme.Dns`) is built from a `SecretId` and the vault's `ISecretHttpHandlerFactory`. Each operation opens an `HttpClient` over `CreateAuthenticatingHandler(secretId, injection, transport)` and disposes it, so the pool never outlives the call.
- The token is never a value in Acme code. The provider declares its own host in the `SecretInjection`: `api.cloudflare.com` with `Bearer`, and `desec.io` with `Authorization: Token`. The vault intersects it with the secret's `AllowedHosts`.
- A vault refusal (`InvalidOperationException`: host not allowed, secret gone) is a permanent `DnsProviderException`.
- `DnsApiClient` is the shared plumbing: bounded body (1 MiB), per-call timeout (30 s, on the system clock), `Retry-After` parsed, JSON by source-generated contexts (`CloudflareJsonContext`, `DesecJsonContext`).
- Messages hold the provider name, an HTTP status and the provider's own error text (control characters removed, cut to 200). Never a header.

### Failure classification

- 429, 5xx, timeouts and network errors are **transient**.
- Other statuses, a body that is not the expected JSON, an oversized body and "no zone/domain for this name" are **permanent**.
- A 429 is also flagged `DnsProviderException.IsRateLimited`, which the orchestrator maps to `AcmeFailureKind.RateLimited` (1 h base, `Retry-After` as a floor). A 5xx with `Retry-After` stays transient with the hint as a floor.
- A 429 whose `Retry-After` is within `DnsProviderOptions.MaxInlineRetryAfter` (65 s, so deSEC's per-domain write limit is waited out) is waited out once inside the call. The hint is the floor, plus up to 25% jitter so a burst of throttled clients does not return in lockstep. A longer one throws.

### Cloudflare (API v4, scoped token)

- **Zone.** Found by `GET /zones?name=` for the name and each parent down to two labels (longest first; a token scoped to some zones just sees no others), cached per provider.
- **Create.** First list CNAME records at the name (found: permanent "CNAME delegation is not supported yet"), then the TXT records (paged, 100 per page; more than 20 pages is refused, never cut off). Skip if the value is there, else `POST` with TTL 60. "Identical record exists" (81058) counts as created.
- **Delete.** List, then `DELETE` exactly the records whose content equals the value. Quotes Cloudflare may add on output are ignored. A 404 is logged as "already gone". The wildcard's and the base name's values therefore coexist.
- **Validate.** `GET /user/tokens/verify` (status must be `active`), then a zone read (`GET /zones?per_page=5`, the documented minimum). An account-owned token, which the user endpoint rejects, is judged by the zone read alone, and a token that reads no zone is invalid. DNS:Edit cannot be checked without writing, so a token that can read but not edit fails at the first create.

### deSEC

- **Domain.** `GET /domains/?owns_qname=` (the most specific the account owns; `subname` is the rest).
- **RRsets are whole.** `GET .../rrsets/{subname}/TXT/` (404 means none), add or remove values, write back the **full** `records` list.
- **Writes are batched per domain**, because deSEC limits them (2/s, 15/min, 100/h, 300/day per domain).
  - `IDnsProvider.CreateTxtBatchAsync` and `DeleteTxtBatchAsync` (default: one by one; Cloudflare uses the default) take the whole order's records.
  - deSEC sends at most one bulk `POST /rrsets/` (new sets) and one bulk `PATCH` (changed sets; `records: []` deletes) per domain.
  - No `PUT` is sent (deSEC needs the whole resource there).
  - A bulk `POST` that meets a set created meanwhile (400) re-reads and goes on as a `PATCH` once.
- A CNAME at the name is refused as with Cloudflare.
- Values are written quoted with `\\` and `"` escaped, and compared unquoted (a multi-string record `"a" "b"` reads as `ab`).
- The TTL is at least the domain's `minimum_ttl` (3600 default) and never lowers an existing set's.
- There is no ETag, so two writers racing on one set could lose a value.
- **Validate.** One bounded `GET /domains/?owns_qname=` (an unlisted full domain list pages above 500 domains).

### The DNS client

`System.Net.Dns` only does address lookups, and .NET 10 has no NS or TXT query. A package (DnsClient.NET, Apache-2.0) was weighed and not taken. The check needs only A, AAAA, NS and TXT over UDP with TCP on truncation, pointed at chosen addresses. A roughly 300-line codec (`DnsWire`, `DnsClient`, internal to Acme) keeps the dependency list and the parser's limits ours.

Parsing is bounded:
- messages at most 65535 bytes; names at most 255 bytes and labels 63;
- compression pointers only backward into the message body and at most 32 followed;
- reserved label types and non-hostname bytes rejected;
- every length checked against the message and the record's RDLENGTH;
- at most 256 answers; authority and additional sections never read.

Queries carry a random 16-bit ID (`RandomNumberGenerator`) and an EDNS0 OPT (1232 bytes). Each uses its own connected socket. A datagram whose ID, question name (case-insensitive), type or class do not match, or that does not parse, is ignored until the timeout. A FORMERR answer to the EDNS0 OPT record is retried once without it.

Not done: 0x20 case randomisation and DNSSEC validation (the check informs a wait, not a trust decision).

### The propagation check

`IDnsPropagationCheck`, implemented by `AuthoritativeDnsPropagationCheck`:
1. NS records for the name and each parent (down to two labels) from a recursive resolver.
2. A and AAAA of each name server through the resolver.
3. Every name server is asked directly for the TXT records at the name (RD off) until each returns every expected value (several values at one name all required, text strings joined) with the AA bit set and NOERROR or NXDOMAIN. A name server with several addresses is done when one address gives an authoritative answer.

Timing:
- Rounds run in parallel across servers: the first at once, then the pause starts at 2 s and grows x1.5 to 15 s, until `Timeout` (5 min). A query times out at 3 s.
- A server with no authoritative answer for 3 rounds in a row is left out and not asked again. This is a limitation: a name server that is merely slow to start answering may then be stale for a CA that asks it.
- If none is left, or no zone or resolver is found, the outcome is `Unverifiable`.
- Resolvers are `DnsPropagationOptions.Resolvers`, else the DNS addresses of the system's network interfaces, else 1.1.1.1 and 8.8.8.8.
- Satisfaction is reset for a zone's servers when more records join it, so every record is asked for.

Outcomes: `Propagated`, `TimedOut` (with `AnyServerHadRecords`) and `Unverifiable`. Settings are bindable through `DnsPropagationSettings` (`Paduraksa:Acme:DnsPropagation`: `Enabled`, `Timeout`, intervals, `Resolvers`).

### The orchestrator's use of it

`AcmeIssuanceService` takes an optional `IDnsPropagationCheck` and publishes an order's records in one `CreateTxtBatchAsync`.
- `Propagated` goes on to respond.
- `TimedOut` with at least one server holding a record goes on too (a warning is logged; the CA's verdict is the real one).
- `TimedOut` with **no** server holding any record fails the attempt as transient. Cleanup runs, and nothing is asked of the CA: a failed validation counts against its limits.
- `Unverifiable`, a check that throws, or no check waits `AcmeIssuanceOptions.DnsPropagationDelay` as before.
- Cleanup is one `DeleteTxtBatchAsync` on its own 3-minute real-time budget, retried up to three times on a transient failure (waiting the provider's `Retry-After`, up to 65 s), so a throttle does not leave records behind.

### Registration and options

`AddPaduraksaDnsProviders(DnsPropagationSettings? propagation = null)` (Application, `DnsProviderServiceCollectionExtensions`):
- registers `DnsProviderFactory` as the `IDnsProviderFactory` (`DnsProviderKind` picks Cloudflare or deSEC);
- if `Enabled`, registers the authoritative check as `IDnsPropagationCheck`;
- needs `ISecretHttpHandlerFactory`, uses `DnsProviderOptions`, `DnsPropagationOptions`, `TimeProvider` and `ILoggerFactory` when registered, and keeps anything already registered.

`AddPaduraksaIssuance` passes whatever `IDnsPropagationCheck` is registered to the orchestrator. Disabled keeps the fixed delay (for a host that cannot send DNS to the zone's servers). The Server composes it in `IssuanceStore`, running `AddPaduraksaDnsProviders(acme.DnsPropagation)` before `AddPaduraksaIssuance()` in the issuance container, over the vault's `ISecretHttpHandlerFactory`.

**`Paduraksa:Acme:DnsPropagation`** binds into `DnsPropagationSettings`:

| Setting | Default | Validation (the process does not start otherwise) |
|---|---|---|
| `Enabled` | true | |
| `Timeout` | 5 min | 1 s to 1 h |
| `InitialInterval` | 2 s | intervals 0.1 s to 10 min, and the maximum not below the initial |
| `MaxInterval` | 15 s | |
| `QueryTimeout` | 3 s | 0.1 to 30 s |
| `UnreachableRounds` | 3 | 1 to 20 |
| `Resolvers` | empty = the system's | at most 8, each an IP address |

`Paduraksa:Acme:DnsPropagationDelay` stays the fallback wait.

### `_acme-challenge` CNAME delegation

Built on `host-header-acme`. **Decision: auto-detection, no setting of its own.** A name that has a CNAME can hold no TXT record, so a CNAME at `_acme-challenge.name` can only mean "validate somewhere else", which is what a CA does when it follows it.

How it works:
- Before publishing, the orchestrator asks `IDnsChallengeNames` (`CnameDelegation`) for the name each record goes to. It reads the CNAME at the name through the same recursive resolvers as the propagation check (`DnsResolvers`, `Paduraksa:Acme:DnsPropagation:Resolvers`) and follows the chain, at most 8 names deep. A loop or a longer chain is a permanent failure naming it (no TXT record can make that validate).
- The provider then publishes at the end of the chain. It finds the zone of *that* name, so the DNS provider's credential must reach the target's zone, same account or not. A zone it cannot reach is the provider's own "no zone" refusal.
- The propagation check finds the delegated zone's name servers and asks them for the delegated name. Cleanup deletes at the same name. The progress line says which CNAME it followed.
- Several names delegated to one target (a wildcard and its base name) are two values at the target, as before.
- A resolver that cannot be asked means no delegation is assumed. The provider's own CNAME refusal (Cloudflare, deSEC) now says the record would have gone to the target.
- Turning `DnsPropagation:Enabled` off turns following off too: both need to send DNS queries.

Tests: `CnameDelegationTests` (none, one hop, a chain, a loop, too long, a dead resolver), the check against a fake delegated zone (`AuthoritativeDnsPropagationCheckTests`), and the orchestrator with a fake target (`AcmeIssuanceTests`).

### Tests

- `Tests.Acme`:
  - both providers against in-memory Cloudflare and deSEC APIs behind a fake of the vault's handler: https and host check, injection format, zone walk, two coexisting values, quoting, paging, 404, 429, error mapping, no token in messages or logs;
  - the DNS codec (truncation at every length, 5000 randomly damaged messages, pointer loops and forward pointers, bad lengths) and client (wrong ID or question ignored, TCP on truncation, timeouts) against a fake UDP/TCP server;
  - the propagation check against a fake resolver and two authoritative servers on 127.0.0.2 and 127.0.0.3 (skipped where those loopback addresses do not answer).
- `Tests.Issuance`: the orchestrator's use of the check and its fallbacks, the registration, and "Test credentials" through the real Cloudflare provider.
- Live Cloudflare and deSEC round trips (`LiveDnsProviderTests`) run only with `PADURAKSA_LIVE_CLOUDFLARE_TOKEN` and `_NAME` or `PADURAKSA_LIVE_DESEC_TOKEN` and `_NAME`.
- `Tests.Vault` (`DnsProviderThroughVaultTests`) runs the Cloudflare provider over the vault's real authenticating handler: the bearer token is seen by the terminal, and a secret whose allowed hosts lack `api.cloudflare.com` is refused permanently without the token in the message. Most other tests go through a fake of the vault's handler.
- `Tests.Integration` (`AcmeHostingTests`) checks that the providers are installed in the real host and that invalid `DnsPropagation` settings refuse the start.

**DNS-01 against Pebble, end to end** (built on `host-header-acme`, `AcmePebbleDnsTests` with `PebbleDnsFixture`; skipped without Docker like the other Pebble tests):
- Pebble runs **without** `PEBBLE_VA_ALWAYS_VALID` on a Docker network of its own, beside a `pebble-challtestsrv` container, which is its resolver (`-dnsserver`). A challenge passes only if the TXT record is really there when Pebble asks, so the test cannot pass on a record that was never published.
- A provider stand-in (`IDnsProvider`) writes each record into challtestsrv through its management API (`set-txt`, `clear-txt`, `set-cname`). It is the one published port, so a host firewall between the container and the host, which broke a first design that made the in-process fake Pebble's resolver, cannot matter.
- It also writes each record into an in-process `FakeDnsServer` that plays the zone's authoritative server for the **real** `AuthoritativeDnsPropagationCheck`. challtestsrv answers without the AA bit the check requires, so it cannot be that server.
- The orchestrator runs unchanged: publish through the batch call, the check finds the zone's name servers through the fake resolver, asks them directly (no recursion), Pebble validates, the certificate is issued, the records are deleted.
- A second test CNAMEs `_acme-challenge` into another zone. The record is published, checked and deleted at the delegated name only, and Pebble follows the CNAME.

