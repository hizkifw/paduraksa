using Microsoft.EntityFrameworkCore;
using Paduraksa.Core.Model;
using Paduraksa.Data.Certificates;
using static Paduraksa.Tests.Data.Config.TestConfigDatabase;

namespace Paduraksa.Tests.Data.Config;

/// <summary>The certificate inventory tables (PLAN §5.1, §9.1): stable names, the name index, public data only.</summary>
public sealed class CertificateStorageTests
{
    [Fact]
    public async Task EnumsAreStoredByStableNameAndRoundTrip()
    {
        await using var db = await CreateAsync();
        var row = Row(CertificateSource.LocalCaRoot, CertificateStatus.Failed);
        await SaveAsync(db, row);

        Assert.Equal(["local_ca_root", "failed"], (await db.QueryAsync("SELECT Source, Status FROM Certificates")).Single());
        await using var context = db.NewContext();
        var read = await context.Certificates.SingleAsync(Ct);
        Assert.Equal((CertificateSource.LocalCaRoot, CertificateStatus.Failed, row.NotAfter), (read.Source, read.Status, read.NotAfter));
    }

    [Fact]
    public async Task UnknownSourceAndStatusNamesAreRefused()
    {
        await using var db = await CreateAsync();
        await SaveAsync(db, Row(CertificateSource.Acme, CertificateStatus.Active));

        var source = await Assert.ThrowsAnyAsync<Exception>(() => db.ExecuteAsync("UPDATE Certificates SET Source = 'imported'"));
        Assert.Contains("CK_Certificates_Source", source.Message, StringComparison.Ordinal);
        var status = await Assert.ThrowsAnyAsync<Exception>(() => db.ExecuteAsync("UPDATE Certificates SET Status = 'expired'"));
        Assert.Contains("CK_Certificates_Status", status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NamesAreIndexedAndUniquePerCertificateIgnoringCase()
    {
        await using var db = await CreateAsync();
        var row = Row(CertificateSource.Acme, CertificateStatus.Active);
        await SaveAsync(db, row, new CertificateNameRow { CertificateId = row.Id, Name = "app.example.com" });

        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(db, new CertificateNameRow { CertificateId = row.Id, Name = "APP.example.com" }));
        Assert.Contains("IX_CertificateNames_Name", (await db.QueryAsync("PRAGMA index_list('CertificateNames')")).Select(r => (string)r[1]!));
    }

    [Fact]
    public async Task NamesNeedACertificateAndGoWithIt()
    {
        await using var db = await CreateAsync();
        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(db, new CertificateNameRow { CertificateId = CertificateId.New(), Name = "orphan.example.com" }));

        var row = Row(CertificateSource.Uploaded, CertificateStatus.Active);
        await SaveAsync(db, row, new CertificateNameRow { CertificateId = row.Id, Name = "a.example.com" }, new CertificateNameRow { CertificateId = row.Id, Name = "*.example.com" });
        await db.ExecuteAsync("DELETE FROM Certificates");

        Assert.Equal(0L, await db.ScalarAsync("SELECT count(*) FROM CertificateNames"));
    }

    [Fact]
    public async Task TheTablesHoldNoKeyMaterialColumn()
    {
        await using var db = await CreateAsync();
        var columns = (await db.QueryAsync("SELECT name FROM pragma_table_info('Certificates')")).Select(r => (string)r[0]!).ToList();

        Assert.DoesNotContain(columns, c => c.Contains("key", StringComparison.OrdinalIgnoreCase) && c != "KeyAlgorithm" && c != "AuthorityKeyIdentifier");
        Assert.Contains("PublicChainPem", columns);
    }

    private static CertificateRow Row(CertificateSource source, CertificateStatus status) => new()
    {
        Id = CertificateId.New(),
        Source = source,
        Status = status,
        Subject = "CN=app.example.com",
        Issuer = "CN=Test CA",
        SerialNumber = "00AB",
        Sha256Fingerprint = new string('a', 64),
        KeyAlgorithm = "ECDSA P-256",
        NotBefore = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        NotAfter = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero),
        PublicChainPem = "-----BEGIN CERTIFICATE-----\n-----END CERTIFICATE-----\n",
        CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        UpdatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
    };

    private static async Task SaveAsync(TestConfigDatabase db, params object[] entities)
    {
        await using var context = db.NewContext();
        context.AddRange(entities);
        await context.SaveChangesAsync(Ct);
    }
}
