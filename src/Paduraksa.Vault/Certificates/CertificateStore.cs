using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Vault.Crypto;
using Paduraksa.Vault.Persistence;

namespace Paduraksa.Vault.Certificates;

/// <summary>
/// The one place certificate keys are written and read (<see cref="CertificateKeyRow"/>), shared by issuance, import and the
/// context source. Every method that changes or opens a key runs inside <see cref="VaultRuntime"/>'s gate, which is what keeps a
/// replacement, a KEK rotation and a context load from interleaving.
/// </summary>
/// <remarks>
/// <b>Atomic replacement.</b> <see cref="PutAsync"/> writes the new key and chain into the one row of the ID and drops the
/// ID's staged key in one <c>SaveChanges</c>, i.e. one transaction: a reader sees the old pair or the new pair, and a failure
/// leaves the old pair in place. The row's version, part of the key's AAD and a concurrency token, moves with every replacement.
/// <para>
/// <b>The cache.</b> Serving contexts are built once per certificate version and kept (<see cref="CertificateContexts"/>), so a handshake
/// path never touches the database and the proxy gets the same instance back; <see cref="PutAsync"/> and <see cref="DeleteAsync"/>
/// drop the entry inside the gate, and a load takes the gate, so a context built from a replaced row can never be cached
/// after the replacement.
/// </para>
/// </remarks>
internal sealed partial class CertificateStore(VaultRuntime runtime, ILogger<CertificateStore> logger)
{
    private readonly ConcurrentDictionary<CertificateId, SslStreamCertificateContext> _contexts = new();

    /// <summary>The gate-held lookup of a cached context.</summary>
    public bool TryGetContext(CertificateId id, out SslStreamCertificateContext? context) => _contexts.TryGetValue(id, out context);

    /// <summary>Caches a freshly built context. Call inside the gate.</summary>
    public SslStreamCertificateContext CacheContext(CertificateId id, SslStreamCertificateContext context) => _contexts.GetOrAdd(id, context);

