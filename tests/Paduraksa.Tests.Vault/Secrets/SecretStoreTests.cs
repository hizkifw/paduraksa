using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Tests.Vault.Testing;
using Paduraksa.Vault.Crypto;
using Paduraksa.Vault.Persistence;
using Paduraksa.Vault.Secrets;

namespace Paduraksa.Tests.Vault.Secrets;

/// <summary><see cref="ISecretStore"/>: metadata rules, versions, what is on disk, and the binding of a sealed value to its row.</summary>
public sealed class SecretStoreTests : IDisposable
{
    private static readonly byte[] Value = "tok-PLAINTEXT-MARKER-0123456789"u8.ToArray();

    private readonly VaultHarness _vault = new();

    private ISecretStore Store => _vault.Get<ISecretStore>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _vault.Dispose();

    internal static SecretOptions Dns(params string[] hosts) => new("Cloudflare", null, hosts);

    internal static SecretOptions Mac(MacAlgorithm algorithm = MacAlgorithm.HmacSha256, string? label = "key") => new(label, algorithm, null);

    [Fact]
    public async Task PutThenDescribeReturnsOnlyMetadata()
    {
        var id = await Store.PutAsync(SecretKind.DnsApiToken, Value, Dns("API.Example.com.", "ns.example.com", "api.example.com"), Ct);

        var metadata = await Store.DescribeAsync(id, Ct);

        Assert.NotNull(metadata);
        Assert.Equal(id, metadata.Id);
        Assert.Equal(SecretKind.DnsApiToken, metadata.Kind);
        Assert.Equal("Cloudflare", metadata.Label);
        Assert.Null(metadata.MacAlgorithm);
        Assert.Equal(["api.example.com", "ns.example.com"], metadata.AllowedHosts);
        Assert.Equal(1, metadata.Version);
        Assert.Equal(_vault.Time.Now, metadata.CreatedAt);
        Assert.Equal(metadata.CreatedAt, metadata.UpdatedAt);
    }

    [Fact]
    public async Task AMacSecretKeepsItsAlgorithm()
    {
        var id = await Store.PutAsync(SecretKind.HmacSigningKey, Value, Mac(MacAlgorithm.HmacSha512), Ct);

        Assert.Equal(MacAlgorithm.HmacSha512, (await Store.DescribeAsync(id, Ct))!.MacAlgorithm);
    }

    [Fact]
    public async Task DescribeOfAnUnknownSecretIsNull() => Assert.Null(await Store.DescribeAsync(SecretId.New(), Ct));

