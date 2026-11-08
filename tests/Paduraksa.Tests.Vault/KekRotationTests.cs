using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Tests.Vault.Testing;
using Paduraksa.Vault;
using Paduraksa.Vault.Crypto;
using Paduraksa.Vault.Persistence;

namespace Paduraksa.Tests.Vault;

/// <summary>KEK rotation (PLAN §8.3): every DEK re-wrapped under the new KEK in one transaction.</summary>
public sealed class KekRotationTests : IDisposable
{
    private static readonly byte[] KeyA = RandomNumberGenerator.GetBytes(32);

    private static readonly byte[] KeyB = RandomNumberGenerator.GetBytes(32);

    private static readonly byte[] Data = "data"u8.ToArray();

    private readonly VaultHarness _vault = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _vault.Dispose();

    private async Task<(SecretId Dns, SecretId Mac, AcmeAccountId Account)> Populate()
    {
        var store = _vault.Get<ISecretStore>();
        var dns = await store.PutAsync(SecretKind.DnsApiToken, "dns-token"u8.ToArray(), SecretStoreTestsDns(), Ct);
        var mac = await store.PutAsync(SecretKind.HmacSigningKey, KeyA, new(null, MacAlgorithm.HmacSha256, null), Ct);
        await store.ReplaceAsync(mac, KeyB, null, Ct);
        var account = AcmeAccountId.New();
        await _vault.Get<IJwsSigner>().CreateAccountKeyAsync(account, JwsAlgorithm.ES256, Ct);
        return (dns, mac, account);
    }

    private static SecretOptions SecretStoreTestsDns() => new("dns", null, ["api.example.com"]);

    private async Task<List<byte[]>> KekIds()
    {
        await using var context = await _vault.Get<IDbContextFactory<VaultDbContext>>().CreateDbContextAsync(Ct);
        var sealedValues = await context.Secrets.AsNoTracking().Select(s => s.Sealed).ToListAsync(Ct);
        sealedValues.AddRange(await context.Secrets.AsNoTracking().Where(s => s.PreviousSealed != null).Select(s => s.PreviousSealed!).ToListAsync(Ct));
        sealedValues.AddRange(await context.AccountKeys.AsNoTracking().Select(k => k.Sealed).ToListAsync(Ct));
        return [.. sealedValues.Select(v => EnvelopeCipher.KekIdOf(v).ToArray())];
    }

    [Fact]
    public async Task EverythingIsRewrappedAndKeepsWorkingUnderTheNewKekInTheSameProcess()
    {
        var (dns, mac, account) = await Populate();
        await _vault.Get<IVaultLifecycle>().InitializeAsync(Ct);
        var key = await _vault.Get<IMacKeySource>().OpenAsync(mac, SecretKind.HmacSigningKey, Ct);
        using var newKek = Kek.Generate();
        var newId = newKek.Id;

        var rotated = await _vault.Get<KekRotation>().RotateAsync(newKek, Ct);

        Assert.Equal(new KekRotated(3, 1, 0, 0, 0), rotated);
        Assert.All(await KekIds(), id => Assert.Equal(newId, id));
        var tag = new byte[32];
        key!.Sign(Data, tag);
        Assert.Equal(HMACSHA256.HashData(KeyB, Data), tag);
        Assert.True(key.Verify(Data, HMACSHA256.HashData(KeyA, Data)));
        await _vault.Get<IJwsSigner>().SignAsync(account, """{"alg":"ES256"}"""u8.ToArray(), "{}"u8.ToArray(), Ct);
        using var opened = await _vault.Get<Paduraksa.Vault.Secrets.SecretReader>().OpenAsync(dns, false, Ct);
        Assert.Equal("dns-token", Encoding.UTF8.GetString(opened!.Current.Span));

        // Writes after the rotation are under the new key too.
        await _vault.Get<ISecretStore>().PutAsync(SecretKind.HmacSigningKey, "x"u8.ToArray(), new(null, MacAlgorithm.HmacSha256, null), Ct);
        Assert.All(await KekIds(), id => Assert.Equal(newId, id));
    }

    [Fact]
    public async Task AfterRotationOnlyTheNewKekOpensTheVault()
    {
        var (_, mac, account) = await Populate();
        var oldText = _vault.Variables[KekProvider.KeyVariable]!;
        using var newKek = Kek.Generate();
        var newText = newKek.Encode();
        await _vault.Get<KekRotation>().RotateAsync(newKek, Ct);

        _vault.Restart();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await _vault.Get<IVaultLifecycle>().InitializeAsync(Ct));

