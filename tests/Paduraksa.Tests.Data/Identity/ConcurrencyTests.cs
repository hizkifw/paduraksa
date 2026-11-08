using Paduraksa.Application.Commands;
using Paduraksa.Application.Identity;
using Paduraksa.Contracts.Identity;
using Paduraksa.Core.Model;

using static Paduraksa.Tests.Data.Identity.IdentityHarness;

namespace Paduraksa.Tests.Data.Identity;

/// <summary>What must hold when requests race: the lockout, the second factor, recovery codes and the last Owner.</summary>
public sealed class ConcurrencyTests
{
    private static readonly IdentityOptions Strict = new() { PasswordIterations = 1_000, MaxFailedAttempts = 5, LockoutDuration = TimeSpan.FromMinutes(10) };

    private static Task<T[]> Parallel<T>(int count, Func<int, Task<T>> run) =>
        Task.WhenAll(Enumerable.Range(0, count).Select(i => Task.Run(() => run(i), Ct)));

    [Fact]
    public async Task ParallelWrongPasswordsEvaluateNoMoreThanTheLimit()
    {
        await using var h = await CreateAsync(Strict);
        await h.CreateUserAsync("alice");

        var results = await Parallel(30, _ => h.Login.LoginAsync("alice", "wrong password entirely", h.Client, Ct));

        Assert.All(results, r => Assert.Equal(LoginStatus.InvalidCredentials, r.Status));
        var failures = await h.AuditAsync("auth.login");
        Assert.Equal(5, failures.Count(e => e.Details!.Contains("wrong_password", StringComparison.Ordinal)));
        Assert.Equal(25, failures.Count(e => e.Details!.Contains("locked", StringComparison.Ordinal)));
        Assert.Single(await h.AuditAsync("auth.lockout"));
    }

    [Fact]
    public async Task ParallelRightPasswordsAfterTheLockAreAllRefused()
    {
        await using var h = await CreateAsync(Strict);
        await h.CreateUserAsync("alice");
        for (var i = 0; i < 5; i++)
        {
            await h.Login.LoginAsync("alice", "wrong password entirely", h.Client, Ct);
        }

        var results = await Parallel(20, _ => h.Login.LoginAsync("alice", Password, h.Client, Ct));

        Assert.All(results, r => Assert.Equal(LoginStatus.InvalidCredentials, r.Status));
        Assert.Equal(0L, await h.Db.ScalarAsync("SELECT count(*) FROM Sessions"));
    }

    [Fact]
    public async Task ARightPasswordRacingTheLastAllowedGuessIsNeverSignedInPastTheLock()
    {
        await using var h = await CreateAsync(new IdentityOptions { PasswordIterations = 1_000, MaxFailedAttempts = 3, LockoutDuration = TimeSpan.FromMinutes(10) });
        await h.CreateUserAsync("alice");

        // 3 wrong and 3 right at once. However they interleave, a session may only exist if its attempt was reserved before the lock,
        // and the account is never left in a state where the lock was reached and a later right password still got in.
        var results = await Parallel(6, i => h.Login.LoginAsync("alice", i % 2 == 0 ? "wrong password entirely" : Password, h.Client, Ct));

        var sessions = (long)(await h.Db.ScalarAsync("SELECT count(*) FROM Sessions"))!;
        Assert.Equal(sessions, results.Count(r => r.Status == LoginStatus.Succeeded));
        var evaluated = (await h.AuditAsync("auth.login")).Count(e => e.Details!.Contains("wrong_password", StringComparison.Ordinal)) + sessions;
        Assert.True(evaluated <= 6);
        // Whatever happened, the next attempt after a lock is refused.
        if ((await h.Users.ListAsync(CommandOrigin.Cli, Ct)).Users[0].Locked)
        {
            Assert.Equal(LoginStatus.InvalidCredentials, (await h.Login.LoginAsync("alice", Password, h.Client, Ct)).Status);
        }
    }

    [Fact]
    public async Task APasswordLockoutDoesNotEndTheOwnersSessions()
    {
        await using var h = await CreateAsync(Strict);
        await h.CreateUserAsync("alice");
        var session = await h.SignInAsync("alice");

        for (var i = 0; i < 5; i++)
        {
            await h.Login.LoginAsync("alice", "wrong password entirely", h.Client, Ct);
        }

        Assert.Equal(SessionStatus.Valid, (await h.Sessions.ValidateAsync(session.CookieValue, Ct)).Status);
    }

    [Fact]
    public async Task ARecoveryCodeUsedByTwoSessionsAtOnceWorksOnce()
    {
        await using var h = await CreateAsync(Strict);
        var user = await h.CreateUserAsync("alice");
        await h.TwoFactor.BeginEnrollmentAsync(h.As(user), Password, Ct);
        var secret = h.Totp.SecretIds[^1];
        var codes = (await h.TwoFactor.ConfirmEnrollmentAsync(h.As(user), h.Totp.CodeFor(secret), Ct)).Codes!.Codes;
        var partials = new List<SessionGrant>();
        for (var i = 0; i < 4; i++)
        {
            partials.Add((await h.Login.LoginAsync("alice", Password, h.Client, Ct)).Session!);
        }

        var results = await Parallel(4, i => h.Login.CompleteSecondFactorAsync(partials[i].CookieValue, null, codes[0], h.Client, Ct));

        Assert.Equal(1, results.Count(r => r.Status == LoginStatus.Succeeded));
        Assert.Equal(1L, await h.Db.ScalarAsync("SELECT count(*) FROM RecoveryCodes WHERE UsedAt IS NOT NULL"));
    }

    [Fact]
    public async Task OwnersDisablingEachOtherAtOnceLeaveOneEnabled()
    {
        await using var h = await CreateAsync();
        var first = await h.CreateUserAsync("first", Role.Owner);
        var second = await h.CreateUserAsync("second", Role.Owner);

        var results = await Parallel(2, i => h.Users.SetDisabledAsync(CommandOrigin.Cli, i == 0 ? first.Id : second.Id, true, Ct));

        Assert.Equal(1, results.Count(r => r.Succeeded));
        Assert.Equal(1, results.Count(r => r.Status == IdentityStatus.LastOwner));
        Assert.Equal(1L, await h.Db.ScalarAsync("SELECT count(*) FROM Users WHERE Disabled = 0"));
    }

    [Fact]
    public async Task OwnersDemotedOrDeletedAtOnceLeaveOne()
    {
        await using var h = await CreateAsync();
        var users = new List<UserSummary>();
        for (var i = 0; i < 4; i++)
        {
            users.Add(await h.CreateUserAsync($"owner{i}", Role.Owner));
        }

        var results = await Parallel(
            4,
            i => i % 2 == 0
                ? h.Users.UpdateAsync(CommandOrigin.Cli, users[i].Id, new UpdateUserRequest(null, [Role.Viewer]), Ct)
                : h.Users.DeleteAsync(CommandOrigin.Cli, users[i].Id, Ct).ContinueWith(t => new UserResult(t.Result.Status), TaskScheduler.Default));

        Assert.True(results.Count(r => r.Status == IdentityStatus.Succeeded) <= 3);
        Assert.True(await h.Setup.IsCompleteAsync(Ct));
        Assert.True((long)(await h.Db.ScalarAsync("SELECT count(*) FROM RoleAssignments r JOIN Users u ON u.Id = r.UserId WHERE r.Role = 'owner' AND u.Disabled = 0"))! >= 1);
    }
}
