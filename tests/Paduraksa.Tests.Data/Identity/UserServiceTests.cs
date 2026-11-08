using Microsoft.EntityFrameworkCore;

using Paduraksa.Application.Commands;
using Paduraksa.Application.Identity;
using Paduraksa.Contracts.Identity;
using Paduraksa.Core.Model;

using static Paduraksa.Tests.Data.Identity.IdentityHarness;

namespace Paduraksa.Tests.Data.Identity;

/// <summary>User management: permissions, validation, the last-Owner guard, password operations, audit.</summary>
public sealed class UserServiceTests
{
    [Fact]
    public async Task CreatingAUserStoresAHashAndAuditsIt()
    {
        await using var h = await CreateAsync();

        var created = await h.Users.CreateAsync(CommandOrigin.Cli, new CreateUserRequest("Alice", " Alice A ", Password, [Role.Admin, Role.Admin]), Ct);

        Assert.True(created.Succeeded);
        Assert.Equal(("Alice", "Alice A", false), (created.User!.Username, created.User.DisplayName, created.User.Disabled));
        Assert.Equal([Role.Admin], created.User.Roles);
        var stored = (string)(await h.Db.ScalarAsync("SELECT PasswordHash FROM Users"))!;
        Assert.StartsWith("$pbkdf2-sha512$v1$i=1000$", stored, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, stored, StringComparison.Ordinal);
        var audit = Assert.Single(await h.AuditAsync("user.create"));
        Assert.Equal((ActorKind.Cli, AuditOutcome.Success, created.User.Id.ToString()), (audit.ActorKind, audit.Outcome, audit.Target));
        Assert.DoesNotContain(Password, audit.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UsernamesAreUniqueIgnoringCaseAndValidated()
    {
        await using var h = await CreateAsync();
        await h.CreateUserAsync("alice");

        Assert.Equal(IdentityStatus.Conflict, (await h.Users.CreateAsync(CommandOrigin.Cli, new("ALICE", null, Password, [Role.Viewer]), Ct)).Status);
        foreach (var bad in new[] { "", "ab", "has space", "semi;colon", new string('a', 65), "tab\tname" })
        {
            Assert.Equal(IdentityStatus.Invalid, (await h.Users.CreateAsync(CommandOrigin.Cli, new(bad, null, Password, [Role.Viewer]), Ct)).Status);
        }
    }

    [Fact]
    public async Task PasswordsNeedTheMinimumLengthAndNothingElse()
    {
        await using var h = await CreateAsync();

        var tooShort = await h.Users.CreateAsync(CommandOrigin.Cli, new("bob", null, "short", [Role.Viewer]), Ct);
        var plain = await h.Users.CreateAsync(CommandOrigin.Cli, new("bob", null, "aaaaaaaaaaaa", [Role.Viewer]), Ct);

        Assert.Equal(IdentityStatus.Invalid, tooShort.Status);
        Assert.Contains("at least 12", tooShort.Problem, StringComparison.Ordinal);
        Assert.True(plain.Succeeded); // no composition rules
    }

    [Fact]
    public async Task AUserNeedsAtLeastOneRoleAndADefinedOne()
    {
        await using var h = await CreateAsync();

        Assert.Equal(IdentityStatus.Invalid, (await h.Users.CreateAsync(CommandOrigin.Cli, new("carol", null, Password, []), Ct)).Status);
        Assert.Equal(IdentityStatus.Invalid, (await h.Users.CreateAsync(CommandOrigin.Cli, new("carol", null, Password, [(Role)99]), Ct)).Status);
    }

    [Fact]
    public async Task OnlyAnOwnerManagesUsersAndRefusalsAreAudited()
    {
        await using var h = await CreateAsync();
        var admin = await h.CreateUserAsync("admin", Role.Admin);
        var owner = await h.CreateUserAsync("owner", Role.Owner);

        var denied = await h.Users.CreateAsync(h.As(admin), new("x-user", null, Password, [Role.Viewer]), Ct);
        var allowed = await h.Users.CreateAsync(h.As(owner), new("x-user", null, Password, [Role.Viewer]), Ct);

        Assert.Equal(IdentityStatus.Denied, denied.Status);
        Assert.True(allowed.Succeeded);
        var audit = Assert.Single(await h.AuditAsync(), e => e.Outcome == AuditOutcome.Denied);
        Assert.Equal((ActorKind.User, "user.create", admin.Id), (audit.ActorKind, audit.Action, audit.ActorUserId));
        Assert.Equal(IdentityStatus.Denied, (await h.Users.ListAsync(h.As(new UserSummary(UserId.New(), "x", "x", [], false, false, false, default, default)), Ct)).Status);
    }

    [Fact]
    public async Task ListAndGetReturnSummariesOnly()
    {
        await using var h = await CreateAsync();
        var owner = await h.CreateUserAsync("owner", Role.Owner);
        var viewer = await h.CreateUserAsync("viewer");

        var list = await h.Users.ListAsync(h.As(owner), Ct);
        var self = await h.Users.GetAsync(h.As(viewer), viewer.Id, Ct);
        var other = await h.Users.GetAsync(h.As(viewer), owner.Id, Ct);

        Assert.Equal(["owner", "viewer"], list.Users.Select(u => u.Username));
        Assert.True(self.Succeeded);
        Assert.Equal(IdentityStatus.Denied, other.Status);
        Assert.Equal(IdentityStatus.NotFound, (await h.Users.GetAsync(CommandOrigin.Cli, UserId.New(), Ct)).Status);
    }

    [Fact]
    public async Task UpdatingReplacesRolesAndDisplayName()
    {
        await using var h = await CreateAsync();
        var user = await h.CreateUserAsync("dave");

        var updated = await h.Users.UpdateAsync(CommandOrigin.Cli, user.Id, new UpdateUserRequest("Dave D", [Role.Operator, Role.Auditor]), Ct);

        Assert.Equal("Dave D", updated.User!.DisplayName);
        Assert.Equal([Role.Operator, Role.Auditor], updated.User.Roles);
        Assert.Equal(2L, await h.Db.ScalarAsync("SELECT count(*) FROM RoleAssignments"));
        Assert.Single(await h.AuditAsync("user.update"));
    }

    [Fact]
    public async Task UpdatingAppliesNameRolesAndEnabledStateInOneTransaction()
    {
        await using var h = await CreateAsync();
        var owner = await h.CreateUserAsync("owner", Role.Owner);
        var user = await h.CreateUserAsync("dave");

        var disabled = await h.Users.UpdateAsync(CommandOrigin.Cli, user.Id, new UpdateUserRequest("Dave D", [Role.Operator], Disabled: true), Ct);

        Assert.Equal(("Dave D", true), (disabled.User!.DisplayName, disabled.User.Disabled));
        Assert.Equal([Role.Operator], disabled.User.Roles);
        var audit = Assert.Single(await h.AuditAsync("user.update"));
        Assert.Contains("\"disabled\":true", audit.Details, StringComparison.Ordinal);
        Assert.False((await h.Users.UpdateAsync(CommandOrigin.Cli, user.Id, new UpdateUserRequest(Disabled: false), Ct)).User!.Disabled);

        // A refusal by the last-Owner guard, whichever part trips it, changes nothing: not even the name.
        foreach (var request in new UpdateUserRequest[] { new("Renamed", null, true), new("Renamed", [Role.Admin]), new("Renamed", [Role.Admin], true) })
        {
            Assert.Equal(IdentityStatus.LastOwner, (await h.Users.UpdateAsync(CommandOrigin.Cli, owner.Id, request, Ct)).Status);
        }

        var unchanged = (await h.Users.GetAsync(CommandOrigin.Cli, owner.Id, Ct)).User!;
        Assert.Equal(("owner", false), (unchanged.DisplayName, unchanged.Disabled));
        Assert.Equal([Role.Owner], unchanged.Roles);
    }

    [Fact]
    public async Task TheLastEnabledOwnerCannotBeDemotedDisabledOrDeleted()
    {
        await using var h = await CreateAsync();
        var owner = await h.CreateUserAsync("owner", Role.Owner);

        Assert.Equal(IdentityStatus.LastOwner, (await h.Users.UpdateAsync(CommandOrigin.Cli, owner.Id, new(null, [Role.Admin]), Ct)).Status);
        Assert.Equal(IdentityStatus.LastOwner, (await h.Users.SetDisabledAsync(CommandOrigin.Cli, owner.Id, true, Ct)).Status);
        Assert.Equal(IdentityStatus.LastOwner, (await h.Users.DeleteAsync(CommandOrigin.Cli, owner.Id, Ct)).Status);

        var second = await h.CreateUserAsync("second", Role.Owner);
        Assert.True((await h.Users.UpdateAsync(CommandOrigin.Cli, owner.Id, new(null, [Role.Admin]), Ct)).Succeeded);
        // Only one Owner is left again.
        Assert.Equal(IdentityStatus.LastOwner, (await h.Users.SetDisabledAsync(CommandOrigin.Cli, second.Id, true, Ct)).Status);
        Assert.Equal(IdentityStatus.LastOwner, (await h.Users.DeleteAsync(CommandOrigin.Cli, second.Id, Ct)).Status);
    }

    [Fact]
    public async Task ADisabledOwnerDoesNotCountAsAnOwner()
    {
        await using var h = await CreateAsync();
        var first = await h.CreateUserAsync("first", Role.Owner);
        var second = await h.CreateUserAsync("second", Role.Owner);
        Assert.True((await h.Users.SetDisabledAsync(CommandOrigin.Cli, first.Id, true, Ct)).Succeeded);

        Assert.Equal(IdentityStatus.LastOwner, (await h.Users.SetDisabledAsync(CommandOrigin.Cli, second.Id, true, Ct)).Status);
        Assert.Equal(IdentityStatus.LastOwner, (await h.Users.UpdateAsync(CommandOrigin.Cli, second.Id, new(null, [Role.Viewer]), Ct)).Status);
        // The disabled one can go: it was never the safety net.
        Assert.True((await h.Users.DeleteAsync(CommandOrigin.Cli, first.Id, Ct)).Succeeded);
    }

    [Fact]
    public async Task DisablingEndsSessionsAndEnablingClearsALockout()
    {
        await using var h = await CreateAsync();
        var user = await h.CreateUserAsync("erin");
        var session = await h.SignInAsync("erin");

        var disabled = await h.Users.SetDisabledAsync(CommandOrigin.Cli, user.Id, true, Ct);

        Assert.True(disabled.User!.Disabled);
        Assert.Equal(SessionStatus.Invalid, (await h.Sessions.ValidateAsync(session.CookieValue, Ct)).Status);
        Assert.Equal(LoginStatus.InvalidCredentials, (await h.Login.LoginAsync("erin", Password, h.Client, Ct)).Status);
        Assert.True((await h.Users.SetDisabledAsync(CommandOrigin.Cli, user.Id, false, Ct)).Succeeded);
        Assert.Equal(LoginStatus.Succeeded, (await h.Login.LoginAsync("erin", Password, h.Client, Ct)).Status);
    }

    [Fact]
    public async Task DeletingRemovesTheUserWithTokensAndSessions()
    {
        await using var h = await CreateAsync();
        var user = await h.CreateUserAsync("frank");
        await h.SignInAsync("frank");
        var issued = await h.Tokens.IssueAsync(h.As(user), new IssueApiTokenRequest("ci", [Permission.ConfigRead], null), Ct);

        Assert.True((await h.Users.DeleteAsync(CommandOrigin.Cli, user.Id, Ct)).Succeeded);

        Assert.Equal(0L, await h.Db.ScalarAsync("SELECT count(*) FROM Users"));
        Assert.Equal(0L, await h.Db.ScalarAsync("SELECT count(*) FROM Sessions"));
        Assert.False((await h.Tokens.VerifyAsync(issued.Issued!.Token, Ct)).IsValid);
        Assert.Equal(IdentityStatus.NotFound, (await h.Users.DeleteAsync(CommandOrigin.Cli, user.Id, Ct)).Status);
    }

    [Fact]
    public async Task DeletingAUserDeletesTheirVaultSecret()
    {
        await using var h = await CreateAsync();
        var user = await h.CreateUserAsync("gina");
        await h.TwoFactor.BeginEnrollmentAsync(h.As(user), Password, Ct);
        Assert.Equal(1, h.Totp.Count);

        await h.Users.DeleteAsync(CommandOrigin.Cli, user.Id, Ct);

        Assert.Equal(0, h.Totp.Count);
    }

    [Fact]
    public async Task ChangingYourPasswordNeedsTheCurrentOneAndEndsOtherSessions()
    {
        await using var h = await CreateAsync();
        var user = await h.CreateUserAsync("hank");
        var keep = await h.SignInAsync("hank");
        var other = await h.SignInAsync("hank");
        var keepId = (await h.Sessions.ValidateAsync(keep.CookieValue, Ct)).Principal!.SessionId;

        var wrong = await h.Users.ChangePasswordAsync(h.As(user), new("not the password", "a brand new passphrase"), keepId, Ct);
        var changed = await h.Users.ChangePasswordAsync(h.As(user), new(Password, "a brand new passphrase"), keepId, Ct);

        Assert.Equal(IdentityStatus.InvalidCredentials, wrong.Status);
        Assert.True(changed.Succeeded);
        Assert.Equal(SessionStatus.Valid, (await h.Sessions.ValidateAsync(keep.CookieValue, Ct)).Status);
        Assert.Equal(SessionStatus.Invalid, (await h.Sessions.ValidateAsync(other.CookieValue, Ct)).Status);
        Assert.Equal(LoginStatus.InvalidCredentials, (await h.Login.LoginAsync("hank", Password, h.Client, Ct)).Status);
        Assert.Equal(LoginStatus.Succeeded, (await h.Login.LoginAsync("hank", "a brand new passphrase", h.Client, Ct)).Status);
        Assert.Equal([AuditOutcome.Denied, AuditOutcome.Success], (await h.AuditAsync("user.password.change")).Select(e => e.Outcome));
    }

    [Fact]
    public async Task ChangingAPasswordChecksThePolicyAndNeedsAUser()
    {
        await using var h = await CreateAsync();
        var user = await h.CreateUserAsync("ivy");

        Assert.Equal(IdentityStatus.Invalid, (await h.Users.ChangePasswordAsync(h.As(user), new(Password, "short"), null, Ct)).Status);
        Assert.Equal(IdentityStatus.Denied, (await h.Users.ChangePasswordAsync(CommandOrigin.Cli, new(Password, "a brand new passphrase"), null, Ct)).Status);
    }

    [Fact]
    public async Task WrongCurrentPasswordsCountTowardTheLockout()
    {
        await using var h = await CreateAsync(new IdentityOptions { PasswordIterations = 1_000, MaxFailedAttempts = 3 });
        var user = await h.CreateUserAsync("jack");

        for (var i = 0; i < 3; i++)
        {
            await h.Users.ChangePasswordAsync(h.As(user), new("wrong", "a brand new passphrase"), null, Ct);
        }

        Assert.Equal(IdentityStatus.InvalidCredentials, (await h.Users.ChangePasswordAsync(h.As(user), new(Password, "a brand new passphrase"), null, Ct)).Status);
        Assert.True((await h.Users.GetAsync(CommandOrigin.Cli, user.Id, Ct)).User!.Locked);
    }

    [Fact]
    public async Task ResetPasswordSetsANewOneEndsSessionsAndClearsTheLockout()
    {
        await using var h = await CreateAsync(new IdentityOptions { PasswordIterations = 1_000, MaxFailedAttempts = 2 });
        var user = await h.CreateUserAsync("kim");
        var session = await h.SignInAsync("kim");
        await h.Login.LoginAsync("kim", "wrong", h.Client, Ct);
        await h.Login.LoginAsync("kim", "wrong", h.Client, Ct);
        Assert.True((await h.Users.GetAsync(CommandOrigin.Cli, user.Id, Ct)).User!.Locked);

        var reset = await h.Users.ResetPasswordAsync(CommandOrigin.Cli, "KIM", "a brand new passphrase", Ct);

        Assert.True(reset.Succeeded);
        Assert.Equal(SessionStatus.Invalid, (await h.Sessions.ValidateAsync(session.CookieValue, Ct)).Status);
        Assert.Equal(LoginStatus.Succeeded, (await h.Login.LoginAsync("kim", "a brand new passphrase", h.Client, Ct)).Status);
        var audit = Assert.Single(await h.AuditAsync("user.password.reset"));
        Assert.Equal(ActorKind.Cli, audit.ActorKind);
        Assert.Equal(IdentityStatus.NotFound, (await h.Users.ResetPasswordAsync(CommandOrigin.Cli, "nobody", "a brand new passphrase", Ct)).Status);
        Assert.Equal(IdentityStatus.Denied, (await h.Users.ResetPasswordAsync(h.As(user), "kim", "a brand new passphrase", Ct)).Status);
    }

    [Fact]
    public async Task NoSummaryCarriesAHashOrASecretReference()
    {
        await using var h = await CreateAsync();
        var user = await h.CreateUserAsync("lena");

        var json = System.Text.Json.JsonSerializer.Serialize(user, IdentityJsonContext.Default.UserSummary);

        Assert.DoesNotContain("pbkdf2", json, StringComparison.Ordinal);
        Assert.DoesNotContain("sec_", json, StringComparison.Ordinal);
        Assert.DoesNotContain("assword", json, StringComparison.Ordinal);
        await using var context = h.Db.NewContext();
        Assert.Equal(1, await context.Users.CountAsync(Ct));
    }

    [Fact]
    public async Task ALockoutAtChangePasswordEndsEverySessionIncludingTheCallers()
    {
        await using var h = await CreateAsync(new IdentityOptions { PasswordIterations = 1_000, MaxFailedAttempts = 3 });
        var user = await h.CreateUserAsync("mona");
        var current = await h.SignInAsync("mona");
        var other = await h.SignInAsync("mona");
        var keep = (await h.Sessions.ValidateAsync(current.CookieValue, Ct)).Principal!.SessionId;

        for (var i = 0; i < 3; i++)
        {
            await h.Users.ChangePasswordAsync(h.As(user), new("wrong", "a brand new passphrase"), keep, Ct);
        }

        Assert.Equal(SessionStatus.Invalid, (await h.Sessions.ValidateAsync(current.CookieValue, Ct)).Status);
        Assert.Equal(SessionStatus.Invalid, (await h.Sessions.ValidateAsync(other.CookieValue, Ct)).Status);
    }

    [Fact]
    public async Task ChangingThePasswordOfAUserDeletedMeanwhileIsNotFoundNotACrash()
    {
        await using var h = await CreateAsync();
        var user = await h.CreateUserAsync("nina");
        await h.CreateUserAsync("boss", Role.Owner);
        await h.Users.DeleteAsync(CommandOrigin.Cli, user.Id, Ct);

        Assert.Equal(IdentityStatus.NotFound, (await h.Users.ChangePasswordAsync(h.As(user), new(Password, "a brand new passphrase"), null, Ct)).Status);
    }
}