    /// <summary>
    /// Makes <paramref name="pkcs8"/> and <paramref name="chain"/> the certificate of <paramref name="id"/>, replacing the one
    /// that is there, and discards the ID's staged key. Call inside the gate, after <see cref="VaultRuntime.EnsureReadyAsync"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The ID is the local CA root and this is not (or the other way round): the root is never replaced this way.</exception>
    public async Task<CertificateInfo> PutAsync(
        VaultDbContext context,
        CertificateId id,
        SecretBuffer pkcs8,
        CertificateChain chain,
        bool isLocalCaRoot,
        string how,
        CancellationToken cancellationToken)
    {
        var info = CertificateFacts.InfoOf(chain);
        var transaction = await runtime.BeginSealingAsync(context, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var row = await context.CertificateKeys.SingleOrDefaultAsync(k => k.Id == id, cancellationToken).ConfigureAwait(false);
            var now = runtime.Time.GetUtcNow();
            if (row is null)
            {
                row = new CertificateKeyRow { Id = id, IsLocalCaRoot = isLocalCaRoot, Version = 1, CreatedAt = now };
                context.CertificateKeys.Add(row);
            }
            else if (row.IsLocalCaRoot != isLocalCaRoot)
            {
                throw new InvalidOperationException($"{id} is the local CA root, which a replacement cannot touch; rotate it instead.");
            }
            else
            {
                row.Version++;
            }

            row.ChainPem = info.PublicChainPem;
            row.UpdatedAt = now;
            row.Sealed = EnvelopeCipher.Seal(runtime.Kek, pkcs8.Span, CertificateSeal.ContextOf(id, isLocalCaRoot, row.Version));

            var staged = await context.StagedKeys.SingleOrDefaultAsync(s => s.CertificateId == id, cancellationToken).ConfigureAwait(false);
            if (staged is not null)
            {
                context.StagedKeys.Remove(staged);
            }

            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        _contexts.TryRemove(id, out _);
        LogStored(id, how, info.Sha256Fingerprint);
        return info;
    }

    /// <summary>
    /// Replaces the local CA root <paramref name="oldId"/> with <paramref name="chain"/> and <paramref name="pkcs8"/> under <paramref name="newId"/>,
    /// the old row deleted in the same transaction. Call inside the gate, after <see cref="VaultRuntime.EnsureReadyAsync"/>.
    /// </summary>
    public async Task<CertificateInfo> ReplaceLocalCaAsync(
        VaultDbContext context, CertificateId oldId, CertificateId newId, SecretBuffer pkcs8, CertificateChain chain, CancellationToken cancellationToken)
    {
        var info = CertificateFacts.InfoOf(chain);
        var transaction = await runtime.BeginSealingAsync(context, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var now = runtime.Time.GetUtcNow();
            _ = await context.CertificateKeys.Where(k => k.Id == oldId && k.IsLocalCaRoot).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            _ = await context.StagedKeys.Where(s => s.CertificateId == oldId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            CertificateKeyRow row = new() { Id = newId, IsLocalCaRoot = true, Version = 1, CreatedAt = now, UpdatedAt = now, ChainPem = info.PublicChainPem };
            row.Sealed = EnvelopeCipher.Seal(runtime.Kek, pkcs8.Span, CertificateSeal.ContextOf(newId, isLocalCaRoot: true, row.Version));
            context.CertificateKeys.Add(row);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        _contexts.TryRemove(oldId, out _);
        LogStored(newId, "local CA rotation", info.Sha256Fingerprint);
        return info;
    }

    /// <summary>Deletes the key, chain and staged key of <paramref name="id"/>. Call inside the gate. False if there was none.</summary>
    public async Task<bool> DeleteAsync(VaultDbContext context, CertificateId id, CancellationToken cancellationToken)
    {
        var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var keys = await context.CertificateKeys.Where(k => k.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            var staged = await context.StagedKeys.Where(s => s.CertificateId == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _contexts.TryRemove(id, out _);
            if (keys > 0)
            {
                LogDeleted(id);
            }

            return keys + staged > 0;
        }
    }

    /// <summary>Opens a certificate's key. Call inside the gate. The caller disposes the buffer.</summary>
    public SecretBuffer OpenKey(CertificateKeyRow row) =>
        EnvelopeCipher.Open(runtime.Kek, row.Sealed, CertificateSeal.ContextOf(row.Id, row.IsLocalCaRoot, row.Version));

    /// <summary>
    /// Builds the serving context of a row: the leaf with its key and the intermediates, chain built offline. The context owns
    /// the leaf with its key from here on and is never disposed (see <see cref="IServerCertificateSource"/>).
    /// </summary>
    public SslStreamCertificateContext BuildContext(CertificateKeyRow row)
    {
        var certificates = CertificateFacts.ParseCertificates(row.ChainPem);
        try
        {
            using var keyBytes = OpenKey(row);
            using var key = KeyPairs.Import(keyBytes.Span, certificates[0]);
            var leaf = KeyPairs.CopyWithKey(certificates[0], key);
            try
            {
                leaf = ForPlatform(leaf);
                X509Certificate2Collection intermediates = [.. certificates.Skip(1)];
                var context = SslStreamCertificateContext.Create(leaf, intermediates, offline: true);

                // The context holds its own copies of the intermediates (from its chain build); these were only the lookup store.
                foreach (var intermediate in certificates.Skip(1))
                {
                    intermediate.Dispose();
                }

                return context;
            }
            catch
            {
                leaf.Dispose();
                throw;
            }
        }
        finally
        {
            certificates[0].Dispose();
        }
    }

    /// <summary>
    /// Windows' SChannel can't serve TLS from an ephemeral CNG key, so there the certificate takes a round trip through PKCS#12
    /// (inside the vault, the one place allowed to). Elsewhere it is returned as it is.
    /// </summary>
    private static X509Certificate2 ForPlatform(X509Certificate2 certificate)
    {
        if (!OperatingSystem.IsWindows())
        {
            return certificate;
        }

        using (certificate)
        {
            var pfx = certificate.ExportPkcs12(Pkcs12ExportPbeParameters.Default, string.Empty);
            try
            {
                return X509CertificateLoader.LoadPkcs12(pfx, string.Empty);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pfx);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Certificate {Certificate} stored ({How}, SHA-256 {Fingerprint}).")]
    private partial void LogStored(CertificateId certificate, string how, string fingerprint);

    [LoggerMessage(Level = LogLevel.Information, Message = "Certificate {Certificate} deleted from the vault, with its key.")]
    private partial void LogDeleted(CertificateId certificate);
}

/// <summary>
/// <see cref="IServerCertificateSource"/> and <see cref="IClientCertificateSource"/>: the two sanctioned egresses of a
/// certificate with its key, one implementation because both hand out the same prebuilt context. The same instance comes back
/// for an unchanged certificate (the proxy deduplicates by instance); a replaced or deleted one is dropped, never disposed.
/// </summary>
/// <remarks>
/// The local CA root is not served: its key is for signing only, so a load by its ID returns null.
/// </remarks>
internal sealed class CertificateContexts(VaultRuntime runtime, CertificateStore store) : IServerCertificateSource, IClientCertificateSource
{
    public ValueTask<SslStreamCertificateContext?> LoadForServingAsync(CertificateId id, CancellationToken cancellationToken) => LoadAsync(id, cancellationToken);

    public ValueTask<SslStreamCertificateContext?> LoadForUpstreamAsync(CertificateId id, CancellationToken cancellationToken) => LoadAsync(id, cancellationToken);

    private async ValueTask<SslStreamCertificateContext?> LoadAsync(CertificateId id, CancellationToken cancellationToken)
    {
        if (store.TryGetContext(id, out var cached))
        {
            return cached;
        }

        await runtime.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        using var gate = await runtime.EnterAsync(cancellationToken).ConfigureAwait(false);

        // A replacement or a load that ran while this one waited for the gate.
        if (store.TryGetContext(id, out cached))
        {
            return cached;
        }

        var context = await runtime.CreateContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var row = await context.CertificateKeys.AsNoTracking().SingleOrDefaultAsync(k => k.Id == id, cancellationToken).ConfigureAwait(false);
            return row is null || row.IsLocalCaRoot ? null : store.CacheContext(id, store.BuildContext(row));
        }
    }
}
