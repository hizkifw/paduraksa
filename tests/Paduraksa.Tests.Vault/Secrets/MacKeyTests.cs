using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Tests.Vault.Testing;
using Paduraksa.Vault;
using Paduraksa.Vault.Crypto;
using Paduraksa.Vault.Persistence;
using Paduraksa.Vault.Secrets;

namespace Paduraksa.Tests.Vault.Secrets;

/// <summary><see cref="IMacKeySource"/> and <see cref="IMacKey"/>: kind checks, correctness, rotation and the grace window.</summary>
public sealed class MacKeyTests : IDisposable
{
    private static readonly byte[] KeyA = RandomNumberGenerator.GetBytes(32);

    private static readonly byte[] KeyB = RandomNumberGenerator.GetBytes(32);

    private static readonly byte[] KeyC = RandomNumberGenerator.GetBytes(32);

    private static readonly byte[] Data = "data to authenticate"u8.ToArray();

    private readonly VaultHarness _vault = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ISecretStore Store => _vault.Get<ISecretStore>();

    private IMacKeySource Source => _vault.Get<IMacKeySource>();

    public void Dispose() => _vault.Dispose();

    private async Task<(SecretId Id, IMacKey Key)> NewKey(
        MacAlgorithm algorithm = MacAlgorithm.HmacSha256, SecretKind kind = SecretKind.HmacSigningKey, byte[]? value = null, ISecretStore? store = null)
    {
        var id = await (store ?? Store).PutAsync(kind, value ?? KeyA, SecretStoreTests.Mac(algorithm), Ct);
        var key = await Source.OpenAsync(id, kind, Ct);
        return (id, key!);
    }

    private static byte[] Tag(MacAlgorithm algorithm, byte[] key, byte[] data) => algorithm switch
    {
        MacAlgorithm.HmacSha256 => HMACSHA256.HashData(key, data),
        MacAlgorithm.HmacSha384 => HMACSHA384.HashData(key, data),
        _ => HMACSHA512.HashData(key, data),
    };

    [Theory]
    [InlineData(MacAlgorithm.HmacSha256, 32)]
    [InlineData(MacAlgorithm.HmacSha384, 48)]
    [InlineData(MacAlgorithm.HmacSha512, 64)]
    public async Task SignComputesTheHmacOfTheAlgorithmFixedAtPut(MacAlgorithm algorithm, int length)
    {
        var (_, key) = await NewKey(algorithm);
        var tag = new byte[100];

        var written = key.Sign(Data, tag);

        Assert.Equal(algorithm, key.Algorithm);
        Assert.Equal(length, key.TagLength);
        Assert.Equal(length, written);
        Assert.Equal(Tag(algorithm, KeyA, Data), tag[..written]);
        Assert.True(key.Verify(Data, tag.AsSpan(0, written)));
    }

    [Fact]
    public async Task VerifyRefusesAWrongTagWrongDataAndWrongLength()
    {
        var (_, key) = await NewKey();
        var tag = new byte[32];
        key.Sign(Data, tag);

        Assert.False(key.Verify("other data"u8, tag));
        Assert.False(key.Verify(Data, tag.AsSpan(0, 31)));
        Assert.False(key.Verify(Data, [.. tag, 0]));
        Assert.False(key.Verify(Data, Tag(MacAlgorithm.HmacSha256, KeyB, Data)));
        tag[5] ^= 1;
        Assert.False(key.Verify(Data, tag));
    }

    [Fact]
    public async Task SignNeedsABufferOfTheTagLength()
    {
        var (_, key) = await NewKey(MacAlgorithm.HmacSha512);

        Assert.Throws<ArgumentException>(() => key.Sign(Data, new byte[63]));
    }

