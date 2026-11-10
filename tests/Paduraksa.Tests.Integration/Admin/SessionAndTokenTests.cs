using System.Net;
using System.Text.Json;

using Paduraksa.Core.Model;
using Paduraksa.Tests.Integration.Hosting;

namespace Paduraksa.Tests.Integration.Admin;

public sealed class SessionAndTokenTests(SharedOwnerSession shared) : IClassFixture<SharedOwnerSession>
{
    [Fact]
    public async Task SessionsAreListedAndRevokedIndividually_AndRevokedOnesDieAtOnce()
    {
        // A user of its own with two sessions: the list it counts is that user's, not a shared account's.
        var h = shared.Harness;
        await h.CreateUserAsync(shared.OwnerCookie, "sessions-list", "admin");
        var a = await h.SignInAsync("sessions-list");
        var b = await h.SignInAsync("sessions-list");

        string bId;
        using (var list = await h.GetAsync("/api/v1/sessions", a))
        {
            Assert.Equal(HttpStatusCode.OK, list.Status);
            JsonElement[] sessions = [.. list.Json.EnumerateArray()];
            Assert.Equal(2, sessions.Length);
            var current = Assert.Single(sessions, s => s.GetProperty("current").GetBoolean());
            Assert.NotEqual(current.GetProperty("id").GetString(), sessions.Single(s => !s.GetProperty("current").GetBoolean()).GetProperty("id").GetString());
            bId = sessions.Single(s => !s.GetProperty("current").GetBoolean()).GetProperty("id").GetString()!;
            Assert.DoesNotContain(a, list.Body, StringComparison.Ordinal);
            Assert.DoesNotContain(b, list.Body, StringComparison.Ordinal);
        }

        using (var revoke = await h.SendAsync(HttpMethod.Delete, "/api/v1/sessions/" + bId, cookie: a))
        {
            Assert.Equal(HttpStatusCode.NoContent, revoke.Status);
        }

        using (var dead = await h.GetAsync("/api/v1/auth/me", b))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, dead.Status);
        }

        using (var bad = await h.SendAsync(HttpMethod.Delete, "/api/v1/sessions/not-an-id", cookie: a))
        {
            Assert.Equal(HttpStatusCode.NotFound, bad.Status);
        }

        using (var unknown = await h.SendAsync(HttpMethod.Delete, "/api/v1/sessions/ses_0000000000000000000000", cookie: a))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknown.Status);
        }
    }

    [Fact]
    public async Task RevokeOthersKeepsOnlyTheCallersSession_AndASessionOfAnotherUserIsNotFound()
    {
        // Users of its own: revoking every other session must not reach another test's.
        var h = shared.Harness;
        await h.CreateUserAsync(shared.OwnerCookie, "sessions-others", "admin");
        var a = await h.SignInAsync("sessions-others");
        var b = await h.SignInAsync("sessions-others");
        var c = await h.SignInAsync("sessions-others");
        await h.CreateUserAsync(shared.OwnerCookie, "sessions-others-viewer", "viewer");
        var viewer = await h.SignInAsync("sessions-others-viewer");

        string ownerSessionId;
        using (var list = await h.GetAsync("/api/v1/sessions", a))
        {
            ownerSessionId = list.Json.EnumerateArray().First(s => !s.GetProperty("current").GetBoolean()).GetProperty("id").GetString()!;
        }

        // Someone else's session is, to this caller, one that does not exist.
        using (var foreign = await h.SendAsync(HttpMethod.Delete, "/api/v1/sessions/" + ownerSessionId, cookie: viewer))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreign.Status);
        }

        using (var others = await h.PostAsync("/api/v1/sessions/revoke-others", cookie: a))
        {
            Assert.Equal(HttpStatusCode.NoContent, others.Status);
        }

        foreach (var gone in new[] { b, c })
        {
            using var me = await h.GetAsync("/api/v1/auth/me", gone);
            Assert.Equal(HttpStatusCode.Unauthorized, me.Status);
        }

        using (var kept = await h.GetAsync("/api/v1/auth/me", a))
        {
            Assert.Equal(HttpStatusCode.OK, kept.Status);
        }

        using (var theirs = await h.GetAsync("/api/v1/auth/me", viewer))
        {
            Assert.Equal(HttpStatusCode.OK, theirs.Status);
        }
    }

    [Fact]
    public async Task ATokenNeverHasMoreThanItsScopes_AndNeverMoreThanItsUser()
    {
        var h = shared.Harness;
        var owner = shared.OwnerCookie;
        await h.CreateUserAsync(owner, "scopes-viewer", "viewer");
        var viewer = await h.SignInAsync("scopes-viewer");

        var (readId, readOnly) = await h.IssueTokenAsync(owner, "users:read");
        using (var me = await h.GetAsync("/api/v1/auth/me", bearer: readOnly))
        {
            Assert.Equal(HttpStatusCode.OK, me.Status);
            Assert.Equal("token", me.Json.GetProperty("authKind").GetString());
            Assert.Equal(readId, me.Json.GetProperty("tokenId").GetString());
            Assert.Equal(["users:read"], me.Json.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
            Assert.Equal("owner", me.Json.GetProperty("user").GetProperty("username").GetString());
        }

        using (var allowed = await h.GetAsync("/api/v1/users", bearer: readOnly))
        {
            Assert.Equal(HttpStatusCode.OK, allowed.Status);
        }

        // The Owner could, the token may not: the scope narrows it.
        using (var narrowed = await h.PostAsync("/api/v1/users", """{"username":"newbie","displayName":null,"password":"correct horse battery staple","roles":["viewer"]}""", bearer: readOnly))
        {
            Assert.Equal(HttpStatusCode.Forbidden, narrowed.Status);
            Assert.Contains("users:write", narrowed.Body, StringComparison.Ordinal);
        }

        // A user cannot hand out what they do not hold.
        using (var tooMuch = await h.PostAsync("/api/v1/tokens", """{"name":"x","scopes":["users:write"],"expiresAt":null}""", viewer))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, tooMuch.Status);
        }
    }

    [Fact]
    public async Task ATokenCannotManageTokensOrSessions_ItGetsAClear403()
    {
        var h = shared.Harness;
        var owner = shared.OwnerCookie;
        var (id, secret) = await h.IssueTokenAsync(owner, "tokens:own", "tokens:all", "sessions:all", "users:read");

        foreach (var (method, path) in new[]
        {
            (HttpMethod.Get, "/api/v1/tokens"),
            (HttpMethod.Post, "/api/v1/tokens"),
            (HttpMethod.Delete, "/api/v1/tokens/" + id),
            (HttpMethod.Get, "/api/v1/sessions"),
            (HttpMethod.Delete, "/api/v1/sessions/" + SessionId.New()),
            (HttpMethod.Post, "/api/v1/sessions/revoke-others"),
        })
        {
            using var refused = await h.SendAsync(method, path, method == HttpMethod.Post && path.EndsWith("tokens", StringComparison.Ordinal) ? """{"name":"x","scopes":["config:read"],"expiresAt":null}""" : null, bearer: secret);
            Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
            Assert.Equal("auth.sessionRequired", refused.Json.GetProperty("code").GetString());
            Assert.Contains("use a signed-in session", refused.Json.GetProperty("detail").GetString(), StringComparison.Ordinal);
        }

        using var stillWorks = await h.GetAsync("/api/v1/users", bearer: secret);
        Assert.Equal(HttpStatusCode.OK, stillWorks.Status);
    }

    [Fact]
    public async Task ARevokedTokenGets401AtOnce_AndTheSecretIsShownOnlyWhenIssued()
    {
        // A user of its own: the token list it counts holds exactly the one token it issued.
        var h = shared.Harness;
        var owner = shared.OwnerCookie;
        var id0 = await h.CreateUserAsync(owner, "tokens-one", "admin");
        var (id, secret) = await h.IssueTokenAsync(await h.SignInAsync("tokens-one"), "users:read");
        Assert.StartsWith("pdk_", secret, StringComparison.Ordinal);

        using (var works = await h.GetAsync("/api/v1/users", bearer: secret))
        {
            Assert.Equal(HttpStatusCode.OK, works.Status);
        }

        using (var list = await h.GetAsync("/api/v1/tokens?userId=" + id0, owner))
        {
            Assert.Equal(HttpStatusCode.OK, list.Status);
            Assert.Equal(id, Assert.Single(list.Json.EnumerateArray()).GetProperty("id").GetString());
            Assert.DoesNotContain(secret, list.Body, StringComparison.Ordinal);
        }

        using (var revoke = await h.SendAsync(HttpMethod.Delete, "/api/v1/tokens/" + id, cookie: owner))
        {
            Assert.Equal(HttpStatusCode.NoContent, revoke.Status);
        }

        using (var dead = await h.GetAsync("/api/v1/users", bearer: secret))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, dead.Status);
            Assert.DoesNotContain(secret, dead.Body, StringComparison.Ordinal);
        }

        using (var notFound = await h.SendAsync(HttpMethod.Delete, "/api/v1/tokens/nonsense", cookie: owner))
        {
            Assert.Equal(HttpStatusCode.NotFound, notFound.Status);
        }
    }

    [Fact]
    public async Task ATokenStopsWorkingWhenItsUserIsDisabled_AndTheirSessionsEnd()
    {
        var h = shared.Harness;
        var owner = shared.OwnerCookie;
        var userId = await h.CreateUserAsync(owner, "disabled-viewer", "viewer");
        var viewer = await h.SignInAsync("disabled-viewer");
        var (_, secret) = await h.IssueTokenAsync(viewer, "config:read");

        using (var ok = await h.GetAsync("/api/v1/auth/me", bearer: secret))
        {
            Assert.Equal(HttpStatusCode.OK, ok.Status);
        }

        using (var disable = await h.SendAsync(HttpMethod.Patch, "/api/v1/users/" + userId, """{"disabled":true}""", owner))
        {
            Assert.Equal(HttpStatusCode.OK, disable.Status);
            Assert.True(disable.Json.GetProperty("disabled").GetBoolean());
        }

        using (var token = await h.GetAsync("/api/v1/auth/me", bearer: secret))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, token.Status);
        }

        using (var session = await h.GetAsync("/api/v1/auth/me", viewer))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, session.Status);
        }
    }

    [Fact]
    public async Task OnlyTokensAllMayListAnotherUsersTokensOrSessions()
    {
        var h = shared.Harness;
        var owner = shared.OwnerCookie;
        string ownerId;
        using (var me = await h.GetAsync("/api/v1/auth/me", owner))
        {
            ownerId = me.Json.GetProperty("user").GetProperty("id").GetString()!;
        }

        var viewerId = await h.CreateUserAsync(owner, "listed-viewer", "viewer");
        var viewer = await h.SignInAsync("listed-viewer");
        await h.IssueTokenAsync(viewer, "config:read");

        using (var denied = await h.GetAsync("/api/v1/tokens?userId=" + ownerId, viewer))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.Status);
        }

        using (var deniedSessions = await h.GetAsync("/api/v1/sessions?userId=" + ownerId, viewer))
        {
            Assert.Equal(HttpStatusCode.Forbidden, deniedSessions.Status);
        }

        using (var allowed = await h.GetAsync("/api/v1/tokens?userId=" + viewerId, owner))
        {
            Assert.Equal(HttpStatusCode.OK, allowed.Status);
            Assert.Single(allowed.Json.EnumerateArray());
        }

        using (var allowedSessions = await h.GetAsync("/api/v1/sessions?userId=" + viewerId, owner))
        {
            Assert.Equal(HttpStatusCode.OK, allowedSessions.Status);
        }

        using (var bad = await h.GetAsync("/api/v1/tokens?userId=garbage", owner))
        {
            Assert.Equal(HttpStatusCode.NotFound, bad.Status);
        }
    }
}
