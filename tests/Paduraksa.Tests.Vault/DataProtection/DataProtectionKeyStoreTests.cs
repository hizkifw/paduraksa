using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;

using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Tests.Vault.Testing;
using Paduraksa.Vault;
using Paduraksa.Vault.Crypto;
using Paduraksa.Vault.DataProtection;
using Paduraksa.Vault.Persistence;

namespace Paduraksa.Tests.Vault.DataProtection;

/// <summary>
/// The Data Protection key ring in the vault (PLAN §8.1, §8.2): round-trip, sealing at rest, the AAD binding, KEK rotation and
/// survival of a restart.
/// </summary>
public sealed class DataProtectionKeyStoreTests : IDisposable
{
    private static readonly string ElementA = "<key id=\"a\"><creationDate>2026-11-08T00:00:00Z</creationDate><descriptor><encryption>secret-a</encryption></descriptor></key>";

    private static readonly string ElementB = "<key id=\"b\"><creationDate>2026-11-08T00:00:01Z</creationDate><descriptor><encryption>secret-b</encryption></descriptor></key>";

    private readonly VaultHarness _vault = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDataProtectionKeyStore Keys => _vault.Get<IDataProtectionKeyStore>();

    public void Dispose() => _vault.Dispose();

    [Fact]
    public async Task StoresAndReadsElementsUnderTheirFriendlyNames()
    {
        await Keys.StoreAsync("key-1", ElementA, Ct);
        await Keys.StoreAsync("key-2", ElementB, Ct);

        var keys = await Keys.ReadAllAsync(Ct);

        Assert.Equal(["key-1", "key-2"], keys.Select(k => k.FriendlyName));
        Assert.Equal([ElementA, ElementB], keys.Select(k => k.KeyXml));
    }

    [Fact]
    public async Task AnEmptyRingReadsAsAnEmptyList()
    {
        Assert.Empty(await Keys.ReadAllAsync(Ct));
    }

    [Fact]
    public async Task TheElementIsSealedAtRestAndBoundToItsRow()
    {
        await Keys.StoreAsync("key-1", ElementA, Ct);

        await using var context = await _vault.Get<IDbContextFactory<VaultDbContext>>().CreateDbContextAsync(Ct);
        var row = await context.DataProtectionKeys.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(DataProtectionKeyStore.ContextOf(row), new SealContext(row.Id.ToString(), "data-protection-key\nkey-1", row.Version));

        // Neither the marker in the element nor its descriptor text is in the row.
        var sealedHex = Convert.ToHexString(row.Sealed);
        Assert.DoesNotContain(Convert.ToHexString(Encoding.UTF8.GetBytes("secret-a")), sealedHex, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(Encoding.UTF8.GetBytes("<key id=\"a\">")), sealedHex, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnElementSwappedToAnotherRowsNameIsSkipped()
    {
        await Keys.StoreAsync("key-1", ElementA, Ct);
        await Keys.StoreAsync("key-2", ElementB, Ct);

        await using (var context = await _vault.Get<IDbContextFactory<VaultDbContext>>().CreateDbContextAsync(Ct))
        {
            var first = await context.DataProtectionKeys.SingleAsync(r => r.FriendlyName == "key-1", Ct);
            var second = await context.DataProtectionKeys.SingleAsync(r => r.FriendlyName == "key-2", Ct);
            (first.Sealed, second.Sealed) = (second.Sealed, first.Sealed);
            await context.SaveChangesAsync(Ct);
        }

        // Each ciphertext now belongs to the other row's AAD, so neither opens; both are refused (and logged), not returned.
        Assert.Empty(await Keys.ReadAllAsync(Ct));
        Assert.Equal(2, _vault.DataProtectionLog.Entries.Count(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error));
    }

    [Fact]
    public async Task EditingTheFriendlyNameOfARowIsRefused()
    {
        await Keys.StoreAsync("key-1", ElementA, Ct);
        await Keys.StoreAsync("key-2", ElementB, Ct);

        await using (var context = await _vault.Get<IDbContextFactory<VaultDbContext>>().CreateDbContextAsync(Ct))
        {
            await context.DataProtectionKeys
                .Where(r => r.FriendlyName == "key-1")
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.FriendlyName, "key-other"), Ct);
        }

        // The name is in the AAD: that element no longer opens, and the other one still does.
        Assert.Equal(ElementB, Assert.Single(await Keys.ReadAllAsync(Ct)).KeyXml);
        Assert.Single(_vault.DataProtectionLog.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error);
    }