    [Fact]
    public async Task OpenIsRefusedForTheWrongKindANonMacKindAndAnUnknownSecret()
    {
        var (id, _) = await NewKey(kind: SecretKind.EabHmacKey);
        var dns = await Store.PutAsync(SecretKind.DnsApiToken, KeyA, SecretStoreTests.Dns("api.example.com"), Ct);

        Assert.NotNull(await Source.OpenAsync(id, SecretKind.EabHmacKey, Ct));
        Assert.Null(await Source.OpenAsync(id, SecretKind.TsigKey, Ct));
        Assert.Null(await Source.OpenAsync(id, SecretKind.HmacSigningKey, Ct));
        Assert.Null(await Source.OpenAsync(id, SecretKind.DnsApiToken, Ct));
        Assert.Null(await Source.OpenAsync(dns, SecretKind.DnsApiToken, Ct));
        Assert.Null(await Source.OpenAsync(dns, SecretKind.EabHmacKey, Ct));
        Assert.Null(await Source.OpenAsync(SecretId.New(), SecretKind.EabHmacKey, Ct));
    }

    [Fact]
    public async Task AHandleFollowsTheCurrentVersionOfTheSecret()
    {
        var (id, key) = await NewKey();
        var oldTag = new byte[32];
        key.Sign(Data, oldTag);

        await Store.ReplaceAsync(id, KeyB, null, Ct);

        var newTag = new byte[32];
        key.Sign(Data, newTag);
        Assert.Equal(Tag(MacAlgorithm.HmacSha256, KeyB, Data), newTag);
        Assert.NotEqual(oldTag, newTag);
    }

    [Fact]
    public async Task ThePreviousVersionVerifiesWithinTheGraceWindowOnly()
    {
        var (id, key) = await NewKey();
        var oldTag = Tag(MacAlgorithm.HmacSha256, KeyA, Data);
        await Store.ReplaceAsync(id, KeyB, null, Ct);

        Assert.True(key.Verify(Data, oldTag));
        Assert.True(key.Verify(Data, Tag(MacAlgorithm.HmacSha256, KeyB, Data)));

        _vault.Time.Advance(_vault.Policy.MacGraceWindow - TimeSpan.FromSeconds(1));
        Assert.True(key.Verify(Data, oldTag));

        _vault.Time.Advance(TimeSpan.FromSeconds(2));
        Assert.False(key.Verify(Data, oldTag));
        Assert.True(key.Verify(Data, Tag(MacAlgorithm.HmacSha256, KeyB, Data)));
    }

    [Fact]
    public async Task OnlyTheImmediatelyPreviousVersionIsKept()
    {
        var (id, key) = await NewKey();
        await Store.ReplaceAsync(id, KeyB, null, Ct);
        await Store.ReplaceAsync(id, KeyC, null, Ct);

        Assert.False(key.Verify(Data, Tag(MacAlgorithm.HmacSha256, KeyA, Data)));
        Assert.True(key.Verify(Data, Tag(MacAlgorithm.HmacSha256, KeyB, Data)));
        Assert.True(key.Verify(Data, Tag(MacAlgorithm.HmacSha256, KeyC, Data)));
    }

    [Fact]
    public async Task SignNeverUsesThePreviousVersion()
    {
        var (id, key) = await NewKey();
        await Store.ReplaceAsync(id, KeyB, null, Ct);
        var tag = new byte[32];

        key.Sign(Data, tag);

        Assert.NotEqual(Tag(MacAlgorithm.HmacSha256, KeyA, Data), tag);
    }

    [Fact]
    public async Task TheExpiredPreviousValueIsDeletedFromTheDatabaseAtStartup()
    {
        var (id, _) = await NewKey();
        await Store.ReplaceAsync(id, KeyB, null, Ct);
        Assert.NotNull(await PreviousSealed(id));

        _vault.Time.Advance(_vault.Policy.MacGraceWindow + TimeSpan.FromSeconds(1));
        _vault.Restart();
        await _vault.Get<IVaultLifecycle>().InitializeAsync(Ct);

        Assert.Null(await PreviousSealed(id));
    }

    [Fact]
    public async Task TheExpiredPreviousValueIsDeletedWhenAHandleNoticesIt()
    {
        var (id, key) = await NewKey();
        await Store.ReplaceAsync(id, KeyB, null, Ct);
        _vault.Time.Advance(_vault.Policy.MacGraceWindow + TimeSpan.FromSeconds(1));

        key.Sign(Data, new byte[32]);

        Assert.Null(await PreviousSealed(id));
    }