        _vault.Variables[KekProvider.KeyVariable] = oldText;
        _vault.Restart();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await _vault.Get<IVaultLifecycle>().InitializeAsync(Ct));
        Assert.Contains("does not match", ex.Message, StringComparison.Ordinal);

        _vault.Variables[KekProvider.KeyVariable] = newText;
        _vault.Restart();
        await _vault.Get<IVaultLifecycle>().InitializeAsync(Ct);
        var key = await _vault.Get<IMacKeySource>().OpenAsync(mac, SecretKind.HmacSigningKey, Ct);
        Assert.True(key!.Verify(Data, HMACSHA256.HashData(KeyB, Data)));
        Assert.NotNull(await _vault.Get<IJwsSigner>().GetAccountKeyAsync(account, Ct));
        await _vault.Get<IJwsSigner>().SignAsync(account, """{"alg":"ES256"}"""u8.ToArray(), "{}"u8.ToArray(), Ct);
    }

    [Fact]
    public async Task ADamagedValueAbortsTheWholeRotationAndNothingChanges()
    {
        var (dns, _, _) = await Populate();
        var before = await KekIds();
        await using (var context = await _vault.Get<IDbContextFactory<VaultDbContext>>().CreateDbContextAsync(Ct))
        {
            // Flip a bit in the account key's wrapped DEK: that row cannot be re-wrapped.
            var damaged = (await context.AccountKeys.AsNoTracking().SingleAsync(Ct)).Sealed.ToArray();
            damaged[30] ^= 1;
            await context.AccountKeys.ExecuteUpdateAsync(u => u.SetProperty(k => k.Sealed, damaged), Ct);
        }

        using var newKek = Kek.Generate();
        await Assert.ThrowsAnyAsync<CryptographicException>(async () => await _vault.Get<KekRotation>().RotateAsync(newKek, Ct));

        Assert.Equal(before.Select(Convert.ToHexString), (await KekIds()).Select(Convert.ToHexString));
        using var opened = await _vault.Get<Paduraksa.Vault.Secrets.SecretReader>().OpenAsync(dns, false, Ct);
        Assert.Equal("dns-token", Encoding.UTF8.GetString(opened!.Current.Span));

        // The old key still opens the database, and the caller still owns the unused new key.
        _vault.Restart();
        await _vault.Get<IVaultLifecycle>().InitializeAsync(Ct);
        Assert.Equal(32, newKek.CheckValue.Length);
    }

    [Fact]
    public async Task RotatingToTheKeyAlreadyInUseIsRefused()
    {
        await Populate();
        using var same = Kek.Parse(_vault.Variables[KekProvider.KeyVariable]!);

        await Assert.ThrowsAsync<ArgumentException>(async () => await _vault.Get<KekRotation>().RotateAsync(same, Ct));
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await _vault.Get<IVaultKekRotation>().RotateKekAsync(_vault.Variables[KekProvider.KeyVariable].AsMemory(), Ct));
    }

    [Fact]
    public async Task ThePublicContractRotatesFromTheTextFormAndRefusesAMalformedKey()
    {
        var (_, mac, _) = await Populate();
        var rotation = _vault.Get<IVaultKekRotation>();
        using var newKek = Kek.Generate();

        foreach (var bad in new[] { "", "not a key", Convert.ToBase64String(new byte[31]), Convert.ToBase64String(new byte[32]) })
        {
            var ex = await Assert.ThrowsAsync<ArgumentException>(async () => await rotation.RotateKekAsync(bad.AsMemory(), Ct));
            Assert.DoesNotContain(bad.Length == 0 ? "\0" : bad, ex.Message, StringComparison.Ordinal);
        }

        var result = await rotation.RotateKekAsync(newKek.Encode().AsMemory(), Ct);

        Assert.Equal(new KekRotationResult(3, 1, 0, 0, 0), result);
        var key = await _vault.Get<IMacKeySource>().OpenAsync(mac, SecretKind.HmacSigningKey, Ct);
        Assert.True(key!.Verify(Data, HMACSHA256.HashData(KeyB, Data)));
        Assert.All(await KekIds(), id => Assert.Equal(newKek.Id, id));
    }

    [Fact]
    public async Task OldWrappedKeysAreGoneFromTheDatabaseAndItsLogAfterRotation()
    {
        await Populate();
        var oldWraps = (await Sealed()).Select(v => v.AsMemory(9, 44).ToArray()).ToList();
        Assert.Equal(4, oldWraps.Count);

        // The scan can see them before (so that finding nothing afterwards means something), with connections still open.
        Assert.All(oldWraps, wrap => Assert.True(FilesContain(wrap), "The scan does not find a wrapped key that is there."));

        using var newKek = Kek.Generate();
        await _vault.Get<IVaultKekRotation>().RotateKekAsync(newKek.Encode().AsMemory(), Ct);

        Assert.All(oldWraps, wrap => Assert.False(FilesContain(wrap), "An old wrapped key is still in vault.db or its log."));
        // The new ones are there.
        var newWraps = (await Sealed()).Select(v => v.AsMemory(9, 44).ToArray()).ToList();
        Assert.All(newWraps, wrap => Assert.True(FilesContain(wrap)));
    }

    private async Task<List<byte[]>> Sealed()
    {
        await using var context = await _vault.Get<IDbContextFactory<VaultDbContext>>().CreateDbContextAsync(Ct);
        var values = await context.Secrets.AsNoTracking().Select(s => s.Sealed).ToListAsync(Ct);
        values.AddRange(await context.Secrets.AsNoTracking().Where(s => s.PreviousSealed != null).Select(s => s.PreviousSealed!).ToListAsync(Ct));
        values.AddRange(await context.AccountKeys.AsNoTracking().Select(k => k.Sealed).ToListAsync(Ct));
        return values;
    }

    /// <summary>Whether the bytes are in vault.db, its write-ahead log or its shared-memory file, read as they are on disk right now.</summary>
    private bool FilesContain(byte[] bytes)
    {
        foreach (var file in Directory.GetFiles(_vault.Data.Path, "vault.db*"))
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var content = new byte[stream.Length];
                stream.ReadExactly(content);
                if (content.AsSpan().IndexOf(bytes) >= 0)
                {
                    return true;
                }
            }
            catch (FileNotFoundException)
            {
                // SQLite removed it (the log, after a checkpoint on close) between listing and opening.
            }
        }

        return false;
    }

    [Fact]
    public async Task AnEmptyVaultRotates()
    {
        using var newKek = Kek.Generate();

        Assert.Equal(new KekRotated(0, 0, 0, 0, 0), await _vault.Get<KekRotation>().RotateAsync(newKek, Ct));
    }
}