    [Fact]
    public async Task StoringTheSameNameAgainReplacesTheElement()
    {
        await Keys.StoreAsync("key-1", ElementA, Ct);
        await Keys.StoreAsync("key-1", ElementB, Ct);

        var keys = await Keys.ReadAllAsync(Ct);
        Assert.Equal("key-1", Assert.Single(keys).FriendlyName);
        Assert.Equal(ElementB, keys[0].KeyXml);

        await using var context = await _vault.Get<IDbContextFactory<VaultDbContext>>().CreateDbContextAsync(Ct);
        Assert.Equal(2, (await context.DataProtectionKeys.AsNoTracking().SingleAsync(Ct)).Version);
    }

    [Fact]
    public async Task ARestartReadsTheSameRing()
    {
        await Keys.StoreAsync("key-1", ElementA, Ct);
        await _vault.Get<IVaultLifecycle>().InitializeAsync(Ct);

        var keys = await _vault.Restart().Get<IDataProtectionKeyStore>().ReadAllAsync(Ct);

        Assert.Equal(ElementA, Assert.Single(keys).KeyXml);
    }

    [Fact]
    public async Task KekRotationRewrapsTheRingAndItStillReads()
    {
        await Keys.StoreAsync("key-1", ElementA, Ct);
        using var newKek = Kek.Generate();

        var rotated = await _vault.Get<KekRotation>().RotateAsync(newKek, Ct);

        Assert.Equal(1, rotated.AccountKeys);
        await using (var context = await _vault.Get<IDbContextFactory<VaultDbContext>>().CreateDbContextAsync(Ct))
        {
            var row = await context.DataProtectionKeys.AsNoTracking().SingleAsync(Ct);
            Assert.Equal(newKek.Id, EnvelopeCipher.KekIdOf(row.Sealed).ToArray());
        }

        Assert.Equal(ElementA, Assert.Single(await Keys.ReadAllAsync(Ct)).KeyXml);

        // And a fresh process over the same database reads it under the new key.
        _vault.Variables[KekProvider.KeyVariable] = newKek.Encode();
        var restarted = _vault.Restart().Get<IDataProtectionKeyStore>();
        Assert.Equal(ElementA, Assert.Single(await restarted.ReadAllAsync(Ct)).KeyXml);
    }

    [Fact]
    public async Task AnUnreadableRowIsSkippedAndLoggedAtError()
    {
        await Keys.StoreAsync("key-1", ElementA, Ct);
        await Keys.StoreAsync("key-2", ElementB, Ct);

        await using (var context = await _vault.Get<IDbContextFactory<VaultDbContext>>().CreateDbContextAsync(Ct))
        {
            // Flip a bit in the first element's wrapped DEK: that row cannot be opened.
            var damaged = (await context.DataProtectionKeys.AsNoTracking().SingleAsync(r => r.FriendlyName == "key-1", Ct)).Sealed.ToArray();
            damaged[30] ^= 1;
            await context.DataProtectionKeys
                .Where(r => r.FriendlyName == "key-1")
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.Sealed, damaged), Ct);
        }

        var keys = await Keys.ReadAllAsync(Ct);

        Assert.Equal("key-2", Assert.Single(keys).FriendlyName);
        var entry = Assert.Single(_vault.DataProtectionLog.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error);
        Assert.Contains("key-1", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASealedElementIsLoggedWithoutTheElement()
    {
        await Keys.StoreAsync("key-1", ElementA, Ct);

        var entry = Assert.Single(_vault.DataProtectionLog.Entries);
        Assert.Contains("key-1", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-a", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsAnEmptyNameOrElement()
    {
        foreach ((string name, string element) in new[] { ("", ElementA), ("   ", ElementA), ("key-1", ""), ("key-1", "   ") })
        {
            await Assert.ThrowsAsync<ArgumentException>(async () => await Keys.StoreAsync(name, element, Ct));
        }
    }

    [Fact]
    public async Task RejectsAnOversizedNameOrElement()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await Keys.StoreAsync(new string('k', DataProtectionKeyStore.MaxFriendlyNameLength + 1), ElementA, Ct));
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await Keys.StoreAsync("key-1", new string('x', DataProtectionKeyStore.MaxElementBytes + 1), Ct));
    }
}
