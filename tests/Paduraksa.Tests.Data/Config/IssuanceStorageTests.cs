using Microsoft.EntityFrameworkCore;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Data.Certificates;
using Paduraksa.Data.Issuance;
using static Paduraksa.Tests.Data.Config.TestConfigDatabase;

namespace Paduraksa.Tests.Data.Config;

public sealed class IssuanceStorageTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AccountsOrdersAndProvidersRoundTripWithStableNames()
    {
        await using var db = await CreateAsync();
        var (account, provider, certificate, order) = Graph();
        order.StagedKeyId = StagedKeyId.New();
        order.Csr = [1, 2, 3];
        order.AriWindowStart = T0.AddDays(40);
        order.AriWindowEnd = T0.AddDays(41);
        await SaveAsync(db, account, provider, certificate, order);

        Assert.Equal(["active", "ES384"], (await db.QueryAsync("SELECT Status, KeyAlgorithm FROM AcmeAccounts")).Single());
        Assert.Equal(["cloudflare"], (await db.QueryAsync("SELECT Kind FROM DnsProviders")).Single());
        Assert.Equal(["ordered", "dns-01", "rsa-3072"], (await db.QueryAsync("SELECT Status, ChallengeType, KeySpec FROM AcmeOrders")).Single());

        await using var context = db.NewContext();
        var read = await context.AcmeOrders.SingleAsync(Ct);
        Assert.Equal(order.Names, read.Names);
        Assert.Equal((order.StagedKeyId, order.AriWindowEnd, order.DnsProviderId), (read.StagedKeyId, read.AriWindowEnd, read.DnsProviderId));
        Assert.Equal([1, 2, 3], read.Csr);
        Assert.Equal(["mailto:ops@example.com"], (await context.AcmeAccounts.SingleAsync(Ct)).Contacts);
    }

    [Fact]
    public async Task UnknownNamesAreRefused()
    {
        await using var db = await CreateAsync();
        var (account, provider, certificate, order) = Graph();
        await SaveAsync(db, account, provider, certificate, order);

        await AssertRefused(db, "UPDATE AcmeAccounts SET Status = 'pending'", "CK_AcmeAccounts_Status");
        await AssertRefused(db, "UPDATE AcmeAccounts SET KeyAlgorithm = 'HS256'", "CK_AcmeAccounts_KeyAlgorithm");
        await AssertRefused(db, "UPDATE DnsProviders SET Kind = 'route53'", "CK_DnsProviders_Kind");
        await AssertRefused(db, "UPDATE AcmeOrders SET Status = 'done'", "CK_AcmeOrders_Status");
        await AssertRefused(db, "UPDATE AcmeOrders SET ChallengeType = 'tls-alpn-01'", "CK_AcmeOrders_ChallengeType");
        await AssertRefused(db, "UPDATE AcmeOrders SET KeySpec = 'dsa'", "CK_AcmeOrders_KeySpec");
        // DNS-01 needs a provider; HTTP-01 may have none.
        await AssertRefused(db, "UPDATE AcmeOrders SET DnsProviderId = NULL", "CK_AcmeOrders_DnsProvider");
        await db.ExecuteAsync("UPDATE AcmeOrders SET DnsProviderId = NULL, ChallengeType = 'http-01'");
    }

    [Fact]
    public async Task ACertificateHasOneOrderRowAndItGoesWithTheCertificate()
    {
        await using var db = await CreateAsync();
        var (account, provider, certificate, order) = Graph();
        await SaveAsync(db, account, provider, certificate, order);

        var second = Graph().Order;
        second.CertificateId = certificate.Id;
        second.AccountId = account.Id;
        second.ChallengeType = AcmeChallengeType.Http01;
        second.DnsProviderId = null;
        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(db, second));

        await db.ExecuteAsync("DELETE FROM Certificates");
        Assert.Equal(0L, await db.ScalarAsync("SELECT count(*) FROM AcmeOrders"));
    }

    [Fact]
    public async Task AnAccountOrProviderInUseCannotBeDeleted()
    {
        await using var db = await CreateAsync();
        var (account, provider, certificate, order) = Graph();
        await SaveAsync(db, account, provider, certificate, order);

        await Assert.ThrowsAnyAsync<Exception>(() => db.ExecuteAsync("DELETE FROM AcmeAccounts"));
        await Assert.ThrowsAnyAsync<Exception>(() => db.ExecuteAsync("DELETE FROM DnsProviders"));
        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(db, Graph().Order));
    }

    [Fact]
    public async Task TheTablesHoldNoSecretColumn()
    {
        await using var db = await CreateAsync();
        string[] tables = ["AcmeAccounts", "AcmeOrders", "DnsProviders"];
        foreach (var table in tables)
        {
            var columns = (await db.QueryAsync($"SELECT name FROM pragma_table_info('{table}')")).Select(r => (string)r[0]!).ToList();
            Assert.DoesNotContain(columns, c => c.Contains("Token", StringComparison.OrdinalIgnoreCase) || c.Contains("Password", StringComparison.OrdinalIgnoreCase) || c.Contains("Hmac", StringComparison.OrdinalIgnoreCase));
        }

        // The only references into the vault are secret and staged-key IDs.
        Assert.Contains("CredentialSecretId", (await db.QueryAsync("SELECT name FROM pragma_table_info('DnsProviders')")).Select(r => (string)r[0]!));
    }

    private static async Task AssertRefused(TestConfigDatabase db, string sql, string constraint)
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => db.ExecuteAsync(sql));
        Assert.Contains(constraint, ex.Message, StringComparison.Ordinal);
    }

    private static (AcmeAccountRow Account, DnsProviderRow Provider, CertificateRow Certificate, AcmeOrderRow Order) Graph()
    {
        AcmeAccountRow account = new()
        {
            Id = AcmeAccountId.New(),
            DirectoryUrl = "https://acme.test/directory",
            Contacts = ["mailto:ops@example.com"],
            Status = AcmeAccountStatus.Active,
            AccountUrl = "https://acme.test/acct/1",
            KeyAlgorithm = JwsAlgorithm.ES384,
            TermsOfServiceAgreed = true,
            CreatedAt = T0,
            UpdatedAt = T0,
        };
        DnsProviderRow provider = new()
        {
            Id = DnsProviderId.New(),
            Kind = DnsProviderKind.Cloudflare,
            Label = "cf",
            CredentialSecretId = SecretId.New(),
            CreatedAt = T0,
            UpdatedAt = T0,
        };
        CertificateRow certificate = new()
        {
            Id = CertificateId.New(),
            Source = CertificateSource.Acme,
            Status = CertificateStatus.Pending,
            NotBefore = T0,
            NotAfter = T0,
            CreatedAt = T0,
            UpdatedAt = T0,
        };
        AcmeOrderRow order = new()
        {
            Id = AcmeOrderId.New(),
            CertificateId = certificate.Id,
            AccountId = account.Id,
            Status = AcmeOrderStatus.Ordered,
            ChallengeType = AcmeChallengeType.Dns01,
            DnsProviderId = provider.Id,
            KeySpec = KeySpec.Rsa3072,
            Names = ["example.com", "*.example.com"],
            CreatedAt = T0,
            UpdatedAt = T0,
        };
        return (account, provider, certificate, order);
    }

    private static async Task SaveAsync(TestConfigDatabase db, params object[] entities)
    {
        await using var context = db.NewContext();
        context.AddRange(entities);
        await context.SaveChangesAsync(Ct);
    }
}
