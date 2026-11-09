using Paduraksa.Application.Identity;
using Paduraksa.Core.Model;

using static Paduraksa.Tests.Data.Identity.IdentityHarness;

namespace Paduraksa.Tests.Data.Identity;

public sealed class SessionServiceTests
{
    private static readonly IdentityOptions Short = new()
    {
        PasswordIterations = 1_000,
        SessionLifetime = TimeSpan.FromHours(1),
        SessionIdleTimeout = TimeSpan.FromMinutes(10),
        TouchInterval = TimeSpan.FromMinutes(1),
    };

    [Fact]
    public async Task ASessionEndsAtItsAbsoluteExpiryHoweverBusy()
    {
        await using var h = await CreateAsync(Short);
        await h.CreateUserAsync("alice");
        var session = await h.SignInAsync("alice");

        for (var i = 0; i < 5; i++)
        {
            h.Time.Advance(TimeSpan.FromMinutes(9)); // never idle for long
            Assert.Equal(SessionStatus.Valid, (await h.Sessions.ValidateAsync(session.CookieValue, Ct)).Status);
        }

        h.Time.Advance(TimeSpan.FromMinutes(16)); // 61 minutes in
        Assert.Equal(SessionStatus.Invalid, (await h.Sessions.ValidateAsync(session.CookieValue, Ct)).Status);
    }

    [Fact]
    public async Task ASessionEndsWhenIdleTooLong()
    {
        await using var h = await CreateAsync(Short);
        await h.CreateUserAsync("alice");
        var session = await h.SignInAsync("alice");

        h.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(SessionStatus.Valid, (await h.Sessions.ValidateAsync(session.CookieValue, Ct)).Status);
        h.Time.Advance(TimeSpan.FromMinutes(11));

        Assert.Equal(SessionStatus.Invalid, (await h.Sessions.ValidateAsync(session.CookieValue, Ct)).Status);
    }

    [Fact]
    public async Task LastSeenIsWrittenAtMostOncePerTouchInterval()
    {
        await using var h = await CreateAsync(Short);
        await h.CreateUserAsync("alice");
        var session = await h.SignInAsync("alice");
        var created = (string)(await h.Db.ScalarAsync("SELECT LastSeenAt FROM Sessions"))!;

        h.Time.Advance(TimeSpan.FromSeconds(30));
        await h.Sessions.ValidateAsync(session.CookieValue, Ct);
        Assert.Equal(created, await h.Db.ScalarAsync("SELECT LastSeenAt FROM Sessions")); // inside the interval: a read only

        h.Time.Advance(TimeSpan.FromSeconds(31));
        await h.Sessions.ValidateAsync(session.CookieValue, Ct);
        Assert.NotEqual(created, await h.Db.ScalarAsync("SELECT LastSeenAt FROM Sessions"));
    }

    [Fact]
    public async Task MalformedAndUnknownCookiesAreInvalid()
    {
        await using var h = await CreateAsync();

        foreach (var cookie in new[] { null, "", "short", new string('a', 43), new string('!', 43), new string('a', 44) })
        {
            Assert.Equal(SessionStatus.Invalid, (await h.Sessions.ValidateAsync(cookie, Ct)).Status);
        }
    }