    [Fact]
    public async Task AReplacementByAnotherProcessIsSeenWithinTheCacheAge()
    {
        var (id, key) = await NewKey();
        key.Sign(Data, new byte[32]);
        var other = _vault.Another();
        await other.GetRequiredService<IVaultLifecycle>().InitializeAsync(Ct);
        await other.GetRequiredService<ISecretStore>().ReplaceAsync(id, KeyB, null, Ct);

        // Still the cached key until the entry is old enough to be checked again.
        var tag = new byte[32];
        key.Sign(Data, tag);
        Assert.Equal(Tag(MacAlgorithm.HmacSha256, KeyA, Data), tag);

        _vault.Time.Advance(_vault.Policy.MacCacheMaxAge);
        key.Sign(Data, tag);
        Assert.Equal(Tag(MacAlgorithm.HmacSha256, KeyB, Data), tag);
    }

    [Fact]
    public async Task ADeletedSecretMakesTheHandleThrow()
    {
        var (id, key) = await NewKey();
        key.Sign(Data, new byte[32]);

        await Store.DeleteAsync(id, Ct);

        Assert.Throws<InvalidOperationException>(() => key.Sign(Data, new byte[32]));
        Assert.Throws<InvalidOperationException>(() => key.Verify(Data, new byte[32]));
    }

    [Fact]
    public async Task AHandleKeepsWorkingAcrossARestartOfTheVault()
    {
        var (id, _) = await NewKey();

        _vault.Restart();

        var key = await Source.OpenAsync(id, SecretKind.HmacSigningKey, Ct);
        Assert.True(key!.Verify(Data, Tag(MacAlgorithm.HmacSha256, KeyA, Data)));
    }

    [Fact]
    public async Task SigningWhileTheSecretIsReplacedNeverFailsOrProducesAForeignTag()
    {
        var (id, key) = await NewKey();
        var keys = new[] { KeyA, KeyB, KeyC };
        var valid = keys.Select(k => Convert.ToHexString(Tag(MacAlgorithm.HmacSha256, k, Data))).ToHashSet(StringComparer.Ordinal);
        using var stop = new CancellationTokenSource();
        // Every signer must have run at least once before the replacements finish, or "everyone worked" only measures
        // the thread pool's scheduling: on a loaded CI runner three of four signers had not started before the 30
        // replacements were done and the loop was cancelled.
        using var running = new CountdownEvent(4);

        var signers = Enumerable.Range(0, 4).Select(_ => Task.Run(
            () =>
            {
                var tag = new byte[32];
                var count = 0;
                while (!stop.IsCancellationRequested)
                {
                    key.Sign(Data, tag);
                    Assert.Contains(Convert.ToHexString(tag), valid);
                    count++;
                    if (count == 1)
                    {
                        running.Signal();
                    }
                }

                return count;
            },
            Ct)).ToList();

        Assert.True(running.Wait(TimeSpan.FromSeconds(10), Ct), "the signing workers never started");
        for (var i = 0; i < 30; i++)
        {
            await Store.ReplaceAsync(id, keys[(i + 1) % 3], null, Ct);
        }

        await stop.CancelAsync();
        Assert.All(await Task.WhenAll(signers), count => Assert.True(count > 0));
    }

