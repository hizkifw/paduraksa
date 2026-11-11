using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Vault.Testing;
using Paduraksa.Vault.Passwords;
using Paduraksa.Vault.Persistence;

namespace Paduraksa.Tests.Vault.Passwords;

public sealed class PasswordCredentialStoreTests : IAsyncLifetime
{
    private static readonly byte[] Alice = "alice"u8.ToArray();
    private static readonly byte[] Password = "correct horse"u8.ToArray();
    private static readonly byte[] Client = new byte[16];

    private readonly VaultHarness _vault = new();

    private IPasswordCredentials Credentials => _vault.Get<IPasswordCredentials>();

    private IPasswordVerifier Verifier => _vault.Get<IPasswordVerifier>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Open is synchronous and never starts the vault: the host does, long before a compile.
    public async ValueTask InitializeAsync() => await Start();

    public ValueTask DisposeAsync()
    {
        _vault.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task Start() => await _vault.Get<IVaultLifecycle>().InitializeAsync(Ct);

    private static PasswordCredentialId NewId() => PasswordCredentialId.New();

    private static byte[] Client16(byte last)
    {
        var client = new byte[16];
        client[15] = last;
        return client;
    }

    [Fact]
    public async Task APasswordIsSetAndOnlyItVerifies()
    {
        var id = NewId();
        var handle = Credentials.Open(id);
        Assert.False(handle.IsSet);

        await Credentials.SetAsync(id, "correct horse".AsMemory(), Ct);

        Assert.True(handle.IsSet);
        Assert.Equal(id, handle.Id);
        Assert.Equal(PasswordVerdict.Verified, Verifier.Verify(handle, Alice, Password, Client, remember: false));
        Assert.Equal(PasswordVerdict.Rejected, Verifier.Verify(handle, Alice, "correct hors"u8, Client, remember: false));
        Assert.Equal(PasswordVerdict.Rejected, Verifier.Verify(handle, Alice, ""u8, Client, remember: false));
    }

    [Fact]
    public async Task ANameNobodyHasAndACredentialWithoutAPasswordAreRejectedWithoutMatching()
    {
        var unset = Credentials.Open(NewId());

        Assert.Equal(PasswordVerdict.Rejected, Verifier.Verify(null, Alice, Password, Client, remember: true));
        Assert.Equal(PasswordVerdict.NoPassword, Verifier.Verify(unset, Alice, Password, Client, remember: true));
        Assert.Equal(default, Verifier.TryVerifyCached(unset, Alice, Password, Client));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ChangingThePasswordKeepsTheHandleAndReplacesWhatVerifies()
    {
        var id = NewId();
        var handle = Credentials.Open(id);
        await Credentials.SetAsync(id, "old password".AsMemory(), Ct);
        Assert.Equal(PasswordVerdict.Verified, Verifier.Verify(handle, Alice, "old password"u8, Client, remember: true));
        Assert.True(Verifier.TryVerifyCached(handle, Alice, "old password"u8, Client).Hit);

        await Credentials.SetAsync(id, "new password".AsMemory(), Ct);

        Assert.Same(handle, Credentials.Open(id));
        Assert.Equal(PasswordVerdict.Rejected, Verifier.Verify(handle, Alice, "old password"u8, Client, remember: true));
        Assert.Equal(PasswordVerdict.Verified, Verifier.Verify(handle, Alice, "new password"u8, Client, remember: true));
        Assert.False(Verifier.TryVerifyCached(handle, Alice, "old password"u8, Client).Hit);
        Assert.True(Verifier.TryVerifyCached(handle, Alice, "new password"u8, Client).Hit);
    }

    [Fact]
    public async Task DeletingForgetsThePasswordAndItsCacheEntries()
    {
        var id = NewId();
        var handle = Credentials.Open(id);
        await Credentials.SetAsync(id, "correct horse".AsMemory(), Ct);
        Verifier.Verify(handle, Alice, Password, Client, remember: true);
        Assert.Equal([id], await Credentials.ListAsync(Ct));

        Assert.True(await Credentials.DeleteAsync(id, Ct));
        Assert.False(await Credentials.DeleteAsync(id, Ct));

        Assert.False(handle.IsSet);
        Assert.Empty(await Credentials.ListAsync(Ct));
        Assert.False(Verifier.TryVerifyCached(handle, Alice, Password, Client).Hit);
        Assert.Equal(PasswordVerdict.NoPassword, Verifier.Verify(handle, Alice, Password, Client, remember: true));

        // Setting it again brings the same handle back to life, and what was cached for the old password stays gone.
        await Credentials.SetAsync(id, "correct horse".AsMemory(), Ct);
        Assert.True(handle.IsSet);
        Assert.False(Verifier.TryVerifyCached(handle, Alice, Password, Client).Hit);
    }

    [Fact]
    public async Task AnExistingVerifierIsImportedAsItIsAndNeverOverwritten()
    {
        var verifier = PasswordHash.Create("correct horse", PasswordHash.MinIterations);
        var id = NewId();

        Assert.True(await Credentials.ImportAsync(id, verifier, Ct));
        Assert.Equal(PasswordVerdict.Verified, Verifier.Verify(Credentials.Open(id), Alice, Password, Client, remember: false));

        // Importing again changes nothing, even with another verifier: the password the vault holds wins.
        Assert.False(await Credentials.ImportAsync(id, PasswordHash.Create("another", PasswordHash.MinIterations), Ct));
        Assert.Equal(PasswordVerdict.Verified, Verifier.Verify(Credentials.Open(id), Alice, Password, Client, remember: false));

        await Assert.ThrowsAsync<ArgumentException>(async () => await Credentials.ImportAsync(NewId(), "$pbkdf2-sha256$i=1000$AAAAAAAAAAA$AAAAAAAAAAAAAAAAAAAAAA", Ct));
        await Assert.ThrowsAsync<ArgumentException>(async () => await Credentials.ImportAsync(NewId(), "plain text", Ct));
    }

    [Fact]
    public async Task CredentialsSurviveARestartAndOnlyTheSealedVerifierIsStored()
    {
        var kept = NewId();
        var removed = NewId();
        await Credentials.SetAsync(kept, "correct horse".AsMemory(), Ct);
        await Credentials.SetAsync(removed, "other".AsMemory(), Ct);
        await Credentials.DeleteAsync(removed, Ct);

        _vault.Restart();
        await Start();

        var handle = Credentials.Open(kept);
        Assert.True(handle.IsSet);
        Assert.False(Credentials.Open(removed).IsSet);
        Assert.Equal(PasswordVerdict.Verified, Verifier.Verify(handle, Alice, Password, Client, remember: false));
        Assert.Equal([kept], await Credentials.ListAsync(Ct));

        await using var context = await _vault.Get<IDbContextFactory<VaultDbContext>>().CreateDbContextAsync(Ct);
        var row = await context.PasswordCredentials.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(PasswordCredentialStore.ContextOf(row), new Paduraksa.Vault.Crypto.SealContext(kept.ToString(), "password-credential", row.Version));
        var sealedHex = Convert.ToHexString(row.Sealed);
        Assert.DoesNotContain(Convert.ToHexString(Encoding.UTF8.GetBytes("$pbkdf2-sha256")), sealedHex, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARowThatDoesNotOpenIsSkippedAndLoggedWithoutStoppingTheOthers()
    {
        var damaged = NewId();
        var fine = NewId();
        await Credentials.SetAsync(damaged, "correct horse".AsMemory(), Ct);
        await Credentials.SetAsync(fine, "correct horse".AsMemory(), Ct);
        await using (var context = await _vault.Get<IDbContextFactory<VaultDbContext>>().CreateDbContextAsync(Ct))
        {
            // The version is in the AAD: a row edited under it no longer opens.
            await context.Database.ExecuteSqlRawAsync("UPDATE PasswordCredentials SET Version = Version + 1 WHERE Id = {0}", [damaged.Value.ToByteArray(bigEndian: true)], Ct);
        }

        _vault.Restart();
        await Start();

        Assert.False(Credentials.Open(damaged).IsSet);
        Assert.True(Credentials.Open(fine).IsSet);
        Assert.Single(_vault.PasswordLog.Entries, e => e.Level == LogLevel.Error && e.Message.Contains(damaged.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACachedSuccessIsKeyedByCredentialUserClientAndPassword()
    {
        var id = NewId();
        var handle = Credentials.Open(id);
        await Credentials.SetAsync(id, "correct horse".AsMemory(), Ct);

        var first = Verifier.TryVerifyCached(handle, Alice, Password, Client);
        Assert.False(first.Hit);
        Assert.NotEqual(0, first.AttemptKey);

        // A wrong password is never remembered.
        Assert.Equal(PasswordVerdict.Rejected, Verifier.Verify(handle, Alice, "wrong"u8, Client, remember: true));
        Assert.False(Verifier.TryVerifyCached(handle, Alice, "wrong"u8, Client).Hit);

        Assert.Equal(PasswordVerdict.Verified, Verifier.Verify(handle, Alice, Password, Client, remember: true));
        var hit = Verifier.TryVerifyCached(handle, Alice, Password, Client);
        Assert.True(hit.Hit);
        Assert.Equal(first.AttemptKey, hit.AttemptKey);

        Assert.False(Verifier.TryVerifyCached(handle, Alice, Password, Client16(1)).Hit);
        Assert.False(Verifier.TryVerifyCached(handle, "bob"u8, Password, Client).Hit);
        Assert.False(Verifier.TryVerifyCached(handle, Alice, "correct hors"u8, Client).Hit);
        Assert.NotEqual(first.AttemptKey, Verifier.TryVerifyCached(handle, Alice, Password, Client16(1)).AttemptKey);
    }

    [Fact]
    public async Task VerifyWithoutRememberLeavesTheCacheAlone()
    {
        var id = NewId();
        var handle = Credentials.Open(id);
        await Credentials.SetAsync(id, "correct horse".AsMemory(), Ct);

        Assert.Equal(PasswordVerdict.Verified, Verifier.Verify(handle, Alice, Password, Client, remember: false));

        Assert.False(Verifier.TryVerifyCached(handle, Alice, Password, Client).Hit);
    }

    [Fact]
    public async Task AHitKeepsAnActiveUserVerifiedAndAnIdleOneLapsesAfterFiveMinutes()
    {
        var id = NewId();
        var handle = Credentials.Open(id);
        await Credentials.SetAsync(id, "correct horse".AsMemory(), Ct);
        Verifier.Verify(handle, Alice, Password, Client, remember: true);

        // Four and a half minutes in, a hit pushes the expiry out to five minutes from now.
        _vault.Time.Advance(TimeSpan.FromSeconds(270));
        Assert.True(Verifier.TryVerifyCached(handle, Alice, Password, Client).Hit);
        _vault.Time.Advance(TimeSpan.FromSeconds(290));
        Assert.True(Verifier.TryVerifyCached(handle, Alice, Password, Client).Hit);

        // Left alone, it lapses five minutes after the last hit.
        _vault.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.False(Verifier.TryVerifyCached(handle, Alice, Password, Client).Hit);
    }

    [Fact]
    public void OpeningACredentialBeforeTheVaultHasStartedFailsLoudlyInsteadOfBlocking()
    {
        using var other = new VaultHarness();

        var failure = Assert.Throws<InvalidOperationException>(() => other.Get<IPasswordCredentials>().Open(NewId()));

        Assert.Contains("InitializeAsync", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARevertedFirstPasswordLeavesNoRowAndNoPassword()
    {
        var id = NewId();
        var handle = Credentials.Open(id);

        var change = await Credentials.SetAsync(id, "correct horse".AsMemory(), Ct);
        Assert.True(handle.IsSet);
        await Credentials.RevertAsync([change], Ct);

        Assert.False(handle.IsSet);
        Assert.Empty(await Credentials.ListAsync(Ct));
        await using var context = await _vault.Get<IDbContextFactory<VaultDbContext>>().CreateDbContextAsync(Ct);
        Assert.Empty(await context.PasswordCredentials.AsNoTracking().ToListAsync(Ct));
    }

    [Fact]
    public async Task ARevertedChangeOfAPasswordPutsBackTheRowExactlyAndTheOldPasswordVerifies()
    {
        var id = NewId();
        var handle = Credentials.Open(id);
        await Credentials.SetAsync(id, "old password".AsMemory(), Ct);
        PasswordCredentialRow before;
        await using (var context = await _vault.Get<IDbContextFactory<VaultDbContext>>().CreateDbContextAsync(Ct))
        {
            before = await context.PasswordCredentials.AsNoTracking().SingleAsync(Ct);
        }

        var change = await Credentials.SetAsync(id, "new password".AsMemory(), Ct);
        Assert.Equal(PasswordVerdict.Verified, Verifier.Verify(handle, Alice, "new password"u8, Client, remember: false));
        await Credentials.RevertAsync([change], Ct);

        Assert.Equal(PasswordVerdict.Verified, Verifier.Verify(handle, Alice, "old password"u8, Client, remember: false));
        Assert.Equal(PasswordVerdict.Rejected, Verifier.Verify(handle, Alice, "new password"u8, Client, remember: false));
        await using (var context = await _vault.Get<IDbContextFactory<VaultDbContext>>().CreateDbContextAsync(Ct))
        {
            var after = await context.PasswordCredentials.AsNoTracking().SingleAsync(Ct);
            Assert.Equal((before.Version, before.CreatedAt, before.UpdatedAt), (after.Version, after.CreatedAt, after.UpdatedAt));
            Assert.Equal(before.Sealed, after.Sealed);
        }

        // And it survives a restart: the database, not only the memory, was put back.
        _vault.Restart();
        await Start();
        Assert.Equal(PasswordVerdict.Verified, Verifier.Verify(Credentials.Open(id), Alice, "old password"u8, Client, remember: false));
    }

    [Fact]
    public async Task SeveralChangesAreRevertedLastFirstAndAKeptChangeIsLeftAlone()
    {
        var (first, second) = (NewId(), NewId());
        await Credentials.SetAsync(second, "second v1".AsMemory(), Ct);
        var kept = await Credentials.SetAsync(first, "first v1".AsMemory(), Ct);
        var changes = new[]
        {
            await Credentials.SetAsync(second, "second v2".AsMemory(), Ct),
            await Credentials.SetAsync(second, "second v3".AsMemory(), Ct),
        };

        Credentials.Keep([kept]);
        await Credentials.RevertAsync(changes, Ct);
        await Credentials.RevertAsync([kept, .. changes], Ct);

        Assert.Equal(PasswordVerdict.Verified, Verifier.Verify(Credentials.Open(second), Alice, "second v1"u8, Client, remember: false));
        Assert.Equal(PasswordVerdict.Verified, Verifier.Verify(Credentials.Open(first), Alice, "first v1"u8, Client, remember: false));
    }

    [Fact]
    public async Task APreparedPasswordIsAHandleForTheCallerAloneNothingStoredOrCached()
    {
        var id = NewId();
        var real = Credentials.Open(id);

        var prepared = await Credentials.PrepareAsync(id, "draft password".AsMemory(), Ct);
        var imported = Credentials.PrepareImport(NewId(), PasswordHash.Create("other", PasswordHash.MinIterations));

        Assert.True(prepared.IsSet);
        Assert.True(imported.IsSet);
        Assert.Equal(PasswordVerdict.Verified, Verifier.Verify(prepared, Alice, "draft password"u8, Client, remember: false));
        Assert.False(real.IsSet);
        Assert.NotSame(prepared, Credentials.Open(id));
        Assert.Empty(await Credentials.ListAsync(Ct));
        await Assert.ThrowsAsync<ArgumentException>(async () => await Credentials.PrepareAsync(id, ReadOnlyMemory<char>.Empty, Ct));
        Assert.Throws<ArgumentException>(() => Credentials.PrepareImport(id, "plain text"));
    }

    [Fact]
    public async Task APasswordIsRefusedWhenEmptyOrTooLongOrForTheWrongHandle()
    {
        var id = NewId();

        await Assert.ThrowsAsync<ArgumentException>(async () => await Credentials.SetAsync(id, ReadOnlyMemory<char>.Empty, Ct));
        await Assert.ThrowsAsync<ArgumentException>(async () => await Credentials.SetAsync(id, new string('p', AccessListLimits.MaxPasswordBytes + 1).AsMemory(), Ct));
        await Credentials.SetAsync(id, new string('p', AccessListLimits.MaxPasswordBytes).AsMemory(), Ct);
        await Assert.ThrowsAsync<ArgumentException>(async () => await Credentials.SetAsync(PasswordCredentialId.Empty, "x".AsMemory(), Ct));
        Assert.Throws<ArgumentException>(() => Credentials.Open(PasswordCredentialId.Empty));
        Assert.Throws<ArgumentException>(() => Verifier.TryVerifyCached(new ForeignHandle(), Alice, Password, Client));
    }

    [Fact]
    public async Task NothingTheVaultLogsCarriesAPasswordOrAVerifier()
    {
        var id = NewId();
        var verifier = PasswordHash.Create("log-me-not-9876", PasswordHash.MinIterations);
        await Credentials.SetAsync(id, "log-me-not-1234".AsMemory(), Ct);
        await Credentials.ImportAsync(NewId(), verifier, Ct);
        await Credentials.DeleteAsync(id, Ct);

        var logged = string.Join("\n", _vault.PasswordLog.Entries.Select(e => e.Message));
        Assert.NotEmpty(logged);
        Assert.DoesNotContain("log-me-not", logged, StringComparison.Ordinal);
        Assert.DoesNotContain("pbkdf2", logged, StringComparison.Ordinal);
        Assert.DoesNotContain(verifier, logged, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCacheProbeAllocatesNothingOnAHitOrAMiss()
    {
        var id = NewId();
        var handle = Credentials.Open(id);
        await Credentials.SetAsync(id, "correct horse".AsMemory(), Ct);
        Verifier.Verify(handle, Alice, Password, Client, remember: true);
        var verifier = Verifier;

        AllocAssert.Zero((verifier, handle), static s => s.verifier.TryVerifyCached(s.handle, Alice, Password, Client), "a cache hit");
        AllocAssert.Zero((verifier, handle), static s => s.verifier.TryVerifyCached(s.handle, Alice, "wrong"u8, Client), "a cache miss");
    }

    private sealed class ForeignHandle : IPasswordCredential
    {
        public PasswordCredentialId Id => PasswordCredentialId.Empty;

        public bool IsSet => true;
    }
}
