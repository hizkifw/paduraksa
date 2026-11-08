using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Paduraksa.Admin.DataProtection;
using Paduraksa.Core.Vault;
using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Hosting;

namespace Paduraksa.Tests.Integration.Admin;

/// <summary>
/// Data Protection in the admin app with its key ring in the vault (PLAN §8.1, §8.2): protect in one host, unprotect after a
/// restart over the same data directory, and a wrong master key fails exactly like the rest of the vault. Also holds the
/// registration to the admin host and to the vault repository (never the file-system one, never an XML encryptor).
/// </summary>
public sealed class DataProtectionTests
{
    private const string Purpose = "paduraksa-admin.tests";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheAdminHostHasDataProtectionOverTheVaultAndTheOtherHostsDoNot()
    {
        string directory = TestDataDirectories.NewPath();
        await using PaduraksaHost host = TestHost.Create(dataDirectory: directory);
        await host.StartAsync(Ct);
        try
        {
            IServiceProvider admin = host.GetServices(HostRole.Admin);
            Assert.NotNull(admin.GetService<IDataProtectionProvider>());
            Assert.IsType<VaultXmlRepository>(KeyManagement(admin).XmlRepository);
            Assert.Null(KeyManagement(admin).XmlEncryptor);

            // The public proxy does not use the vault's ring: whatever the generic web host registers by default must not be
            // our repository (and nothing in the public pipeline resolves a protector).
            IServiceProvider publicServices = host.GetServices(HostRole.Public);
            Assert.NotNull(publicServices.GetService<IDataProtectionProvider>());
            Assert.IsNotType<VaultXmlRepository>(
                publicServices.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository);
            Assert.Null(host.GetServices(HostRole.Core).GetService<IDataProtectionProvider>());
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ProtectsAndUnprotectsAcrossARestartFromTheVault()
    {
        string directory = TestDataDirectories.NewPath();
        string protectedValue;
        string storedElement;

        await using (PaduraksaHost first = TestHost.Create(dataDirectory: directory))
        {
            await first.StartAsync(Ct);
            try
            {
                protectedValue = AdminProtector(first).Protect("the dashboard's circuit and antiforgery state");
                var keys = await first.GetServices(HostRole.Admin).GetRequiredService<IDataProtectionKeyStore>().ReadAllAsync(Ct);
                storedElement = Assert.Single(keys).KeyXml;
                Assert.Contains("<key", storedElement, StringComparison.Ordinal);
            }
            finally
            {
                await first.StopAsync(CancellationToken.None);
            }
        }

        // The key element is in the vault, sealed: no plain key XML (and no key directory) is on disk.
        Assert.False(Directory.Exists(Path.Combine(directory, "DataProtection-Keys")), "Data Protection wrote a key directory.");
        foreach (string file in Directory.GetFiles(directory))
        {
            Assert.DoesNotContain("</key>", File.ReadAllText(file), StringComparison.Ordinal);
        }

        await using (PaduraksaHost second = TestHost.Create(dataDirectory: directory))
        {
            await second.StartAsync(Ct);
            try
            {
                string unprotected = AdminProtector(second).Unprotect(protectedValue);
                Assert.Equal("the dashboard's circuit and antiforgery state", unprotected);

                // The same element came back out of the vault, unchanged.
                var keys = await second.GetServices(HostRole.Admin).GetRequiredService<IDataProtectionKeyStore>().ReadAllAsync(Ct);
                Assert.Equal(storedElement, Assert.Single(keys).KeyXml);

                // A protector with another purpose does not open it.
                Assert.ThrowsAny<CryptographicException>(() => AdminProtector(second, "another-purpose").Unprotect(protectedValue));
            }
            finally
            {
                await second.StopAsync(CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task AWrongMasterKeyStopsTheProcessLikeTheRestOfTheVault()
    {
        string directory = TestDataDirectories.NewPath();
        await using (PaduraksaHost first = TestHost.Create(dataDirectory: directory))
        {
            await first.StartAsync(Ct);
            try
            {
                AdminProtector(first).Protect("a value");
            }
            finally
            {
                await first.StopAsync(CancellationToken.None);
            }
        }

        // The vault generated master.key; replace it with a key the database was not written under.
        string keyPath = Path.Combine(directory, "master.key");
        Assert.True(File.Exists(keyPath), "The vault did not generate master.key.");
        await File.WriteAllTextAsync(keyPath, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) + "\n", Ct);

        await using PaduraksaHost second = TestHost.Create(dataDirectory: directory);
        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() => second.StartAsync(Ct));
        Assert.Contains("master key", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static IDataProtector AdminProtector(PaduraksaHost host, string purpose = Purpose) =>
        host.GetServices(HostRole.Admin).GetRequiredService<IDataProtectionProvider>().CreateProtector(purpose);

    private static KeyManagementOptions KeyManagement(IServiceProvider admin) => admin.GetRequiredService<IOptions<KeyManagementOptions>>().Value;
}