    [Fact]
    public async Task SignAndVerifyDoNotAllocateOnceWarm()
    {
        var (_, key) = await NewKey();
        var tag = new byte[32];
        for (var i = 0; i < 100; i++)
        {
            key.Sign(Data, tag);
            key.Verify(Data, tag);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            key.Sign(Data, tag);
            key.Verify(Data, tag);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public async Task RefreshesRacingReplacementsAndInvalidationsNeverFailOrAcceptAForgedTag()
    {
        // Every call refreshes (the cache never trusts what it has), while the secret is replaced and the entry invalidated
        // from other threads: the install race. A state zeroed under a user would be the all-zero key, whose tag is forged below.
        _vault.Policy = _vault.Policy with { MacCacheMaxAge = TimeSpan.Zero };
        var (id, key) = await NewKey();
        var keys = new[] { KeyA, KeyB, KeyC };
        var valid = keys.Select(k => Convert.ToHexString(Tag(MacAlgorithm.HmacSha256, k, Data))).ToHashSet(StringComparer.Ordinal);
        var forged = Tag(MacAlgorithm.HmacSha256, new byte[32], Data);
        var cache = _vault.Get<MacKeyCache>();
        using var stop = new CancellationTokenSource();
        // Prove every worker is alive before the invalidation storm starts. An invalidation advances the epoch and a
        // refresh that started in an older epoch is thrown away, so a tight invalidate loop can starve all four workers
        // for seconds: "everyone worked" would then measure scheduling, not the race (a CI run failed that way).
        using var running = new CountdownEvent(4);

        var users = Enumerable.Range(0, 4).Select(_ => Task.Run(
            () =>
            {
                var tag = new byte[32];
                var count = 0;
                while (!stop.IsCancellationRequested)
                {
                    key.Sign(Data, tag);
                    Assert.Contains(Convert.ToHexString(tag), valid);
                    Assert.False(key.Verify(Data, forged));
                    count++;
                    if (count == 1)
                    {
                        running.Signal();
                    }
                }

                return count;
            },
            Ct)).ToList();

        Assert.True(running.Wait(TimeSpan.FromSeconds(10), Ct), "the signing workers never started");
        var invalidator = Task.Run(
            () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    cache.Invalidate(id);
                }
            },
            Ct);

        for (var i = 0; i < 25; i++)
        {
            await Store.ReplaceAsync(id, keys[(i + 1) % 3], null, Ct);
        }

        await stop.CancelAsync();
        await invalidator;
        Assert.All(await Task.WhenAll(users), count => Assert.True(count > 0));
    }

    [Fact]
    public void ARetiredStateCannotBeAcquiredAndIsZeroedOnlyWhenTheLastUserIsDone()
    {
        using var current = SecretBuffer.CopyOf(KeyA);
        var state = new MacKeyState(1, current, null, DateTimeOffset.MinValue, 0);
        Assert.True(state.TryAcquire());
        Assert.True(state.TryAcquire());

        state.Retire();
        state.Retire();

        Assert.False(state.TryAcquire());
        Assert.Equal(KeyA, state.Current.ToArray());
        state.Release();
        Assert.Equal(KeyA, state.Current.ToArray());
        state.Release();
        Assert.True(state.Current.IndexOfAnyExcept((byte)0) < 0);

        // Too many releases change nothing (no negative count, no second zeroing, no resurrection).
        state.Release();
        state.Release();
        Assert.False(state.TryAcquire());
    }

    [Fact]
    public async Task SigningNeitherWaitsForARotationNorFailsDuringOne()
    {
        var (_, key) = await NewKey();
        var tag = new byte[32];
        key.Sign(Data, tag);
        var runtime = _vault.Get<VaultRuntime>();
        _vault.Time.Advance(_vault.Policy.MacCacheMaxAge + TimeSpan.FromSeconds(1));

        // A rotation holds the gate: the cached key is used and the refresh is postponed, not waited for.
        using (await runtime.EnterAsync(Ct))
        {
            var during = Task.Run(() => (key.Sign(Data, tag), key.Verify(Data, tag)), Ct);
            Assert.Same(during, await Task.WhenAny(during, Task.Delay(TimeSpan.FromSeconds(5), Ct)));
            Assert.True((await during).Item2);
        }

        // A secret never used here has no cached key, so its first use does wait.
        var (_, fresh) = await NewKey();
        Task<int> first;
        using (await runtime.EnterAsync(Ct))
        {
            first = Task.Run(() => fresh.Sign(Data, new byte[32]), Ct);
            Assert.NotSame(first, await Task.WhenAny(first, Task.Delay(TimeSpan.FromMilliseconds(300), Ct)));
            Assert.False(first.IsCompleted);
        }

        Assert.Equal(32, await first);
    }

    private async Task<byte[]?> PreviousSealed(SecretId id)
    {
        await using var context = await _vault.Get<IDbContextFactory<VaultDbContext>>().CreateDbContextAsync(Ct);
        return await context.Secrets.AsNoTracking().Where(s => s.Id == id).Select(s => s.PreviousSealed).SingleAsync(Ct);
    }
}
