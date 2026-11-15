using Paduraksa.Application.Commands;
using Paduraksa.Application.Identity;
using Paduraksa.Contracts.Identity;
using Paduraksa.Core.Model;

using static Paduraksa.Tests.Data.Identity.IdentityHarness;

namespace Paduraksa.Tests.Data.Identity;

/// <summary>API tokens: issue, verify, scopes that can only narrow, expiry, revocation, disabling, editing, owner state, throttled last-used.</summary>
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
        var issued = await h.Tokens.IssueAsync(h.As(user), Request(), Ct);
        // A row that carries the scope, as one from before the scope was refused would: it still can't issue.
        await h.Db.ExecuteAsync("UPDATE ApiTokens SET Scopes = 'tokens:own'");

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

    private static UpdateApiTokenRequest Change(string? name = null, Permission[]? scopes = null, bool? disabled = null) => new(name, scopes, disabled);

    [Fact]
    public async Task ATokenIsRenamedAndRescopedAndTheAuditEventsCarryTheOldAndNewValues()
    {
        await using var h = await CreateAsync();
        var user = await h.CreateUserAsync("alice", Role.Operator);
        var issued = await h.Tokens.IssueAsync(h.As(user), Request("ci", scopes: [Permission.ConfigRead]), Ct);
        var id = issued.Issued!.Summary.Id;

        var changed = await h.Tokens.UpdateAsync(h.As(user), id, Change("  deploy  ", [Permission.ConfigWrite, Permission.ConfigRead, Permission.ConfigRead]), Ct);

        Assert.True(changed.Succeeded);
        Assert.Equal(("deploy", false), (changed.Token!.Name, changed.Token.Disabled));
        Assert.Equal([Permission.ConfigRead, Permission.ConfigWrite], changed.Token.Scopes);
        var verified = await h.Tokens.VerifyAsync(issued.Issued.Token, Ct);
        Assert.Equal([Permission.ConfigRead, Permission.ConfigWrite], verified.Principal!.Permissions.Order());
        var audit = Assert.Single(await h.AuditAsync("token.update"));
        Assert.Equal((AuditOutcome.Success, id.ToString()), (audit.Outcome, audit.Target));
        using var details = System.Text.Json.JsonDocument.Parse(audit.Details!);
        var root = details.RootElement;
        Assert.Equal(("deploy", "ci", "config:read,config:write", "config:read"),
            (root.GetProperty("name").GetString(), root.GetProperty("previousName").GetString(), root.GetProperty("scopes").GetString(), root.GetProperty("previousScopes").GetString()));
        Assert.DoesNotContain(issued.Issued.Token, audit.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChangeThatChangesNothingWritesNoAuditEvent()
    {
        await using var h = await CreateAsync();
        var user = await h.CreateUserAsync("alice");
        var id = (await h.Tokens.IssueAsync(h.As(user), Request(), Ct)).Issued!.Summary.Id;

        var same = await h.Tokens.UpdateAsync(h.As(user), id, Change("ci", [Permission.ConfigRead], false), Ct);

        Assert.True(same.Succeeded);
        Assert.Empty(await h.AuditAsync("token.update"));
        Assert.Empty(await h.AuditAsync("token.enable"));
        Assert.Empty(await h.AuditAsync("token.disable"));
    }

    [Fact]
    public async Task EditingValidatesTheRequestAndRefusesAScopeItsOwnerLacks()
    {
        await using var h = await CreateAsync();
        var user = await h.CreateUserAsync("alice");
        var id = (await h.Tokens.IssueAsync(h.As(user), Request(), Ct)).Issued!.Summary.Id;

        Assert.Equal(IdentityStatus.Invalid, (await h.Tokens.UpdateAsync(h.As(user), id, Change(), Ct)).Status);
        Assert.Equal(IdentityStatus.Invalid, (await h.Tokens.UpdateAsync(h.As(user), id, Change(name: " "), Ct)).Status);
        Assert.Equal(128, (await h.Tokens.UpdateAsync(h.As(user), id, Change(name: new string('x', 129)), Ct)).Token!.Name.Length);
        await h.Tokens.UpdateAsync(h.As(user), id, Change("ci"), Ct);
        var audited = (await h.AuditAsync("token.update")).Count;
        Assert.Equal(IdentityStatus.Invalid, (await h.Tokens.UpdateAsync(h.As(user), id, Change(scopes: []), Ct)).Status);
        Assert.Equal(IdentityStatus.Invalid, (await h.Tokens.UpdateAsync(h.As(user), id, Change(scopes: [(Permission)99]), Ct)).Status);
        var tooMuch = await h.Tokens.UpdateAsync(h.As(user), id, Change(scopes: [Permission.ConfigRead, Permission.ConfigWrite]), Ct);

        Assert.Equal(IdentityStatus.Invalid, tooMuch.Status);
        Assert.Contains("owner", tooMuch.Problem, StringComparison.Ordinal);
        var kept = Assert.Single((await h.Tokens.ListAsync(h.As(user), null, Ct)).Tokens);
        Assert.Equal("ci", kept.Name);
        Assert.Equal([Permission.ConfigRead], kept.Scopes);
        Assert.Equal(audited, (await h.AuditAsync("token.update")).Count);
    }

    [Fact]
    public async Task AnOwnerEditingSomeoneElsesTokenCannotGrantItWhatItsOwnerLacks()
    {
        await using var h = await CreateAsync();
        var alice = await h.CreateUserAsync("alice");
        var owner = await h.CreateUserAsync("owner", Role.Owner);
        var id = (await h.Tokens.IssueAsync(h.As(alice), Request(), Ct)).Issued!.Summary.Id;

        // The Owner holds config:write; alice, a Viewer, does not.
        var refused = await h.Tokens.UpdateAsync(h.As(owner), id, Change(scopes: [Permission.ConfigWrite]), Ct);
        var narrowed = await h.Tokens.UpdateAsync(h.As(owner), id, Change("alice's script", [Permission.MetricsRead]), Ct);

        Assert.Equal(IdentityStatus.Invalid, refused.Status);
        Assert.True(narrowed.Succeeded);
        Assert.Equal([Permission.MetricsRead], narrowed.Token!.Scopes);
        Assert.Equal(alice.Id, narrowed.Token.UserId);
    }

    [Fact]
    public async Task ADisabledTokenIsRefusedLikeAnyOtherInvalidOneAndEnablingBringsItBack()
    {
        await using var h = await CreateAsync();
        var user = await h.CreateUserAsync("alice");
        var issued = await h.Tokens.IssueAsync(h.As(user), Request(), Ct);
        var id = issued.Issued!.Summary.Id;
        var token = issued.Issued.Token;

        var disabled = await h.Tokens.UpdateAsync(h.As(user), id, Change(disabled: true), Ct);

        Assert.True(disabled.Token!.Disabled);
        Assert.False(disabled.Token.Revoked);
        var refused = await h.Tokens.VerifyAsync(token, Ct);
        Assert.False(refused.IsValid);
        Assert.Null(refused.Principal);
        Assert.Empty(await h.Permissions.GetPermissionsAsync(Actor.ForToken(id), Ct));

        var enabled = await h.Tokens.UpdateAsync(h.As(user), id, Change(disabled: false), Ct);

        Assert.False(enabled.Token!.Disabled);
        Assert.True((await h.Tokens.VerifyAsync(token, Ct)).IsValid);
        Assert.Equal(AuditOutcome.Success, Assert.Single(await h.AuditAsync("token.disable")).Outcome);
        Assert.Equal(AuditOutcome.Success, Assert.Single(await h.AuditAsync("token.enable")).Outcome);
        Assert.Empty(await h.AuditAsync("token.update"));
    }

    [Fact]
    public async Task ADisabledTokenStillCountsTowardsTheLimitAndIsListedAsDisabled()
    {
        await using var h = await CreateAsync();
        var user = await h.CreateUserAsync("alice");
        for (var i = 0; i < ApiTokenService.MaxTokensPerUser; i++)
        {
            var issued = await h.Tokens.IssueAsync(h.As(user), Request($"t{i}"), Ct);
            Assert.True(issued.Succeeded);
            if (i == 0)
            {
                await h.Tokens.UpdateAsync(h.As(user), issued.Issued!.Summary.Id, Change(disabled: true), Ct);
            }
        }

        var over = await h.Tokens.IssueAsync(h.As(user), Request("one too many"), Ct);

        Assert.Equal(IdentityStatus.Invalid, over.Status);
        Assert.Single((await h.Tokens.ListAsync(h.As(user), null, Ct)).Tokens, t => t.Disabled);
    }

    [Fact]
    public async Task ARevokedTokenCannotBeEditedOrEnabled()
    {
        await using var h = await CreateAsync();
        var user = await h.CreateUserAsync("alice");
        var issued = await h.Tokens.IssueAsync(h.As(user), Request(), Ct);
        var id = issued.Issued!.Summary.Id;
        await h.Tokens.UpdateAsync(h.As(user), id, Change(disabled: true), Ct);
        await h.Tokens.RevokeAsync(h.As(user), id, Ct);

        foreach (var request in new[] { Change("new"), Change(scopes: [Permission.MetricsRead]), Change(disabled: false) })
        {
            Assert.Equal(IdentityStatus.Conflict, (await h.Tokens.UpdateAsync(h.As(user), id, request, Ct)).Status);
        }

        var summary = Assert.Single((await h.Tokens.ListAsync(h.As(user), null, Ct)).Tokens);
        Assert.Equal(("ci", true), (summary.Name, summary.Revoked));
        Assert.False((await h.Tokens.VerifyAsync(issued.Issued.Token, Ct)).IsValid);
    }

    [Fact]
    public async Task EditingFollowsTheRulesForRevoking()
    {
        await using var h = await CreateAsync();
        var alice = await h.CreateUserAsync("alice");
        var bob = await h.CreateUserAsync("bob");
        var owner = await h.CreateUserAsync("owner", Role.Owner);
        var id = (await h.Tokens.IssueAsync(h.As(alice), Request(), Ct)).Issued!.Summary.Id;

        // Not theirs, and no tokens:all: answered like a token that does not exist, and audited as denied.
        Assert.Equal(IdentityStatus.NotFound, (await h.Tokens.UpdateAsync(h.As(bob), id, Change("mine now"), Ct)).Status);
        Assert.Equal(IdentityStatus.NotFound, (await h.Tokens.UpdateAsync(h.As(bob), ApiTokenId.New(), Change("x"), Ct)).Status);
        var denied = Assert.Single(await h.AuditAsync("token.update"), e => e.Outcome == AuditOutcome.Denied);
        Assert.Equal(id.ToString(), denied.Target);
        Assert.Equal("ci", Assert.Single((await h.Tokens.ListAsync(h.As(alice), null, Ct)).Tokens).Name);

        // The owner of the token, and anyone with tokens:all.
        Assert.True((await h.Tokens.UpdateAsync(h.As(alice), id, Change("alice's"), Ct)).Succeeded);
        Assert.True((await h.Tokens.UpdateAsync(h.As(owner), id, Change(disabled: true), Ct)).Succeeded);
        var after = Assert.Single((await h.Tokens.ListAsync(h.As(alice), null, Ct)).Tokens);
        Assert.Equal("alice's", after.Name);
        Assert.True(after.Disabled);
    }

    [Fact]
    public async Task ATokenCannotEditTokens()
    {
        await using var h = await CreateAsync();
        var user = await h.CreateUserAsync("alice", Role.Owner);
        var issued = await h.Tokens.IssueAsync(h.As(user), Request(), Ct);
        var id = issued.Issued!.Summary.Id;
        await h.Db.ExecuteAsync("UPDATE ApiTokens SET Scopes = 'tokens:own'");

        var viaToken = await h.Tokens.UpdateAsync(h.Client.ToOrigin(Actor.ForToken(id)), id, Change(disabled: true), Ct);

        Assert.Equal(IdentityStatus.NotFound, viaToken.Status);
        Assert.True((await h.Tokens.VerifyAsync(issued.Issued.Token, Ct)).IsValid);
    }

    [Fact]
    public async Task AnEditNeverRevealsOrRotatesTheSecret()
    {
        await using var h = await CreateAsync();
        var user = await h.CreateUserAsync("alice");
        var issued = await h.Tokens.IssueAsync(h.As(user), Request(), Ct);
        var hash = (byte[])(await h.Db.ScalarAsync("SELECT SecretHash FROM ApiTokens"))!;

        var changed = await h.Tokens.UpdateAsync(h.As(user), issued.Issued!.Summary.Id, Change("renamed", disabled: true), Ct);

        Assert.Null(changed.Issued);
        Assert.Equal(hash, (byte[])(await h.Db.ScalarAsync("SELECT SecretHash FROM ApiTokens"))!);
        await h.Tokens.UpdateAsync(h.As(user), issued.Issued.Summary.Id, Change(disabled: false), Ct);
        Assert.True((await h.Tokens.VerifyAsync(issued.Issued.Token, Ct)).IsValid);
    }

    [Theory]
    [InlineData(Permission.TokensOwn)]
    [InlineData(Permission.TokensAll)]
    [InlineData(Permission.SessionsAll)]
    public async Task TheScopesThatManageTokensAndSessionsAreUnknownToIssuingAndEditing(Permission scope)
    {
        await using var h = await CreateAsync();
        var owner = await h.CreateUserAsync("owner", Role.Owner);
        var id = (await h.Tokens.IssueAsync(h.As(owner), Request(), Ct)).Issued!.Summary.Id;

        var issued = await h.Tokens.IssueAsync(h.As(owner), Request(scopes: [Permission.ConfigRead, scope]), Ct);
        var edited = await h.Tokens.UpdateAsync(h.As(owner), id, Change(scopes: [Permission.ConfigRead, scope]), Ct);

        Assert.Equal((IdentityStatus.Invalid, "Unknown scope."), (issued.Status, issued.Problem));
        Assert.Equal((IdentityStatus.Invalid, "Unknown scope."), (edited.Status, edited.Problem));
        Assert.Equal([Permission.ConfigRead], Assert.Single((await h.Tokens.ListAsync(h.As(owner), null, Ct)).Tokens).Scopes);
    }

    [Fact]
    public async Task AnEmptyChangeToARevokedTokenIsAConflictNotAnInvalidRequest()
    {
        await using var h = await CreateAsync();
        var user = await h.CreateUserAsync("alice");
        var id = (await h.Tokens.IssueAsync(h.As(user), Request(), Ct)).Issued!.Summary.Id;
        await h.Tokens.RevokeAsync(h.As(user), id, Ct);

        Assert.Equal(IdentityStatus.Conflict, (await h.Tokens.UpdateAsync(h.As(user), id, Change(), Ct)).Status);
    }

    [Fact]
    public async Task AChangeRacingARevokeNeverLandsOrIsAuditedAfterIt()
    {
        await using var h = await CreateAsync();
        var user = await h.CreateUserAsync("alice");
        var origin = h.As(user);
        for (var i = 0; i < 25; i++)
        {
            var id = (await h.Tokens.IssueAsync(origin, Request($"t{i}"), Ct)).Issued!.Summary.Id;

            var results = await Task.WhenAll(
                Task.Run(() => h.Tokens.UpdateAsync(origin, id, Change(disabled: true, name: "renamed"), Ct), Ct),
                Task.Run(() => h.Tokens.RevokeAsync(origin, id, Ct), Ct));

            Assert.True(results[1].Succeeded);
            Assert.Contains(results[0].Status, new[] { IdentityStatus.Succeeded, IdentityStatus.Conflict });
            var events = await h.AuditAsync();
            var revoked = events.FindIndex(e => e.Action == "token.revoke" && e.Target == id.ToString());
            var changed = events.FindLastIndex(e => e.Action is "token.update" or "token.disable" or "token.enable" && e.Target == id.ToString());
            Assert.True(changed < revoked, "a change was audited after the revoke");
        }
    }

    private static ApiTokenId ApiTokenIdOf(string token)
    {
        Assert.True(ApiTokenFormat.TryParse(token, out var id, out _));
        return id;
    }
}
