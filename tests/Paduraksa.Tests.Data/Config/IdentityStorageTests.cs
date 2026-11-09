using Microsoft.EntityFrameworkCore;
using Paduraksa.Core.Model;
using Paduraksa.Data.Identity;
using static Paduraksa.Tests.Data.Config.TestConfigDatabase;

namespace Paduraksa.Tests.Data.Config;

public sealed class IdentityStorageTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AUserRoundTripsWithTypedIdsAndUtcTimestamps()
    {
        await using var db = await CreateAsync();
        var user = User("alice");
        user.TotpSecretId = SecretId.New();
        user.TotpEnabled = true;
        user.LockedUntil = Now.AddMinutes(5);
        await SaveAsync(db, user);

        await using var context = db.NewContext();
        var read = await context.Users.SingleAsync(Ct);

        Assert.Equal(user.Id, read.Id);
        Assert.Equal(user.TotpSecretId, read.TotpSecretId);
        Assert.Equal(Now.AddMinutes(5), read.LockedUntil);
        Assert.True(read.TotpEnabled);
        Assert.Equal(16L, await db.ScalarAsync("SELECT length(Id) FROM Users"));
    }

    [Fact]
    public async Task UsernamesAreUniqueIgnoringCase()
    {
        await using var db = await CreateAsync();
        await SaveAsync(db, User("Alice"));

        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(db, User("alice")));
        await using var context = db.NewContext();
        Assert.Equal("Alice", (await context.Users.SingleAsync(u => u.Username == "ALICE", Ct)).Username);
    }

    [Fact]
    public async Task EnabledTotpNeedsASecretReference()
    {
        await using var db = await CreateAsync();
        var user = User("bob");
        user.TotpEnabled = true;

        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(db, user));
        Assert.Contains("CK_Users_Totp", failure.InnerException!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RolesAreStoredByNameOncePerUserAndNeedAUser()
    {
        await using var db = await CreateAsync();
        var user = User("carol");
        await SaveAsync(db, user, new RoleAssignmentRow { UserId = user.Id, Role = Role.Auditor });

        Assert.Equal("auditor", await db.ScalarAsync("SELECT Role FROM RoleAssignments"));
        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(db, new RoleAssignmentRow { UserId = user.Id, Role = Role.Auditor }));
        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(db, new RoleAssignmentRow { UserId = UserId.New(), Role = Role.Owner }));
        var unknown = await Assert.ThrowsAnyAsync<Exception>(() => db.ExecuteAsync("UPDATE RoleAssignments SET Role = 'root'"));
        Assert.Contains("CK_RoleAssignments_Role", unknown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeletingAUserDeletesWhatHangsOffIt()
    {
        await using var db = await CreateAsync();
        var user = User("dave");
        await SaveAsync(
            db,
            user,
            new RoleAssignmentRow { UserId = user.Id, Role = Role.Viewer },
            Token(user.Id),
            Session(user.Id),
            new RecoveryCodeRow { UserId = user.Id, CodeHash = Hash(1) });

        await db.ExecuteAsync("DELETE FROM Users");

        foreach (var table in new[] { "RoleAssignments", "ApiTokens", "Sessions", "RecoveryCodes" })
        {
            Assert.Equal(0L, await db.ScalarAsync($"SELECT count(*) FROM {table}"));
        }
    }

    [Fact]
    public async Task ATokenRoundTripsAndOnlyKeepsAHash()
    {
        await using var db = await CreateAsync();
        var user = User("erin");
        var token = Token(user.Id);
        token.ExpiresAt = Now.AddDays(30);
        await SaveAsync(db, user, token);

        await using var context = db.NewContext();
        var read = await context.ApiTokens.SingleAsync(Ct);
        Assert.Equal((token.Id, user.Id, "ci", "config:read,simulate", Now.AddDays(30)), (read.Id, read.UserId, read.Name, read.Scopes, read.ExpiresAt));
        Assert.Equal(token.SecretHash, read.SecretHash);

        var short_ = await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(db, Token(user.Id, hash: new byte[16])));
        Assert.Contains("CK_ApiTokens_SecretHash", short_.InnerException!.Message, StringComparison.Ordinal);
        var noScopes = Token(user.Id);
        noScopes.Scopes = "";
        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(db, noScopes));
        Assert.Contains("CK_ApiTokens_Scopes", failure.InnerException!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASessionHashIsUniqueAndTheIdIsARandomUuid()
    {
        await using var db = await CreateAsync();
        var user = User("frank");
        var session = Session(user.Id);
        await SaveAsync(db, user, session);

        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(db, Session(user.Id, session.TokenHash)));
        await using var context = db.NewContext();
        var read = await context.Sessions.SingleAsync(Ct);
        Assert.Equal(session.Id, read.Id);
        Assert.Equal(session.TokenHash, read.TokenHash);
        Assert.True(read.SecondFactorCompleted);
        var bytes = (byte[])(await db.ScalarAsync("SELECT Id FROM Sessions"))!;
        Assert.Equal(4, bytes[6] >> 4); // UUID version 4
    }

    [Fact]
    public async Task RecoveryCodesAreUniquePerUserAndMarkedUsedInPlace()
    {
        await using var db = await CreateAsync();
        var user = User("gina");
        await SaveAsync(db, user, new RecoveryCodeRow { UserId = user.Id, CodeHash = Hash(7) });

        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(db, new RecoveryCodeRow { UserId = user.Id, CodeHash = Hash(7) }));
        await using var context = db.NewContext();
        var used = await context.RecoveryCodes
            .Where(c => c.UserId == user.Id && c.CodeHash == Hash(7) && c.UsedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.UsedAt, Now), Ct);
        var again = await context.RecoveryCodes
            .Where(c => c.UserId == user.Id && c.CodeHash == Hash(7) && c.UsedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.UsedAt, Now), Ct);
        Assert.Equal((1, 0), (used, again));
    }

    [Fact]
    public async Task NoColumnHoldsAPlaintextSecret()
    {
        await using var db = await CreateAsync();
        var columns = new List<string>();
        foreach (var table in new[] { "Users", "ApiTokens", "Sessions", "RecoveryCodes" })
        {
            columns.AddRange((await db.QueryAsync($"SELECT '{table}.' || name FROM pragma_table_info('{table}')")).Select(r => (string)r[0]!));
        }

        // Hashes and references only: none of these is a plaintext password, token, cookie, code or TOTP secret.
        Assert.DoesNotContain(columns, c => c.EndsWith(".Password", StringComparison.Ordinal) || c.EndsWith(".Secret", StringComparison.Ordinal)
            || c.EndsWith(".Token", StringComparison.Ordinal) || c.EndsWith(".Cookie", StringComparison.Ordinal) || c.EndsWith(".Code", StringComparison.Ordinal));
        Assert.Contains("Users.PasswordHash", columns);
        Assert.Contains("ApiTokens.SecretHash", columns);
        Assert.Contains("Sessions.TokenHash", columns);
        Assert.Contains("RecoveryCodes.CodeHash", columns);
    }

    private static UserRow User(string name) => new()
    {
        Id = UserId.New(),
        Username = name,
        DisplayName = name,
        PasswordHash = "pbkdf2-sha512$v1$...",
        CreatedAt = Now,
        UpdatedAt = Now,
    };

    private static ApiTokenRow Token(UserId user, byte[]? hash = null) => new()
    {
        Id = ApiTokenId.New(),
        UserId = user,
        Name = "ci",
        Scopes = "config:read,simulate",
        SecretHash = hash ?? Hash(9),
        CreatedAt = Now,
    };

    private static SessionRow Session(UserId user, byte[]? hash = null) => new()
    {
        Id = SessionId.New(),
        UserId = user,
        TokenHash = hash ?? Hash(3),
        CreatedAt = Now,
        LastSeenAt = Now,
        ExpiresAt = Now.AddHours(8),
        SecondFactorCompleted = true,
    };

    private static byte[] Hash(byte seed) => [.. Enumerable.Repeat(seed, 32)];

    private static async Task SaveAsync(TestConfigDatabase db, params object[] entities)
    {
        await using var context = db.NewContext();
        context.AddRange(entities);
        await context.SaveChangesAsync(Ct);
    }
}
