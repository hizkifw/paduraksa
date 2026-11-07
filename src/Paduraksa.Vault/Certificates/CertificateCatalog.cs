using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Vault.Persistence;

namespace Paduraksa.Vault.Certificates;

/// <summary>
/// <see cref="ICertificateCatalog"/>: the vault describes the certificates it holds from the public chain stored beside each key.
/// No key is opened and the gate is not taken: the chain is public, and a replacement writes key and chain in one transaction,
/// so a read sees the old pair's chain or the new pair's, never a mix. A row whose chain cannot be read is left out of a listing
/// (logged), so one damaged row does not hide the others; <see cref="DescribeAsync"/> of it is null for the same reason.
/// </summary>
internal sealed partial class CertificateCatalog(VaultRuntime runtime, ILogger<CertificateCatalog> logger) : ICertificateCatalog
{
    public async ValueTask<IReadOnlyList<VaultCertificate>> ListAsync(CancellationToken cancellationToken)
    {
        await runtime.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        var context = await runtime.CreateContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var rows = await Rows(context, null).ToListAsync(cancellationToken).ConfigureAwait(false);
            return [.. rows.Select(TryDescribe).OfType<VaultCertificate>()];
        }
    }

    public async ValueTask<VaultCertificate?> DescribeAsync(CertificateId id, CancellationToken cancellationToken)
    {
        await runtime.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        var context = await runtime.CreateContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var row = await Rows(context, id).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            return row is null ? null : TryDescribe(row);
        }
    }

    /// <summary>The public columns only: the projection leaves the sealed key where it is (a test pins that the query never names it).</summary>
    internal static IQueryable<CatalogRow> Rows(VaultDbContext context, CertificateId? id) =>
        context.CertificateKeys.AsNoTracking()
            .Where(k => id == null || k.Id == id)
            .Select(k => new CatalogRow(k.Id, k.IsLocalCaRoot, k.ChainPem));

    private VaultCertificate? TryDescribe(CatalogRow row)
    {
        try
        {
            using var chain = new CertificateChain(CertificateFacts.ParseCertificates(row.ChainPem));
            return new VaultCertificate(row.Id, row.IsLocalCaRoot, CertificateFacts.InfoOf(chain));
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or FormatException)
        {
            LogUnreadable(row.Id, ex.GetType().Name);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Certificate {Certificate} is in the vault but its stored chain cannot be read ({ErrorType}); it is left out of the catalog.")]
    private partial void LogUnreadable(CertificateId certificate, string errorType);

    internal sealed record CatalogRow(CertificateId Id, bool IsLocalCaRoot, string ChainPem);
}
