using Paduraksa.Application.Commands;
using Paduraksa.Application.Identity;
using Paduraksa.Contracts.Identity;
using Paduraksa.Core.Model;

using static Paduraksa.Tests.Data.Identity.IdentityHarness;

namespace Paduraksa.Tests.Data.Identity;

/// <summary>Signing in: generic refusals, lockout, the second factor, recovery codes, and what is audited.</summary>
public sealed class LoginServiceTests
{
    [Fact]
    public async Task ARightPasswordSignsInAndIsAudited()
    {
        await using var h = await CreateAsync();
        var user = await h.CreateUserAsync("alice", Role.Admin);

        var result = await h.Login.LoginAsync(" Alice ", Password, h.Client, Ct);

        Assert.Equal(LoginStatus.Succeeded, result.Status);
        Assert.Equal(SessionService.CookieValueLength, result.Session!.CookieValue.Length);
        var validation = await h.Sessions.ValidateAsync(result.Session.CookieValue, Ct);
        Assert.Equal(SessionStatus.Valid, validation.Status);
        Assert.Equal(user.Id, validation.Principal!.UserId);
        var audit = Assert.Single(await h.AuditAsync("auth.login"));
        Assert.Equal((ActorKind.User, AuditOutcome.Success, Channel.Dashboard, "203.0.113.9"), (audit.ActorKind, audit.Outcome, audit.Channel, audit.ClientIp));
        Assert.Equal(result.Session.Id.ToString(), audit.Target);
    }

    [Fact]
    public async Task OnlyTheHashOfTheCookieIsStored()
    {
        await using var h = await CreateAsync();
        await h.CreateUserAsync("alice");

        var session = await h.SignInAsync("alice");

        Assert.Equal(32L, await h.Db.ScalarAsync("SELECT length(TokenHash) FROM Sessions"));
        var stored = (byte[])(await h.Db.ScalarAsync("SELECT TokenHash FROM Sessions"))!;
        Assert.Equal(SessionService.HashOf(session.CookieValue), stored);
        Assert.NotEqual(session.CookieValue, Convert.ToBase64String(stored));
        Assert.Equal("test-agent/1.0", await h.Db.ScalarAsync("SELECT UserAgent FROM Sessions"));
    }

    [Theory]
    [InlineData("nobody", Password)]
    [InlineData("alice", "wrong password entirely")]
    [InlineData("", Password)]
    [InlineData("alice", "")]
    public async Task EveryRefusalLooksTheSame(string username, string password)
    {
        await using var h = await CreateAsync();
        await h.CreateUserAsync("alice");

        var result = await h.Login.LoginAsync(username, password, h.Client, Ct);

        Assert.Equal(new LoginResult(LoginStatus.InvalidCredentials), result);
    }

