using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using Paduraksa.Admin.DataProtection;
using Paduraksa.Core.Vault;
using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Hosting;

namespace Paduraksa.Tests.Integration.Admin;

/// <summary>
/// Data Protection in the admin app with its key ring in the vault: protect in one host, unprotect after a
/// restart over the same data directory, and a wrong master key fails exactly like the rest of the vault. Also holds the
/// registration to the admin host and to the vault repository (never the file-system one, never an XML encryptor).
/// </summary>
public sealed class DataProtectionTests
{
    private const string Purpose = "paduraksa-admin.tests";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OnlyTheAdminHostUsesTheVaultRepository()
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

            // The capability itself is not in any container: only the IXmlRepository above holds it.
            Assert.Null(admin.GetService<IDataProtectionKeyStore>());

            // YARP registers Data Protection in the public app for session affinity, but its destination ID is not a secret:
            // an in-memory ephemeral provider, no vault repository and no key-ring warm-up that could create a key.
            IServiceProvider publicServices = host.GetServices(HostRole.Public);
            Assert.IsType<EphemeralDataProtectionProvider>(publicServices.GetRequiredService<IDataProtectionProvider>());
            Assert.DoesNotContain(
                publicServices.GetServices<IHostedService>(),
                service => string.Equals(service.GetType().FullName, EphemeralDataProtection.HostedServiceTypeName, StringComparison.Ordinal));
            Assert.Null(publicServices.GetService<IDataProtectionKeyStore>());

            Assert.Null(host.GetServices(HostRole.Core).GetService<IDataProtectionProvider>());
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ThePublicAppsProtectorWritesNothingToTheVaultAndCannotOpenTheAdminsPayload()
    {
        string directory = TestDataDirectories.NewPath();
        await using PaduraksaHost host = TestHost.Create(dataDirectory: directory);
        await host.StartAsync(Ct);
        try
        {
            int before = (await host.Shared.KeyRing.ReadAllAsync(Ct)).Count;
            IDataProtectionProvider publicProvider = host.GetServices(HostRole.Public).GetRequiredService<IDataProtectionProvider>();

            string affinity = publicProvider.CreateProtector("yarp-affinity").Protect("destination-1");
            Assert.Equal("destination-1", publicProvider.CreateProtector("yarp-affinity").Unprotect(affinity));

            // The two providers have different keys and purposes: neither opens the other's payload.
            Assert.ThrowsAny<CryptographicException>(() => AdminProtector(host, "yarp-affinity").Unprotect(affinity));
            string adminPayload = AdminProtector(host).Protect("the dashboard's antiforgery state");
            Assert.ThrowsAny<CryptographicException>(() => publicProvider.CreateProtector(Purpose).Unprotect(adminPayload));

            // The public app read nothing and wrote nothing to the vault, and no key reached the disk.
            Assert.Equal(before, (await host.Shared.KeyRing.ReadAllAsync(Ct)).Count);
            Assert.Empty(Directory.GetFiles(directory, "*.xml", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetDirectories(directory, "DataProtection-Keys", SearchOption.AllDirectories));
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
        HashSet<string> storedElements;

        await using (PaduraksaHost first = TestHost.Create(dataDirectory: directory))
        {
            await first.StartAsync(Ct);
            try
            {
                protectedValue = AdminProtector(first).Protect("the dashboard's circuit and antiforgery state");
                var keys = await first.Shared.KeyRing.ReadAllAsync(Ct);
                Assert.NotEmpty(keys);
                Assert.All(keys, key => Assert.Contains("<key", key.KeyXml, StringComparison.Ordinal));
                storedElements = [.. keys.Select(key => key.KeyXml)];
            }
            finally
            {
                await first.StopAsync(CancellationToken.None);
            }
        }

        // The key elements are in the vault, sealed: no plain key XML (and no key directory) is on disk.
        Assert.Empty(Directory.GetDirectories(directory, "DataProtection-Keys", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(directory, "*.xml", SearchOption.AllDirectories));
        foreach (string file in Directory.GetFiles(directory, "*.db*", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain("</key>", Encoding.UTF8.GetString(File.ReadAllBytes(file)), StringComparison.Ordinal);
        }

        await using (PaduraksaHost second = TestHost.Create(dataDirectory: directory))
        {
            await second.StartAsync(Ct);
            try
            {
                string unprotected = AdminProtector(second).Unprotect(protectedValue);
                Assert.Equal("the dashboard's circuit and antiforgery state", unprotected);

                // The same elements came back out of the vault, unchanged.
                var keys = await second.Shared.KeyRing.ReadAllAsync(Ct);
                Assert.Superset(storedElements, new HashSet<string>(keys.Select(key => key.KeyXml)));

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
    public async Task TheFrameworkWarningIsFilteredAndTheVaultLogsItsOwnLine()
    {
        LogCapture logs = new();
        await using PaduraksaHost host = TestHost.Create(
            dataDirectory: TestDataDirectories.NewPath(),
            settings: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                // The test's default level is Warning; the vault's own line is Information.
                ["Logging:LogLevel:Paduraksa.Vault.DataProtection.DataProtectionKeyStore"] = "Information",
            },
            logs: logs);
        await host.StartAsync(Ct);
        try
        {
            AdminProtector(host).Protect("a value");

            Assert.DoesNotContain(logs.Entries, e => e.Message.Contains("No XML encryptor configured", StringComparison.Ordinal));
            Assert.Contains(
                logs.Entries,
                e => e.Level == Microsoft.Extensions.Logging.LogLevel.Information
                    && e.Message.Contains("sealed in the vault", StringComparison.Ordinal));
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
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
