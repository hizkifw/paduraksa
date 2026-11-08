using Paduraksa.Application.Commands;
using Paduraksa.Application.Identity;
using Paduraksa.Contracts.Identity;
using Paduraksa.Core.Model;

using static Paduraksa.Tests.Data.Identity.IdentityHarness;

namespace Paduraksa.Tests.Data.Identity;

/// <summary>API tokens: issue, verify, scopes that can only narrow, expiry, revocation, owner state, throttled last-used.</summary>
public sealed class ApiTokenServiceTests
{
    private static IssueApiTokenRequest Request(string name = "ci", DateTimeOffset? expires = null, params Permission[] scopes) =>
        new(name, scopes.Length == 0 ? [Permission.ConfigRead] : scopes, expires);

    [Fact]
    public async Task AnIssuedTokenVerifiesAndOnlyItsHashIsStored()
    {
        await using var h = await CreateAsync();
        var user = await h.CreateUserAsync("alice", Role.Operator);

        var issued = await h.Tokens.IssueAsync(h.As(user), Request(scopes: [Permission.ConfigRead, Permission.ConfigWrite]), Ct);

        Assert.True(issued.Succeeded);
        var token = issued.Issued!.Token;
        Assert.StartsWith("pdk_", token, StringComparison.Ordinal);
        Assert.Equal(issued.Issued.Summary.Id, ApiTokenIdOf(token));
        var verified = await h.Tokens.VerifyAsync(token, Ct);
        Assert.True(verified.IsValid);
        Assert.Equal((issued.Issued.Summary.Id, user.Id), (verified.Principal!.TokenId, verified.Principal.UserId));
        Assert.Equal([Permission.ConfigRead, Permission.ConfigWrite], verified.Principal.Permissions.Order());
        var stored = (byte[])(await h.Db.ScalarAsync("SELECT SecretHash FROM ApiTokens"))!;
        Assert.Equal(32, stored.Length);
        Assert.DoesNotContain(Convert.ToHexString(stored), token, StringComparison.OrdinalIgnoreCase);
        var audit = Assert.Single(await h.AuditAsync("token.issue"));
        Assert.Equal((ActorKind.User, AuditOutcome.Success, issued.Issued.Summary.Id.ToString()), (audit.ActorKind, audit.Outcome, audit.Target));
        Assert.DoesNotContain(token, audit.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListingNeverShowsASecret()
    {
        await using var h = await CreateAsync();
        var user = await h.CreateUserAsync("alice");
        var issued = await h.Tokens.IssueAsync(h.As(user), Request(), Ct);

        var list = await h.Tokens.ListAsync(h.As(user), null, Ct);

        var summary = Assert.Single(list.Tokens);
        Assert.Equal(("ci", false), (summary.Name, summary.Revoked));
        Assert.Equal([Permission.ConfigRead], summary.Scopes);
        var json = System.Text.Json.JsonSerializer.Serialize(list.Tokens, IdentityJsonContext.Default.IReadOnlyListApiTokenSummary);
        Assert.DoesNotContain(issued.Issued!.Token, json, StringComparison.Ordinal);
        Assert.DoesNotContain("pdk_", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATokenNeverHasMoreThanItsUser()
    {
        await using var h = await CreateAsync();
        var viewer = await h.CreateUserAsync("viewer");

        var tooMuch = await h.Tokens.IssueAsync(h.As(viewer), Request(scopes: [Permission.ConfigRead, Permission.ConfigWrite]), Ct);

        Assert.Equal(IdentityStatus.Invalid, tooMuch.Status);
        Assert.Contains("scope", tooMuch.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScopesNarrowWhatTheOwnerCanDoAndTrackTheOwnersRoles()
    {
        await using var h = await CreateAsync();
        var user = await h.CreateUserAsync("alice", Role.Admin);
        var issued = await h.Tokens.IssueAsync(h.As(user), Request(scopes: [Permission.ConfigRead, Permission.AuditRead]), Ct);
        var id = issued.Issued!.Summary.Id;
        var token = Actor.ForToken(id);

        Assert.Equal([Permission.ConfigRead, Permission.AuditRead], (await h.Permissions.GetPermissionsAsync(token, Ct)).Order());
        Assert.False(await h.Permissions.IsAllowedAsync(h.Client.ToOrigin(token), Permission.ConfigWrite, Ct));

        // The owner is demoted to Viewer: the audit scope no longer grants anything.
        await h.Users.UpdateAsync(CommandOrigin.Cli, user.Id, new(null, [Role.Viewer]), Ct);
        Assert.Equal([Permission.ConfigRead], await h.Permissions.GetPermissionsAsync(token, Ct));
        Assert.Equal([Permission.ConfigRead], (await h.Tokens.VerifyAsync(issued.Issued.Token, Ct)).Principal!.Permissions);
    }

    [Fact]
    public async Task ATokenStopsWorkingWithItsUser()
    {
        await using var h = await CreateAsync();
        var user = await h.CreateUserAsync("alice");
        var issued = await h.Tokens.IssueAsync(h.As(user), Request(), Ct);
        var token = issued.Issued!.Token;

        await h.Users.SetDisabledAsync(CommandOrigin.Cli, user.Id, true, Ct);
        Assert.False((await h.Tokens.VerifyAsync(token, Ct)).IsValid);
        Assert.Empty(await h.Permissions.GetPermissionsAsync(Actor.ForToken(issued.Issued.Summary.Id), Ct));

        await h.Users.SetDisabledAsync(CommandOrigin.Cli, user.Id, false, Ct);
        Assert.True((await h.Tokens.VerifyAsync(token, Ct)).IsValid);
    }

    [Fact]
    public async Task ATokenExpires()
    {
        await using var h = await CreateAsync();
        var user = await h.CreateUserAsync("alice");
        var issued = await h.Tokens.IssueAsync(h.As(user), Request(expires: h.Time.Now.AddDays(1)), Ct);

        h.Time.Advance(TimeSpan.FromHours(23));
        Assert.True((await h.Tokens.VerifyAsync(issued.Issued!.Token, Ct)).IsValid);
        h.Time.Advance(TimeSpan.FromHours(2));

        Assert.False((await h.Tokens.VerifyAsync(issued.Issued.Token, Ct)).IsValid);
        Assert.Empty(await h.Permissions.GetPermissionsAsync(Actor.ForToken(issued.Issued.Summary.Id), Ct));
    }

    [Fact]
    public async Task ARevokedTokenStopsWorkingAndRevokingIsAuditedAndIdempotent()
    {
        await using var h = await CreateAsync();
        var user = await h.CreateUserAsync("alice");
        var issued = await h.Tokens.IssueAsync(h.As(user), Request(), Ct);
        var id = issued.Issued!.Summary.Id;

        var revoked = await h.Tokens.RevokeAsync(h.As(user), id, Ct);
        var again = await h.Tokens.RevokeAsync(h.As(user), id, Ct);

        Assert.True(revoked.Token!.Revoked);
        Assert.True(again.Token!.Revoked);
        Assert.False((await h.Tokens.VerifyAsync(issued.Issued.Token, Ct)).IsValid);
        Assert.Equal(2, (await h.AuditAsync("token.revoke")).Count);
        Assert.Equal(IdentityStatus.NotFound, (await h.Tokens.RevokeAsync(h.As(user), ApiTokenId.New(), Ct)).Status);
    }

    [Fact]
    public async Task TokensBelongToTheirOwnerExceptForTokensAll()
    {
        await using var h = await CreateAsync();
        var alice = await h.CreateUserAsync("alice");
        var bob = await h.CreateUserAsync("bob");
        var owner = await h.CreateUserAsync("owner", Role.Owner);
        var issued = await h.Tokens.IssueAsync(h.As(alice), Request(), Ct);
        var id = issued.Issued!.Summary.Id;

        Assert.Equal(IdentityStatus.Denied, (await h.Tokens.ListAsync(h.As(bob), alice.Id, Ct)).Status);
        Assert.Equal(IdentityStatus.NotFound, (await h.Tokens.RevokeAsync(h.As(bob), id, Ct)).Status);
        Assert.Single((await h.Tokens.ListAsync(h.As(owner), alice.Id, Ct)).Tokens);
        Assert.Equal(IdentityStatus.Denied, (await h.Tokens.ListAllAsync(h.As(bob), Ct)).Status);
        var everyone = await h.Tokens.ListAllAsync(h.As(owner), Ct);
        Assert.Equal(alice.Id, Assert.Single(everyone.Tokens).UserId);
        Assert.True((await h.Tokens.RevokeAsync(h.As(owner), id, Ct)).Succeeded);
        Assert.Equal(AuditOutcome.Denied, (await h.AuditAsync("token.revoke"))[0].Outcome);
    }

    [Fact]
    public async Task ATokenCannotIssueTokensAndTheCliHasNoOwner()
    {
        await using var h = await CreateAsync();
        var user = await h.CreateUserAsync("alice", Role.Owner);
        var issued = await h.Tokens.IssueAsync(h.As(user), Request(scopes: [Permission.TokensOwn]), Ct);

        var viaToken = await h.Tokens.IssueAsync(h.Client.ToOrigin(Actor.ForToken(issued.Issued!.Summary.Id)), Request(), Ct);
        var viaCli = await h.Tokens.IssueAsync(CommandOrigin.Cli, Request(), Ct);

        Assert.Equal(IdentityStatus.Denied, viaToken.Status);
        Assert.Equal(IdentityStatus.Denied, viaCli.Status);
        Assert.Equal(2, (await h.AuditAsync("token.issue")).Count(e => e.Outcome == AuditOutcome.Denied));
    }

    [Fact]
    public async Task IssuingValidatesTheRequest()
    {
        await using var h = await CreateAsync();
        var user = await h.CreateUserAsync("alice");

        Assert.Equal(IdentityStatus.Invalid, (await h.Tokens.IssueAsync(h.As(user), Request(name: " "), Ct)).Status);
        Assert.Equal(IdentityStatus.Invalid, (await h.Tokens.IssueAsync(h.As(user), new("ci", [], null), Ct)).Status);
        Assert.Equal(IdentityStatus.Invalid, (await h.Tokens.IssueAsync(h.As(user), new("ci", [(Permission)99], null), Ct)).Status);
        Assert.Equal(IdentityStatus.Invalid, (await h.Tokens.IssueAsync(h.As(user), Request(expires: h.Time.Now), Ct)).Status);
        Assert.True((await h.Tokens.IssueAsync(h.As(user), Request(scopes: [Permission.ConfigRead, Permission.ConfigRead]), Ct)).Issued!.Summary.Scopes.Count == 1);
    }

    [Fact]
    public async Task LastUsedIsWrittenAtMostOncePerTouchInterval()
    {
        await using var h = await CreateAsync();
        var user = await h.CreateUserAsync("alice");
        var issued = await h.Tokens.IssueAsync(h.As(user), Request(), Ct);
        Assert.Null(await h.Db.ScalarAsync("SELECT LastUsedAt FROM ApiTokens"));

        await h.Tokens.VerifyAsync(issued.Issued!.Token, Ct);
        var first = (string)(await h.Db.ScalarAsync("SELECT LastUsedAt FROM ApiTokens"))!;
        h.Time.Advance(TimeSpan.FromSeconds(30));
        await h.Tokens.VerifyAsync(issued.Issued.Token, Ct);
        Assert.Equal(first, await h.Db.ScalarAsync("SELECT LastUsedAt FROM ApiTokens"));
        h.Time.Advance(TimeSpan.FromSeconds(31));
        await h.Tokens.VerifyAsync(issued.Issued.Token, Ct);

        Assert.NotEqual(first, await h.Db.ScalarAsync("SELECT LastUsedAt FROM ApiTokens"));
    }

    [Fact]
    public async Task WrongSecretsMalformedAndUnknownTokensAreSimplyInvalid()
    {
        await using var h = await CreateAsync();
        var user = await h.CreateUserAsync("alice");
        var a = (await h.Tokens.IssueAsync(h.As(user), Request("a"), Ct)).Issued!.Token;
        var b = (await h.Tokens.IssueAsync(h.As(user), Request("b"), Ct)).Issued!.Token;
        // a's ID with b's secret, rechecksummed so only the lookup and the hash can refuse it.
        Assert.True(ApiTokenFormat.TryParse(a, out var idA, out _));
        Assert.True(ApiTokenFormat.TryParse(b, out _, out var secretB));
        var crossed = ApiTokenFormat.Format(idA, secretB);
        var unknown = ApiTokenFormat.Format(ApiTokenId.New(), secretB);

        foreach (var presented in new[] { null, "", "pdk_", "Bearer " + a, a + "x", a[..^1] + (a[^1] == '0' ? "1" : "0"), crossed, unknown })
        {
            Assert.False((await h.Tokens.VerifyAsync(presented, Ct)).IsValid);
        }

        Assert.True((await h.Tokens.VerifyAsync(a, Ct)).IsValid);
    }

    private static ApiTokenId ApiTokenIdOf(string token)
    {
        Assert.True(ApiTokenFormat.TryParse(token, out var id, out _));
        return id;
    }
}
