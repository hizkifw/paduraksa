using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Paduraksa.Core.Vault;
using Paduraksa.Tests.Vault.Testing;
using Paduraksa.Vault;
using Paduraksa.Vault.Crypto;

namespace Paduraksa.Tests.Vault;

public sealed class VaultLifecycleTests : IDisposable
{
    private readonly VaultHarness _vault = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _vault.Dispose();

    [Fact]
    public async Task AnEnvironmentKeyGivesAStatusWithoutAWarning()
    {
        var status = await _vault.Get<IVaultLifecycle>().InitializeAsync(Ct);

        Assert.Equal(new VaultStatus(KekSourceKind.Environment, false), status);
        Assert.False(File.Exists(_vault.Data.File("master.key")));
        Assert.True(File.Exists(_vault.Data.File("vault.db")));
    }

    [Fact]
    public async Task WithNoKeyConfiguredAMasterKeyIsGeneratedAndTheStatusSaysItIsBesideTheDatabase()
    {
        _vault.Variables.Clear();

        var status = await _vault.Get<IVaultLifecycle>().InitializeAsync(Ct);

        Assert.Equal(new VaultStatus(KekSourceKind.GeneratedFile, true), status);
        Assert.True(File.Exists(_vault.Data.File("master.key")));

        // The next start reuses it.
        _vault.Restart();
        Assert.Equal(status, await _vault.Get<IVaultLifecycle>().InitializeAsync(Ct));
    }

    [Fact]
    public async Task AKeyFileInTheDataDirectoryIsFlaggedAndOneElsewhereIsNot()
    {
        using var elsewhere = new TempDirectory();
        var key = _vault.Variables[KekProvider.KeyVariable]!;
        var near = _vault.Data.File("near.key");
        File.WriteAllText(near, key);
        _vault.Variables.Clear();
        _vault.Variables[KekProvider.KeyFileVariable] = near;

        Assert.Equal(new VaultStatus(KekSourceKind.File, true), await _vault.Get<IVaultLifecycle>().InitializeAsync(Ct));

        var far = elsewhere.File("far.key");
        File.WriteAllText(far, key);
        _vault.Variables[KekProvider.KeyFileVariable] = far;
        _vault.Restart();
        Assert.Equal(new VaultStatus(KekSourceKind.File, false), await _vault.Get<IVaultLifecycle>().InitializeAsync(Ct));
    }

    [Fact]
    public async Task AWrongKekStopsStartupAndTheRightOneStillWorks()
    {
        var right = _vault.Variables[KekProvider.KeyVariable]!;
        await _vault.Get<ISecretStore>().PutAsync(SecretKind.HmacSigningKey, "value"u8.ToArray(), new(null, MacAlgorithm.HmacSha256, null), Ct);
        _vault.Restart();
        using (var other = Kek.Generate())
        {
            _vault.Variables[KekProvider.KeyVariable] = other.Encode();
        }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await _vault.Get<IVaultLifecycle>().InitializeAsync(Ct));
        Assert.Contains("does not match", ex.Message, StringComparison.Ordinal);

        // Every capability fails the same way instead of failing later at a secret.
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await _vault.Get<ISecretStore>().DescribeAsync(Paduraksa.Core.Model.SecretId.New(), Ct));

        _vault.Variables[KekProvider.KeyVariable] = right;
        _vault.Restart();
        await _vault.Get<IVaultLifecycle>().InitializeAsync(Ct);
    }

    [Fact]
    public async Task AMalformedKeyStopsStartup()
    {
        _vault.Variables[KekProvider.KeyVariable] = "not-a-key";

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await _vault.Get<IVaultLifecycle>().InitializeAsync(Ct));
    }

    [Fact]
    public async Task InitializeIsIdempotentAndAFirstUseInitializesToo()
    {
        var lifecycle = _vault.Get<IVaultLifecycle>();

        var found = await _vault.Get<ISecretStore>().DescribeAsync(Paduraksa.Core.Model.SecretId.New(), Ct);

        Assert.Null(found);
        Assert.Equal(await lifecycle.InitializeAsync(Ct), await lifecycle.InitializeAsync(Ct));
    }

    [Fact]
    public async Task InitializeRunsOnceUnderConcurrency()
    {
        _vault.Variables.Clear();
        var lifecycle = _vault.Get<IVaultLifecycle>();

        var statuses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => lifecycle.InitializeAsync(Ct).AsTask()));

        Assert.Single(statuses.Distinct());
    }

    [Fact]
    public void TheAssemblyExposesOnlyTheRegistrationEntryPoint()
    {
        var exported = typeof(VaultServiceCollectionExtensions).Assembly.GetExportedTypes();

        // EF's generated migration classes are public (the architecture rule exempts exactly that namespace, and
        // VaultMigrationTests holds it to schema code).
        var migrations = exported.Where(t => t.Namespace == "Paduraksa.Vault.Migrations").ToList();
        Assert.Equal([typeof(VaultServiceCollectionExtensions)], exported.Except(migrations));
        Assert.Equal(["AddPaduraksaVault"], typeof(VaultServiceCollectionExtensions).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(m => m.Name).Distinct());
    }

    [Fact]
    public void PublicRegistrationRegistersTheCapabilitiesAndNothingReadsAValue()
    {
        var services = new ServiceCollection().AddPaduraksaVault(_vault.Data.Path);

        var registered = services.Select(d => d.ServiceType).Where(t => t.Namespace == "Paduraksa.Core.Vault").ToList();

        Assert.Contains(typeof(ISecretStore), registered);
        Assert.Contains(typeof(IMacKeySource), registered);
        Assert.Contains(typeof(IVaultLifecycle), registered);

        // No capability method can return secret bytes: nothing returns byte arrays, spans or streams.
        foreach (var method in registered.SelectMany(t => t.GetMethods()))
        {
            var returned = method.ReturnType;
            var payload = returned.IsGenericType ? returned.GetGenericArguments()[0] : returned;
            Assert.False(payload == typeof(byte[]) || payload == typeof(Stream) || payload == typeof(ReadOnlyMemory<byte>), method.Name);
        }
    }
}