    [Fact]
    public async Task OptionsThatDoNotFitTheKindAreRefused()
    {
        (SecretKind Kind, SecretOptions Options)[] bad =
        [
            (SecretKind.DnsApiToken, new(null, null, null)),
            (SecretKind.DnsApiToken, new(null, null, [])),
            (SecretKind.DnsApiToken, new(null, MacAlgorithm.HmacSha256, ["api.example.com"])),
            (SecretKind.DnsApiToken, new(null, null, ["https://api.example.com"])),
            (SecretKind.DnsApiToken, new(null, null, ["*.example.com"])),
            (SecretKind.EabHmacKey, new(null, null, null)),
            (SecretKind.TsigKey, new(null, MacAlgorithm.HmacSha256, ["host.example.com"])),
            (SecretKind.TotpSecret, new(null, MacAlgorithm.HmacSha256, null)),
            (SecretKind.HmacSigningKey, new(null, null, ["api.example.com"])),
        ];

        foreach (var (kind, options) in bad)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () => await Store.PutAsync(kind, Value, options, Ct));
        }
    }

    [Fact]
    public async Task ValueAndLabelSizesAreBounded()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () => await Store.PutAsync(SecretKind.HmacSigningKey, ReadOnlyMemory<byte>.Empty, new(null, MacAlgorithm.HmacSha256, null), Ct));
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await Store.PutAsync(SecretKind.HmacSigningKey, new byte[SecretStore.MaxValueBytes + 1], new(null, MacAlgorithm.HmacSha256, null), Ct));
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await Store.PutAsync(SecretKind.HmacSigningKey, Value, new(new string('x', SecretStore.MaxLabelLength + 1), MacAlgorithm.HmacSha256, null), Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await Store.PutAsync((SecretKind)99, Value, new(null, null, null), Ct));
        await Store.PutAsync(SecretKind.HmacSigningKey, new byte[SecretStore.MaxValueBytes], new(null, MacAlgorithm.HmacSha256, null), Ct);
    }

    [Fact]
    public async Task ReplaceKeepsTheIdBumpsTheVersionAndTheUpdateTime()
    {
        var id = await Store.PutAsync(SecretKind.DnsApiToken, Value, Dns("api.example.com"), Ct);
        _vault.Time.Advance(TimeSpan.FromHours(1));

        var replaced = await Store.ReplaceAsync(id, "new-value"u8.ToArray(), null, Ct);

        Assert.Equal(id, replaced.Id);
        Assert.Equal(2, replaced.Version);
        Assert.Equal(VaultHarness.Epoch, replaced.CreatedAt);
        Assert.Equal(_vault.Time.Now, replaced.UpdatedAt);
        Assert.Equal("Cloudflare", replaced.Label);
        Assert.Equal(["api.example.com"], replaced.AllowedHosts);
        Assert.Equal(replaced, (await Store.DescribeAsync(id, Ct))! with { AllowedHosts = replaced.AllowedHosts });
    }

    [Fact]
    public async Task ReplaceWithOptionsReplacesLabelAndHostsButNotTheAlgorithm()
    {
        var dns = await Store.PutAsync(SecretKind.DnsApiToken, Value, Dns("api.example.com"), Ct);
        var mac = await Store.PutAsync(SecretKind.TsigKey, Value, Mac(MacAlgorithm.HmacSha256), Ct);

        var changed = await Store.ReplaceAsync(dns, Value, new("Renamed", null, ["other.example.com"]), Ct);
        var same = await Store.ReplaceAsync(mac, Value, new("Tsig", MacAlgorithm.HmacSha256, null), Ct);

        Assert.Equal("Renamed", changed.Label);
        Assert.Equal(["other.example.com"], changed.AllowedHosts);
        Assert.Equal("Tsig", same.Label);
        await Assert.ThrowsAsync<ArgumentException>(async () => await Store.ReplaceAsync(mac, Value, new(null, MacAlgorithm.HmacSha512, null), Ct));
        await Assert.ThrowsAsync<ArgumentException>(async () => await Store.ReplaceAsync(dns, Value, new(null, null, null), Ct));
        await Assert.ThrowsAsync<ArgumentException>(async () => await Store.ReplaceAsync(dns, ReadOnlyMemory<byte>.Empty, null, Ct));
        Assert.Equal(2, (await Store.DescribeAsync(mac, Ct))!.Version);
    }

    [Fact]
    public async Task ReplaceOfAnUnknownSecretIsKeyNotFound() =>
        await Assert.ThrowsAsync<KeyNotFoundException>(async () => await Store.ReplaceAsync(SecretId.New(), Value, null, Ct));

    [Fact]
    public async Task DeleteRemovesTheSecretOnce()
    {
        var id = await Store.PutAsync(SecretKind.HmacSigningKey, Value, new(null, MacAlgorithm.HmacSha256, null), Ct);

        Assert.True(await Store.DeleteAsync(id, Ct));
        Assert.False(await Store.DeleteAsync(id, Ct));
        Assert.Null(await Store.DescribeAsync(id, Ct));
    }

    [Fact]
    public async Task SecretsSurviveARestart()
    {
        var id = await Store.PutAsync(SecretKind.EabHmacKey, Value, Mac(), Ct);

        _vault.Restart();

        var metadata = await Store.DescribeAsync(id, Ct);
        Assert.Equal(SecretKind.EabHmacKey, metadata?.Kind);
    }

    [Fact]
    public async Task NoPlaintextIsEverWrittenToTheDatabaseFiles()
    {
        await Store.DescribeAsync(SecretId.New(), Ct);
        await using var keepOpen = await _vault.Get<IDbContextFactory<VaultDbContext>>().CreateDbContextAsync(Ct);
        await keepOpen.Database.OpenConnectionAsync(Ct);
        var id = await Store.PutAsync(SecretKind.HmacSigningKey, Value, Mac(), Ct);
        await Store.ReplaceAsync(id, Value, null, Ct);

        // First with the connection still open, when the data is in the write-ahead log: the scan must see a log to mean anything.
        var wal = _vault.Data.File("vault.db-wal");
        Assert.True(File.Exists(wal) && new FileInfo(wal).Length > 0, "There is no write-ahead log to scan.");
        await AssertNoPlaintextInTheFiles();

        await keepOpen.Database.CloseConnectionAsync();
        _vault.Restart();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await AssertNoPlaintextInTheFiles();
    }

    private async Task AssertNoPlaintextInTheFiles()
    {
        var files = Directory.GetFiles(_vault.Data.Path, "vault.db*");
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var bytes = new byte[stream.Length];
            await stream.ReadExactlyAsync(bytes, Ct);
            Assert.True(bytes.AsSpan().IndexOf(Value) < 0, $"The plaintext is in {Path.GetFileName(file)}.");
            Assert.True(bytes.AsSpan().IndexOf("PLAINTEXT-MARKER"u8) < 0, $"Part of the plaintext is in {Path.GetFileName(file)}.");
        }
    }

    [Fact]
    public async Task EachSecretHasItsOwnDataKey()
    {
        var a = await Store.PutAsync(SecretKind.HmacSigningKey, Value, new(null, MacAlgorithm.HmacSha256, null), Ct);
        var b = await Store.PutAsync(SecretKind.HmacSigningKey, Value, new(null, MacAlgorithm.HmacSha256, null), Ct);

        var sealedValues = await Sealed(a, b);

        Assert.NotEqual(sealedValues[a].AsSpan(21, 32).ToArray(), sealedValues[b].AsSpan(21, 32).ToArray());
        Assert.NotEqual(sealedValues[a].AsSpan(81).ToArray(), sealedValues[b].AsSpan(81).ToArray());
    }

    [Fact]
    public async Task ASealedValueMovedToAnotherRowDoesNotOpen()
    {
        var victim = await Store.PutAsync(SecretKind.HmacSigningKey, "victim-value"u8.ToArray(), new(null, MacAlgorithm.HmacSha256, null), Ct);
        var donor = await Store.PutAsync(SecretKind.HmacSigningKey, "donor-value"u8.ToArray(), new(null, MacAlgorithm.HmacSha256, null), Ct);
        var donorSealed = (await Sealed(donor))[donor];

        await Update(s => s.Where(r => r.Id == victim).ExecuteUpdateAsync(u => u.SetProperty(r => r.Sealed, donorSealed), Ct));

        await Assert.ThrowsAnyAsync<CryptographicException>(async () => await _vault.Get<SecretReader>().OpenAsync(victim, false, Ct));
    }

    [Fact]
    public async Task ASealedValueUnderAnotherKindDoesNotOpen()
    {
        var id = await Store.PutAsync(SecretKind.EabHmacKey, Value, new(null, MacAlgorithm.HmacSha256, null), Ct);

        // Same row, same bytes, only the kind differs.
        await Update(s => s.Where(r => r.Id == id).ExecuteUpdateAsync(u => u.SetProperty(r => r.Kind, SecretKind.TsigKey), Ct));

        await Assert.ThrowsAnyAsync<CryptographicException>(async () => await _vault.Get<SecretReader>().OpenAsync(id, false, Ct));
    }

    [Fact]
    public async Task ASealedValueUnderAnotherVersionDoesNotOpen()
    {
        var id = await Store.PutAsync(SecretKind.HmacSigningKey, Value, new(null, MacAlgorithm.HmacSha256, null), Ct);

        // Same row, same bytes, only the version differs (a rolled-back or forged row).
        await Update(s => s.Where(r => r.Id == id).ExecuteUpdateAsync(u => u.SetProperty(r => r.Version, 7), Ct));

        await Assert.ThrowsAnyAsync<CryptographicException>(async () => await _vault.Get<SecretReader>().OpenAsync(id, false, Ct));
    }

    [Fact]
    public async Task WideningTheAllowedHostsInTheDatabaseMakesTheValueFailToOpen()
    {
        var id = await Store.PutAsync(SecretKind.DnsApiToken, Value, Dns("api.example.com"), Ct);
        var reader = _vault.Get<SecretReader>();
        using (await reader.OpenAsync(id, false, Ct))
        {
        }

        await Update(s => s.Where(r => r.Id == id).ExecuteUpdateAsync(
            u => u.SetProperty(r => r.AllowedHosts, new List<string> { "api.example.com", "evil.example" }), Ct));

        await Assert.ThrowsAnyAsync<CryptographicException>(async () => await reader.OpenAsync(id, false, Ct));

        // The supported way to change them re-seals the value, and then it opens again.
        await Store.ReplaceAsync(id, Value, Dns("api.example.com", "other.example"), Ct);
        using var opened = await reader.OpenAsync(id, false, Ct);
        Assert.Equal(["api.example.com", "other.example"], opened!.Row.AllowedHosts);
    }

    [Fact]
    public async Task ChangingAMacKeysAlgorithmInTheDatabaseMakesTheValueFailToOpen()
    {
        var id = await Store.PutAsync(SecretKind.TsigKey, Value, Mac(MacAlgorithm.HmacSha256), Ct);

        await Update(s => s.Where(r => r.Id == id).ExecuteUpdateAsync(u => u.SetProperty(r => r.MacAlgorithm, MacAlgorithm.HmacSha512), Ct));

        await Assert.ThrowsAnyAsync<CryptographicException>(async () => await _vault.Get<SecretReader>().OpenAsync(id, false, Ct));
    }

    private async Task Update(Func<DbSet<SecretRow>, Task<int>> change)
    {
        await using var context = await _vault.Get<IDbContextFactory<VaultDbContext>>().CreateDbContextAsync(Ct);
        await change(context.Secrets);
    }

    private async Task<Dictionary<SecretId, byte[]>> Sealed(params SecretId[] ids)
    {
        await using var context = await _vault.Get<IDbContextFactory<VaultDbContext>>().CreateDbContextAsync(Ct);
        return await context.Secrets.AsNoTracking().Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Sealed, Ct);
    }

    [Fact]
    public async Task APutValueDecryptsToWhatWasStoredUnderTheKek()
    {
        var id = await Store.PutAsync(SecretKind.DnsApiToken, Value, Dns("api.example.com"), Ct);

        using var opened = await _vault.Get<SecretReader>().OpenAsync(id, false, Ct);

        Assert.Equal(Value, opened!.Current.Span.ToArray());
        Assert.Equal(Encoding.UTF8.GetString(Value), Encoding.UTF8.GetString(opened.Current.Span));
    }
}