    [Fact]
    public async Task EachSessionGetsItsOwnRandomCookieAndId()
    {
        await using var h = await CreateAsync();
        await h.CreateUserAsync("alice");

        var a = await h.SignInAsync("alice");
        var b = await h.SignInAsync("alice");

        Assert.NotEqual(a.CookieValue, b.CookieValue);
        Assert.NotEqual(a.Id, b.Id);
        Assert.StartsWith("ses_", a.Id.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListingShowsActiveSessionsMarksTheCurrentOneAndHidesOthersFromOthers()
    {
        await using var h = await CreateAsync();
        var alice = await h.CreateUserAsync("alice");
        var bob = await h.CreateUserAsync("bob");
        var owner = await h.CreateUserAsync("owner", Role.Owner);
        var first = await h.SignInAsync("alice");
        h.Time.Advance(TimeSpan.FromMinutes(1));
        var second = await h.SignInAsync("alice");
        await h.Sessions.RevokeAsync(h.As(alice), first.Id, Ct);

        var own = await h.Sessions.ListAsync(h.As(alice), alice.Id, second.Id, Ct);
        var byBob = await h.Sessions.ListAsync(h.As(bob), alice.Id, null, Ct);
        var byOwner = await h.Sessions.ListAsync(h.As(owner), alice.Id, null, Ct);

        var listed = Assert.Single(own.Sessions);
        Assert.Equal((second.Id, true, "203.0.113.9", "test-agent/1.0"), (listed.Id, listed.Current, listed.ClientIp, listed.UserAgent));
        Assert.Equal(IdentityStatus.Denied, byBob.Status);
        Assert.Single(byOwner.Sessions);
        Assert.False(byOwner.Sessions[0].Current);
    }

    [Fact]
    public async Task RevokingIsForYourOwnSessionsOrNeedsSessionsAll()
    {
        await using var h = await CreateAsync();
        var alice = await h.CreateUserAsync("alice");
        var bob = await h.CreateUserAsync("bob");
        var owner = await h.CreateUserAsync("owner", Role.Owner);
        var aliceSession = await h.SignInAsync("alice");
        var id = (await h.Sessions.ValidateAsync(aliceSession.CookieValue, Ct)).Principal!.SessionId;

        Assert.Equal(IdentityStatus.NotFound, (await h.Sessions.RevokeAsync(h.As(bob), id, Ct)).Status);
        Assert.Equal(SessionStatus.Valid, (await h.Sessions.ValidateAsync(aliceSession.CookieValue, Ct)).Status);
        Assert.True((await h.Sessions.RevokeAsync(h.As(owner), id, Ct)).Succeeded);
        Assert.Equal(SessionStatus.Invalid, (await h.Sessions.ValidateAsync(aliceSession.CookieValue, Ct)).Status);

        var audit = Assert.Single(await h.AuditAsync("auth.session.revoke"));
        Assert.Equal((ActorKind.User, id.ToString()), (audit.ActorKind, audit.Target));
        Assert.Equal(owner.Id, audit.ActorUserId);
        Assert.Equal(IdentityStatus.NotFound, (await h.Sessions.RevokeAsync(h.As(alice), SessionId.New(), Ct)).Status);
    }

    [Fact]
    public async Task RevokeOthersKeepsOnlyTheCurrentSession()
    {
        await using var h = await CreateAsync();
        var alice = await h.CreateUserAsync("alice");
        var bobSession = await h.SignInAsync((await h.CreateUserAsync("bob")).Username);
        var a = await h.SignInAsync("alice");
        var b = await h.SignInAsync("alice");
        var keep = (await h.Sessions.ValidateAsync(b.CookieValue, Ct)).Principal!.SessionId;

        Assert.True((await h.Sessions.RevokeOthersAsync(h.As(alice), keep, Ct)).Succeeded);

        Assert.Equal(SessionStatus.Invalid, (await h.Sessions.ValidateAsync(a.CookieValue, Ct)).Status);
        Assert.Equal(SessionStatus.Valid, (await h.Sessions.ValidateAsync(b.CookieValue, Ct)).Status);
        Assert.Equal(SessionStatus.Valid, (await h.Sessions.ValidateAsync(bobSession.CookieValue, Ct)).Status);
        Assert.Equal(IdentityStatus.Denied, (await h.Sessions.RevokeOthersAsync(Paduraksa.Application.Commands.CommandOrigin.Cli, keep, Ct)).Status);
    }

    [Fact]
    public async Task LoggingOutEndsTheSessionAndIsAudited()
    {
        await using var h = await CreateAsync();
        var alice = await h.CreateUserAsync("alice");
        var session = await h.SignInAsync("alice");

        await h.Sessions.LogoutAsync(session.CookieValue, h.Client, Ct);
        await h.Sessions.LogoutAsync(session.CookieValue, h.Client, Ct); // again: nothing to do
        await h.Sessions.LogoutAsync("garbage", h.Client, Ct);

        Assert.Equal(SessionStatus.Invalid, (await h.Sessions.ValidateAsync(session.CookieValue, Ct)).Status);
        var audit = Assert.Single(await h.AuditAsync("auth.logout"));
        Assert.Equal(alice.Id, audit.ActorUserId);
    }

    [Fact]
    public async Task OldEndedSessionsArePurgedAtTheNextSignIn()
    {
        await using var h = await CreateAsync(Short);
        await h.CreateUserAsync("alice");
        await h.SignInAsync("alice");

        h.Time.Advance(TimeSpan.FromDays(8));
        await h.SignInAsync("alice");

        Assert.Equal(1L, await h.Db.ScalarAsync("SELECT count(*) FROM Sessions"));
    }
}
