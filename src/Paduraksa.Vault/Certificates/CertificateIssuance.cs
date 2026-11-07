using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Vault.Crypto;
using Paduraksa.Vault.Persistence;

namespace Paduraksa.Vault.Certificates;

/// <summary>
/// <see cref="ICertificateIssuance"/> (PLAN §8.2, §9): keys are born here and leave only as a CSR or as the public facts of the
/// certificate made from them. Everything that replaces a certificate goes through <see cref="CertificateStore.PutAsync"/>,
/// so the key and the chain change together and the ID stays.
/// </summary>
/// <remarks>
/// <para>
/// <b>Certificates the vault signs.</b> Self-signed and local-CA leaves carry the SAN, the server-authentication EKU, the key
/// usage a TLS server needs, a subject key identifier and (for the leaf) the authority key identifier of its issuer; a
/// leaf is never a CA. The local CA root is a CA with path length 0 (it issues leaves only), key usage <c>keyCertSign</c> and
/// <c>cRLSign</c>, and its own random name. Serial numbers are 126 random bits (the top two bits of 128 are fixed, so the integer
/// is positive and its DER content bytes need no sign byte). Validity starts <see cref="ClockSkew"/> early.
/// </para>
/// <para>
/// <b>The chain handed back is the chain stored:</b> an issued chain is checked against the staged key (the leaf's public key
/// must be the key's, its SANs must cover the names that were asked for) and kept in issuing order without its root.
/// </para>
/// </remarks>
internal sealed class CertificateIssuance(VaultRuntime runtime, CertificateStore store) : ICertificateIssuance
{
    /// <summary>How long before now a new certificate's validity begins, so a client with a slow clock still accepts it.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>The longest validity the vault signs: a root's. Leaves are capped lower (<see cref="MaxLeafValidity"/>).</summary>
    public static readonly TimeSpan MaxValidity = TimeSpan.FromDays(365 * 30);

    /// <summary>
    /// The longest validity of a self-signed or local-CA leaf, silently capped to: 825 days, what Apple's trust store accepts
    /// for a certificate issued by a user-installed root (a longer leaf is rejected by Safari, iOS and macOS, so issuing one
    /// would only produce a certificate that fails there). The root is not subject to it.
    /// </summary>
    public static readonly TimeSpan MaxLeafValidity = TimeSpan.FromDays(825);

    /// <summary>How long a key staged for a CSR waits for its chain before the next staging deletes it (an abandoned order).</summary>
    public static readonly TimeSpan StagedKeyLifetime = TimeSpan.FromDays(30);

    /// <summary>How far in the future a chain's leaf may start and still be attached (clock skew), like the inventory's tolerance.</summary>
    public static readonly TimeSpan AttachTolerance = TimeSpan.FromMinutes(5);

    private const int SerialNumberLength = 16;

    private static readonly Oid ServerAuthentication = new("1.3.6.1.5.5.7.3.1", "Server Authentication");