    [Fact]
    public async Task FailuresAreAuditedUnderTheSystemActorWithTheAccountAsTarget()
    {
        await using var h = await CreateAsync();
        var alice = await h.CreateUserAsync("alice");

        await h.Login.LoginAsync("alice", "wrong password entirely", h.Client, Ct);
        await h.Login.LoginAsync("nobody", "whatever whatever", h.Client, Ct);

        var failures = await h.AuditAsync("auth.login");
        Assert.All(failures, f => Assert.Equal((ActorKind.System, AuditOutcome.Denied), (f.ActorKind, f.Outcome)));
        Assert.Equal(alice.Id.ToString(), failures[0].Target);
        Assert.Contains("wrong_password", failures[0].Details, StringComparison.Ordinal);
        Assert.Null(failures[1].Target);
        Assert.Contains("unknown_user", failures[1].Details, StringComparison.Ordinal);
        Assert.DoesNotContain("wrong password entirely", failures[0].Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TooManyFailuresLockTheAccountEvenAgainstTheRightPassword()
    {
        await using var h = await CreateAsync(new IdentityOptions { PasswordIterations = 1_000, MaxFailedAttempts = 3, LockoutDuration = TimeSpan.FromMinutes(10) });
        var alice = await h.CreateUserAsync("alice");
        var live = await h.SignInAsync("alice");

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(LoginStatus.InvalidCredentials, (await h.Login.LoginAsync("alice", "wrong password entirely", h.Client, Ct)).Status);
        }

        Assert.Equal(LoginStatus.InvalidCredentials, (await h.Login.LoginAsync("alice", Password, h.Client, Ct)).Status);
        Assert.True((await h.Users.GetAsync(CommandOrigin.Cli, alice.Id, Ct)).User!.Locked);
        Assert.Equal(SessionStatus.Valid, (await h.Sessions.ValidateAsync(live.CookieValue, Ct)).Status); // a password-stage lockout ends nothing
        var lockout = Assert.Single(await h.AuditAsync("auth.lockout"));
        Assert.Equal(alice.Id.ToString(), lockout.Target);

        h.Time.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
        Assert.Equal(LoginStatus.Succeeded, (await h.Login.LoginAsync("alice", Password, h.Client, Ct)).Status);
    }

    [Fact]
    public async Task ASuccessResetsTheFailureCountAndAWrongPasswordAfterTheLockoutStartsAfresh()
    {
        await using var h = await CreateAsync(new IdentityOptions { PasswordIterations = 1_000, MaxFailedAttempts = 3, LockoutDuration = TimeSpan.FromMinutes(10) });
        await h.CreateUserAsync("alice");

        await h.Login.LoginAsync("alice", "wrong password entirely", h.Client, Ct);
        await h.Login.LoginAsync("alice", "wrong password entirely", h.Client, Ct);
        Assert.Equal(LoginStatus.Succeeded, (await h.Login.LoginAsync("alice", Password, h.Client, Ct)).Status);
        await h.Login.LoginAsync("alice", "wrong password entirely", h.Client, Ct);
        await h.Login.LoginAsync("alice", "wrong password entirely", h.Client, Ct);

        Assert.Equal(LoginStatus.Succeeded, (await h.Login.LoginAsync("alice", Password, h.Client, Ct)).Status); // never reached three in a row
        Assert.Empty(await h.AuditAsync("auth.lockout"));
    }

    [Fact]
    public async Task APasswordHashMadeWithLowerCostIsRehashedAtSignIn()
    {
        await using var h = await CreateAsync();
        await h.CreateUserAsync("alice");
        var weak = new PasswordHasher(1_000).Hash(Password);
        await h.Db.ExecuteAsync($"UPDATE Users SET PasswordHash = '{weak}'");
        await using var stronger = await CreateAsync(new IdentityOptions { PasswordIterations = 2_000 });
        var user = await stronger.CreateUserAsync("bob");
        await stronger.Db.ExecuteAsync($"UPDATE Users SET PasswordHash = '{weak}'");

        Assert.Equal(LoginStatus.Succeeded, (await stronger.Login.LoginAsync("bob", Password, stronger.Client, Ct)).Status);

        Assert.Contains("$i=2000$", (string)(await stronger.Db.ScalarAsync("SELECT PasswordHash FROM Users"))!, StringComparison.Ordinal);
        Assert.NotNull(user);
    }

    [Fact]
    public async Task ADisabledUserCannotSignInEvenWithTheRightPassword()
    {
        await using var h = await CreateAsync();
        var user = await h.CreateUserAsync("alice");
        await h.Users.SetDisabledAsync(CommandOrigin.Cli, user.Id, true, Ct);

        Assert.Equal(LoginStatus.InvalidCredentials, (await h.Login.LoginAsync("alice", Password, h.Client, Ct)).Status);
        Assert.Contains("disabled", (await h.AuditAsync("auth.login"))[^1].Details, StringComparison.Ordinal);
    }

    // --- second factor ---------------------------------------------------------------------------------------------

    private static async Task<(UserSummary User, SecretId Secret, IReadOnlyList<string> Recovery)> EnrolledAsync(IdentityHarness h, string name = "alice")
    {
        var user = await h.CreateUserAsync(name);
        var begun = await h.TwoFactor.BeginEnrollmentAsync(h.As(user), Password, Ct);
        Assert.True(begun.Status == IdentityStatus.Succeeded);
        var secret = h.Totp.SecretIds[^1];
        var confirmed = await h.TwoFactor.ConfirmEnrollmentAsync(h.As(user), h.Totp.CodeFor(secret), Ct);
        Assert.Equal(IdentityStatus.Succeeded, confirmed.Status);
        return (user, secret, confirmed.Codes!.Codes);
    }

    [Fact]
    public async Task AUserWithASecondFactorGetsAPartialSessionFirst()
    {
        await using var h = await CreateAsync();
        var (user, secret, _) = await EnrolledAsync(h);

        var first = await h.Login.LoginAsync("alice", Password, h.Client, Ct);

        Assert.Equal(LoginStatus.SecondFactorRequired, first.Status);
        var partial = await h.Sessions.ValidateAsync(first.Session!.CookieValue, Ct);
        Assert.Equal(SessionStatus.SecondFactorRequired, partial.Status);
        Assert.Equal(user.Id, partial.Principal!.UserId);
        Assert.Equal(h.Time.Now + h.Options.PartialSessionLifetime, first.Session.ExpiresAt);

        h.Totp.Rotate(secret);
        var second = await h.Login.CompleteSecondFactorAsync(first.Session.CookieValue, h.Totp.CodeFor(secret), null, h.Client, Ct);

        Assert.Equal(LoginStatus.Succeeded, second.Status);
        Assert.Equal(first.Session.Id, second.Session!.Id);
        Assert.NotEqual(first.Session.CookieValue, second.Session.CookieValue); // the cookie changes with the privilege
        Assert.Equal(SessionStatus.Invalid, (await h.Sessions.ValidateAsync(first.Session.CookieValue, Ct)).Status);
        Assert.Equal(SessionStatus.Valid, (await h.Sessions.ValidateAsync(second.Session.CookieValue, Ct)).Status);
        Assert.Equal(h.Time.Now + h.Options.SessionLifetime, second.Session.ExpiresAt);
        Assert.Contains("totp", Assert.Single(await h.AuditAsync("auth.second_factor")).Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APartialSessionIsNoMoreThanAPasswordProof()
    {
        await using var h = await CreateAsync();
        await EnrolledAsync(h);
        var first = await h.Login.LoginAsync("alice", Password, h.Client, Ct);

        // It is not a full session anywhere that asks for one, and it cannot be completed after it expires.
        Assert.NotEqual(SessionStatus.Valid, (await h.Sessions.ValidateAsync(first.Session!.CookieValue, Ct)).Status);
        h.Time.Advance(h.Options.PartialSessionLifetime + TimeSpan.FromSeconds(1));
        var late = await h.Login.CompleteSecondFactorAsync(first.Session.CookieValue, "100001", null, h.Client, Ct);

        Assert.Equal(LoginStatus.SessionExpired, late.Status);
    }

    [Fact]
    public async Task WrongCodesAreRefusedCountedAndLockTheAccount()
    {
        await using var h = await CreateAsync(new IdentityOptions { PasswordIterations = 1_000, MaxFailedAttempts = 4 });
        var (user, _, _) = await EnrolledAsync(h);
        var first = await h.Login.LoginAsync("alice", Password, h.Client, Ct);

        Assert.Equal(LoginStatus.InvalidCode, (await h.Login.CompleteSecondFactorAsync(first.Session!.CookieValue, "000000", null, h.Client, Ct)).Status);
        Assert.Equal(LoginStatus.InvalidCode, (await h.Login.CompleteSecondFactorAsync(first.Session.CookieValue, "000001", null, h.Client, Ct)).Status);
        var last = await h.Login.CompleteSecondFactorAsync(first.Session.CookieValue, "000002", null, h.Client, Ct);

        Assert.Equal(LoginStatus.SessionExpired, last.Status);
        Assert.True((await h.Users.GetAsync(CommandOrigin.Cli, user.Id, Ct)).User!.Locked);
        Assert.Equal(LoginStatus.InvalidCredentials, (await h.Login.LoginAsync("alice", Password, h.Client, Ct)).Status);
        Assert.Single(await h.AuditAsync("auth.lockout"));
    }

    [Fact]
    public async Task ExactlyOneOfCodeAndRecoveryCodeIsNeeded()
    {
        await using var h = await CreateAsync();
        var (_, _, recovery) = await EnrolledAsync(h);
        var first = await h.Login.LoginAsync("alice", Password, h.Client, Ct);

        Assert.Equal(LoginStatus.InvalidCode, (await h.Login.CompleteSecondFactorAsync(first.Session!.CookieValue, null, null, h.Client, Ct)).Status);
        Assert.Equal(LoginStatus.InvalidCode, (await h.Login.CompleteSecondFactorAsync(first.Session.CookieValue, "100001", recovery[0], h.Client, Ct)).Status);
    }

    [Fact]
    public async Task ARecoveryCodeWorksOnceCaseAndDashesAside()
    {
        await using var h = await CreateAsync();
        var (_, _, recovery) = await EnrolledAsync(h);
        Assert.Equal(IdentityOptions.RecoveryCodeCount, recovery.Count);
        Assert.Equal(recovery.Count, recovery.Distinct().Count());

        var first = await h.Login.LoginAsync("alice", Password, h.Client, Ct);
        var typed = recovery[0].ToUpperInvariant().Replace("-", " ", StringComparison.Ordinal);
        var used = await h.Login.CompleteSecondFactorAsync(first.Session!.CookieValue, null, typed, h.Client, Ct);

        Assert.Equal(LoginStatus.Succeeded, used.Status);
        var again = await h.Login.LoginAsync("alice", Password, h.Client, Ct);
        Assert.Equal(LoginStatus.InvalidCode, (await h.Login.CompleteSecondFactorAsync(again.Session!.CookieValue, null, recovery[0], h.Client, Ct)).Status);
        Assert.Equal(LoginStatus.Succeeded, (await h.Login.CompleteSecondFactorAsync(again.Session.CookieValue, null, recovery[1], h.Client, Ct)).Status);
        Assert.Contains("recovery_code", (await h.AuditAsync("auth.second_factor")).First().Details, StringComparison.Ordinal);
        Assert.Equal(2L, await h.Db.ScalarAsync("SELECT count(*) FROM RecoveryCodes WHERE UsedAt IS NOT NULL"));
    }

    [Fact]
    public async Task OnlyHashesOfTheRecoveryCodesAreStored()
    {
        await using var h = await CreateAsync();
        var (_, _, recovery) = await EnrolledAsync(h);

        var stored = (await h.Db.QueryAsync("SELECT hex(CodeHash) FROM RecoveryCodes")).Select(r => (string)r[0]!).ToList();

        Assert.Equal(10, stored.Count);
        Assert.All(recovery, c => Assert.DoesNotContain(stored, s => s.Contains(c.Replace("-", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(Convert.ToHexString(TwoFactorService.HashRecoveryCode(recovery[3])), stored.Single(s => s == Convert.ToHexString(TwoFactorService.HashRecoveryCode(recovery[3]))));
    }

    [Fact]
    public async Task ACodeCannotBeReusedAndAnUnenrolledSessionCannotUseTheSecondStep()
    {
        await using var h = await CreateAsync();
        var (_, secret, _) = await EnrolledAsync(h);
        var first = await h.Login.LoginAsync("alice", Password, h.Client, Ct);
        h.Totp.Rotate(secret);
        var code = h.Totp.CodeFor(secret);
        Assert.Equal(LoginStatus.Succeeded, (await h.Login.CompleteSecondFactorAsync(first.Session!.CookieValue, code, null, h.Client, Ct)).Status);

        var second = await h.Login.LoginAsync("alice", Password, h.Client, Ct);
        Assert.Equal(LoginStatus.InvalidCode, (await h.Login.CompleteSecondFactorAsync(second.Session!.CookieValue, code, null, h.Client, Ct)).Status);

        var plain = await h.CreateUserAsync("bob");
        var full = await h.SignInAsync("bob");
        Assert.Equal(LoginStatus.SessionExpired, (await h.Login.CompleteSecondFactorAsync(full.CookieValue, "100000", null, h.Client, Ct)).Status);
        Assert.Equal(LoginStatus.SessionExpired, (await h.Login.CompleteSecondFactorAsync("garbage", "100000", null, h.Client, Ct)).Status);
        Assert.NotNull(plain);
    }

    [Fact]
    public async Task EnrolmentIsOnlyOnAfterConfirmationAndOffAgainOnDisable()
    {
        await using var h = await CreateAsync();
        var user = await h.CreateUserAsync("alice");
        var begun = await h.TwoFactor.BeginEnrollmentAsync(h.As(user), Password, Ct);

        Assert.Equal("otpauth", begun.Enrollment!.ProvisioningUri.Scheme);
        Assert.Equal(LoginStatus.Succeeded, (await h.Login.LoginAsync("alice", Password, h.Client, Ct)).Status); // abandoned enrolment locks nobody out
        var secret = h.Totp.SecretIds.Single();
        Assert.Equal(IdentityStatus.InvalidCredentials, (await h.TwoFactor.ConfirmEnrollmentAsync(h.As(user), "999999", Ct)).Status);
        Assert.Equal(IdentityStatus.Succeeded, (await h.TwoFactor.ConfirmEnrollmentAsync(h.As(user), h.Totp.CodeFor(secret), Ct)).Status);
        Assert.Equal(IdentityStatus.Conflict, (await h.TwoFactor.BeginEnrollmentAsync(h.As(user), Password, Ct)).Status);
        Assert.Equal(LoginStatus.SecondFactorRequired, (await h.Login.LoginAsync("alice", Password, h.Client, Ct)).Status);

        Assert.Equal(IdentityStatus.InvalidCredentials, (await h.TwoFactor.DisableAsync(h.As(user), "wrong password entirely", Ct)).Status);
        Assert.Equal(IdentityStatus.Succeeded, (await h.TwoFactor.DisableAsync(h.As(user), Password, Ct)).Status);

        Assert.Equal(0, h.Totp.Count);
        Assert.Equal(0L, await h.Db.ScalarAsync("SELECT count(*) FROM RecoveryCodes"));
        Assert.Equal(LoginStatus.Succeeded, (await h.Login.LoginAsync("alice", Password, h.Client, Ct)).Status);
        Assert.Equal(["user.totp.enroll", "user.totp.confirm", "user.totp.confirm", "user.totp.disable", "user.totp.disable"], (await h.AuditAsync()).Select(e => e.Action).Where(a => a.StartsWith("user.totp", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task StartingAgainDiscardsTheAbandonedSecret()
    {
        await using var h = await CreateAsync();
        var user = await h.CreateUserAsync("alice");

        await h.TwoFactor.BeginEnrollmentAsync(h.As(user), Password, Ct);
        await h.TwoFactor.BeginEnrollmentAsync(h.As(user), Password, Ct);

        Assert.Equal(1, h.Totp.Count);
    }

    [Fact]
    public async Task TheAdminCanTurnOffSomeoneElsesSecondFactorFromTheCli()
    {
        await using var h = await CreateAsync();
        var (user, _, _) = await EnrolledAsync(h);
        var first = await h.Login.LoginAsync("alice", Password, h.Client, Ct);

        var result = await h.TwoFactor.DisableForUserAsync(CommandOrigin.Cli, "alice", Ct);

        Assert.True(result.Succeeded);
        Assert.False((await h.Users.GetAsync(CommandOrigin.Cli, user.Id, Ct)).User!.TwoFactorEnabled);
        Assert.Equal(SessionStatus.Invalid, (await h.Sessions.ValidateAsync(first.Session!.CookieValue, Ct)).Status);
        var audit = (await h.AuditAsync("user.totp.disable")).Single();
        Assert.Equal(ActorKind.Cli, audit.ActorKind);
        Assert.Equal(IdentityStatus.Denied, (await h.TwoFactor.DisableForUserAsync(h.As(user), "alice", Ct)).Status);
        Assert.Equal(IdentityStatus.NotFound, (await h.TwoFactor.DisableForUserAsync(CommandOrigin.Cli, "nobody", Ct)).Status);
    }

    [Fact]
    public async Task RegeneratingRecoveryCodesReplacesTheOldOnesAndNeedsThePassword()
    {
        await using var h = await CreateAsync();
        var (user, _, old) = await EnrolledAsync(h);

        Assert.Equal(IdentityStatus.InvalidCredentials, (await h.TwoFactor.RegenerateRecoveryCodesAsync(h.As(user), "wrong password entirely", Ct)).Status);
        var fresh = await h.TwoFactor.RegenerateRecoveryCodesAsync(h.As(user), Password, Ct);

        Assert.Equal(10, fresh.Codes!.Codes.Count);
        Assert.Empty(fresh.Codes.Codes.Intersect(old));
        var first = await h.Login.LoginAsync("alice", Password, h.Client, Ct);
        Assert.Equal(LoginStatus.InvalidCode, (await h.Login.CompleteSecondFactorAsync(first.Session!.CookieValue, null, old[0], h.Client, Ct)).Status);
        Assert.Equal(LoginStatus.Succeeded, (await h.Login.CompleteSecondFactorAsync(first.Session.CookieValue, null, fresh.Codes.Codes[0], h.Client, Ct)).Status);
    }

    [Fact]
    public async Task AnUnknownUsernameIsNeverWrittenToTheAuditLog()
    {
        await using var h = await CreateAsync();

        // People type their password into the username box; the hash-chained log is permanent.
        await h.Login.LoginAsync("hunter2-my-real-password", "whatever whatever", h.Client, Ct);

        var audit = Assert.Single(await h.AuditAsync("auth.login"));
        Assert.DoesNotContain("hunter2", audit.Details, StringComparison.Ordinal);
        Assert.Contains("unknown_user", audit.Details, StringComparison.Ordinal);
        Assert.Equal("203.0.113.9", audit.ClientIp);
    }

    [Fact]
    public async Task AnOverlongPasswordIsRefusedByTheSameMeasureThePolicyUses()
    {
        await using var h = await CreateAsync();
        await h.CreateUserAsync("alice");
        var atLimit = string.Concat(Enumerable.Repeat("\U0001F511", IdentityOptions.MaxPasswordLength)); // 1024 runes, 2048 UTF-16 units

        Assert.Equal(LoginStatus.InvalidCredentials, (await h.Login.LoginAsync("alice", atLimit + "x", h.Client, Ct)).Status);
        Assert.Equal(IdentityStatus.Succeeded, (await h.Users.ResetPasswordAsync(CommandOrigin.Cli, "alice", atLimit, Ct)).Status);
        Assert.Equal(LoginStatus.Succeeded, (await h.Login.LoginAsync("alice", atLimit, h.Client, Ct)).Status);
    }

    [Fact]
    public async Task EnrolmentNeedsTheCurrentPasswordAndAWrongOneStartsNothing()
    {
        await using var h = await CreateAsync();
        var user = await h.CreateUserAsync("alice");

        var wrong = await h.TwoFactor.BeginEnrollmentAsync(h.As(user), "wrong password entirely", Ct);

        Assert.Equal(IdentityStatus.InvalidCredentials, wrong.Status);
        Assert.Equal(0, h.Totp.Count);
        Assert.Contains(await h.AuditAsync("user.totp.enroll"), e => e.Outcome == AuditOutcome.Denied);
        Assert.Equal(IdentityStatus.Denied, (await h.TwoFactor.BeginEnrollmentAsync(CommandOrigin.Cli, Password, Ct)).Status);
    }

    [Fact]
    public async Task ALockoutAtASensitiveStepEndsEverySessionOfTheUser()
    {
        await using var h = await CreateAsync(new IdentityOptions { PasswordIterations = 1_000, MaxFailedAttempts = 3 });
        var user = await h.CreateUserAsync("alice");
        var session = await h.SignInAsync("alice");

        for (var i = 0; i < 3; i++)
        {
            await h.TwoFactor.BeginEnrollmentAsync(h.As(user), "wrong password entirely", Ct);
        }

        Assert.Equal(SessionStatus.Invalid, (await h.Sessions.ValidateAsync(session.CookieValue, Ct)).Status);
    }
}