    public async ValueTask<CsrResult> CreateKeyAndCsrAsync(
        CertificateId id, KeySpec spec, IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        RequireId(id);
        var checkedNames = CertificateNaming.Normalize(names);

        using var key = KeyPairs.Generate(spec);
        using var pkcs8 = KeyPairs.ExportPkcs8(key);
        var subject = CertificateNaming.SubjectOf(checkedNames);
        var request = KeyPairs.NewRequest(subject, key);
        request.CertificateExtensions.Add(CertificateNaming.SanOf(checkedNames, subject));
        var der = request.CreateSigningRequest();
        var pem = PemEncoding.WriteString("CERTIFICATE REQUEST", der);

        await runtime.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        using var gate = await runtime.EnterAsync(cancellationToken).ConfigureAwait(false);
        var context = await runtime.CreateContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            if (await context.CertificateKeys.AnyAsync(k => k.Id == id && k.IsLocalCaRoot, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException($"{id} is the local CA root; it takes no CSR.");
            }

            var transaction = await runtime.BeginSealingAsync(context, cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var stagedId = StagedKeyId.New();
                var now = runtime.Time.GetUtcNow();
                var row = await context.StagedKeys.SingleOrDefaultAsync(s => s.CertificateId == id, cancellationToken).ConfigureAwait(false);
                if (row is null)
                {
                    row = new StagedKeyRow { CertificateId = id };
                    context.StagedKeys.Add(row);
                }

                // Staging again replaces the row in place: the earlier token no longer names it, which is what makes it stale.
                row.StagedKeyId = stagedId;
                row.Names = [.. checkedNames];
                row.CreatedAt = now;
                row.Sealed = EnvelopeCipher.Seal(runtime.Kek, pkcs8.Span, CertificateSeal.StagedContextOf(id, stagedId, row.Names));

                // Keys staged for orders nobody finished do not wait forever.
                var cutoff = now - StagedKeyLifetime;
                await context.StagedKeys.Where(s => s.CreatedAt < cutoff && s.CertificateId != id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new CsrResult(stagedId, der, pem);
            }
        }
    }

    public async ValueTask<CertificateInfo> AttachChainAsync(
        CertificateId id, StagedKeyId stagedKey, ReadOnlyMemory<byte> pemChain, CancellationToken cancellationToken)
    {
        RequireId(id);
        if (stagedKey.IsEmpty)
        {
            throw new ArgumentException("The staged key ID can't be empty.", nameof(stagedKey));
        }

        if (pemChain.Length is 0 or > CertificateImportLimits.MaxBytes)
        {
            throw new ArgumentException($"The chain is 1 to {CertificateImportLimits.MaxBytes} bytes.", nameof(pemChain));
        }

        var parsed = CertificateFacts.ParseCertificates(System.Text.Encoding.UTF8.GetString(pemChain.Span));
        try
        {
            var now = runtime.Time.GetUtcNow();
            using var chain = CertificateFacts.Assemble(parsed[0], parsed.Skip(1), now);
            if (new DateTimeOffset(chain.Leaf.NotAfter) < now)
            {
                throw new CryptographicException($"The leaf certificate expired at {chain.Leaf.NotAfter.ToUniversalTime():u}; it can't be attached.");
            }

            if (new DateTimeOffset(chain.Leaf.NotBefore) > now + AttachTolerance)
            {
                throw new CryptographicException($"The leaf certificate is not valid before {chain.Leaf.NotBefore.ToUniversalTime():u}; it can't be attached yet.");
            }

            await runtime.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
            using var gate = await runtime.EnterAsync(cancellationToken).ConfigureAwait(false);
            var context = await runtime.CreateContextAsync(cancellationToken).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                var staged = await context.StagedKeys.SingleOrDefaultAsync(s => s.CertificateId == id && s.StagedKeyId == stagedKey, cancellationToken).ConfigureAwait(false)
                    ?? throw new StagedKeyMismatchException(
                        $"{stagedKey} is not the staged key of {id}: it was staged again, replaced by another certificate, or deleted. Start the issuance again.");

                using var keyBytes = EnvelopeCipher.Open(runtime.Kek, staged.Sealed, CertificateSeal.StagedContextOf(staged.CertificateId, staged.StagedKeyId, staged.Names));
                using var key = KeyPairs.Import(keyBytes.Span, chain.Leaf);
                if (!KeyPairs.Matches(key, chain.Leaf))
                {
                    throw new CryptographicException("The leaf certificate's public key is not the staged key's: the chain does not belong to this request.");
                }

                var sans = CertificateFacts.SubjectAlternativeNames(chain.Leaf);
                foreach (var name in staged.Names)
                {
                    if (!sans.Any(san => CertificateNaming.Covers(san, name)))
                    {
                        throw new CryptographicException($"The leaf certificate does not cover the requested name '{name}'.");
                    }
                }

                return await store.PutAsync(context, id, keyBytes, chain, isLocalCaRoot: false, "attached chain", cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            foreach (var certificate in parsed)
            {
                certificate.Dispose();
            }
        }
    }

    public async ValueTask<CertificateInfo> CreateSelfSignedAsync(
        CertificateId id, KeySpec spec, IReadOnlyList<string> names, TimeSpan validity, CancellationToken cancellationToken)
    {
        RequireId(id);
        var checkedNames = CertificateNaming.Normalize(names);
        RequireValidity(validity);
        validity = Min(validity, MaxLeafValidity);

        using var key = KeyPairs.Generate(spec);
        var now = runtime.Time.GetUtcNow();
        using var chain = new CertificateChain([SignServerCertificate(key, checkedNames, KeyPairs.Generator(key), null, now - ClockSkew, now + validity)]);
        return await PutAsync(id, key, chain, isLocalCaRoot: false, "self-signed", cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<CertificateInfo> EnsureLocalCaAsync(
        CertificateId id, KeySpec spec, TimeSpan validity, CancellationToken cancellationToken)
    {
        RequireId(id);
        RequireValidity(validity);
        await runtime.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        // Generated before the gate (RSA keys take a while) and thrown away again if a root exists.
        using var key = KeyPairs.Generate(spec);
        using var gate = await runtime.EnterAsync(cancellationToken).ConfigureAwait(false);
        var context = await runtime.CreateContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var existing = await context.CertificateKeys.AsNoTracking().SingleOrDefaultAsync(k => k.IsLocalCaRoot, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                using var existingChain = new CertificateChain(CertificateFacts.ParseCertificates(existing.ChainPem));
                var existingInfo = CertificateFacts.InfoOf(existingChain);
                return existing.Id == id ? existingInfo : throw new LocalCaExistsException(existing.Id, existingInfo);
            }

            if (await context.CertificateKeys.AnyAsync(k => k.Id == id, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException($"{id} is a certificate already and cannot become the local CA root.");
            }

            var now = runtime.Time.GetUtcNow();
            var name = $"CN=Paduraksa Local CA ({Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))})";
            var request = KeyPairs.NewRequest(new X500DistinguishedName(name), key);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: true, pathLengthConstraint: 0, critical: true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
            using var rootChain = new CertificateChain([request.Create(request.SubjectName, KeyPairs.Generator(key), now - ClockSkew, now + validity, NewSerialNumber())]);
            using var pkcs8 = KeyPairs.ExportPkcs8(key);
            return await store.PutAsync(context, id, pkcs8, rootChain, isLocalCaRoot: true, "local CA root", cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask<CertificateInfo> IssueFromLocalCaAsync(
        CertificateId id, KeySpec spec, IReadOnlyList<string> names, TimeSpan validity, CancellationToken cancellationToken)
    {
        RequireId(id);
        var checkedNames = CertificateNaming.Normalize(names);
        RequireValidity(validity);
        await runtime.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        using var key = KeyPairs.Generate(spec);
        using var gate = await runtime.EnterAsync(cancellationToken).ConfigureAwait(false);
        var context = await runtime.CreateContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var rootRow = await context.CertificateKeys.AsNoTracking().SingleOrDefaultAsync(k => k.IsLocalCaRoot, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("There is no local CA yet: call EnsureLocalCaAsync first.");
            if (rootRow.Id == id)
            {
                throw new InvalidOperationException($"{id} is the local CA root, not a leaf.");
            }

            var rootCertificates = CertificateFacts.ParseCertificates(rootRow.ChainPem);
            using var rootChain = new CertificateChain(rootCertificates);
            var root = rootChain.Leaf;
            using var rootKeyBytes = store.OpenKey(rootRow);
            using var rootKey = KeyPairs.Import(rootKeyBytes.Span, root);

            var now = runtime.Time.GetUtcNow();
            var notBefore = Max(now - ClockSkew, new DateTimeOffset(root.NotBefore));
            var notAfter = Min(now + Min(validity, MaxLeafValidity), new DateTimeOffset(root.NotAfter));
            if (notAfter <= now)
            {
                throw new InvalidOperationException("The local CA root has expired; a new root is needed.");
            }

            using var chain = new CertificateChain([SignServerCertificate(key, checkedNames, KeyPairs.Generator(rootKey), root, notBefore, notAfter, root.SubjectName)]);
            using var pkcs8 = KeyPairs.ExportPkcs8(key);
            return await store.PutAsync(context, id, pkcs8, chain, isLocalCaRoot: false, "issued by the local CA", cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask<bool> DeleteAsync(CertificateId id, CancellationToken cancellationToken)
    {
        await runtime.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        using var gate = await runtime.EnterAsync(cancellationToken).ConfigureAwait(false);
        var context = await runtime.CreateContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            return await store.DeleteAsync(context, id, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<CertificateInfo> PutAsync(
        CertificateId id, AsymmetricAlgorithm key, CertificateChain chain, bool isLocalCaRoot, string how, CancellationToken cancellationToken)
    {
        await runtime.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        using var pkcs8 = KeyPairs.ExportPkcs8(key);
        using var gate = await runtime.EnterAsync(cancellationToken).ConfigureAwait(false);
        var context = await runtime.CreateContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            return await store.PutAsync(context, id, pkcs8, chain, isLocalCaRoot, how, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A server certificate for <paramref name="names"/> over <paramref name="key"/>, signed with <paramref name="generator"/> as <paramref name="issuerName"/> (itself when null).</summary>
    private static X509Certificate2 SignServerCertificate(
        AsymmetricAlgorithm key,
        IReadOnlyList<string> names,
        X509SignatureGenerator generator,
        X509Certificate2? issuer,
        DateTimeOffset notBefore,
        DateTimeOffset notAfter,
        X500DistinguishedName? issuerName = null)
    {
        var subject = CertificateNaming.SubjectOf(names);
        var request = KeyPairs.NewRequest(subject, key);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(KeyPairs.ServerKeyUsage(key), critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([ServerAuthentication], critical: false));
        request.CertificateExtensions.Add(CertificateNaming.SanOf(names, subject));
        var subjectKeyIdentifier = new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false);
        request.CertificateExtensions.Add(subjectKeyIdentifier);
        request.CertificateExtensions.Add(issuer is null
            ? X509AuthorityKeyIdentifierExtension.CreateFromSubjectKeyIdentifier(subjectKeyIdentifier)
            : X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, includeKeyIdentifier: true, includeIssuerAndSerial: false));
        return request.Create(issuerName ?? subject, generator, notBefore, notAfter, NewSerialNumber());
    }

    /// <summary>126 random bits (the top byte's two high bits are fixed to 01): a positive integer whose DER content needs no sign byte.</summary>
    private static byte[] NewSerialNumber()
    {
        var serial = RandomNumberGenerator.GetBytes(SerialNumberLength);
        serial[0] = (byte)(0x40 | (serial[0] & 0x3F));
        return serial;
    }

    private static void RequireId(CertificateId id)
    {
        if (id.IsEmpty)
        {
            throw new ArgumentException("The certificate ID can't be empty.", nameof(id));
        }
    }

    private static void RequireValidity(TimeSpan validity)
    {
        if (validity <= TimeSpan.Zero || validity > MaxValidity)
        {
            throw new ArgumentOutOfRangeException(nameof(validity), validity, "The validity must be positive and at most 30 years.");
        }
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a >= b ? a : b;

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a <= b ? a : b;
}
