using System.Net;
using System.Text.Json;

using Paduraksa.Core.Model;

namespace Paduraksa.Tests.Integration.Admin;

/// <summary>Sessions (list, revoke, revoke others) and API tokens (scope narrowing, revocation, admin-wide listing).</summary>
public sealed class SessionAndTokenTests
{
    [Fact]
    public async Task SessionsAreListedAndRevokedIndividually_AndRevokedOnesDieAtOnce()
    {
        await using AdminHarness h = await AdminHarness.StartAsync();
        string a = await h.SetUpOwnerAsync();
        string b = await h.SignInAsync("owner");

        string bId;
        using (AdminResponse list = await h.GetAsync("/api/v1/sessions", a))
        {
            Assert.Equal(HttpStatusCode.OK, list.Status);
            JsonElement[] sessions = [.. list.Json.EnumerateArray()];
            Assert.Equal(2, sessions.Length);
            JsonElement current = Assert.Single(sessions, s => s.GetProperty("current").GetBoolean());
            Assert.NotEqual(current.GetProperty("id").GetString(), sessions.Single(s => !s.GetProperty("current").GetBoolean()).GetProperty("id").GetString());
            bId = sessions.Single(s => !s.GetProperty("current").GetBoolean()).GetProperty("id").GetString()!;
            Assert.DoesNotContain(a, list.Body, StringComparison.Ordinal);
            Assert.DoesNotContain(b, list.Body, StringComparison.Ordinal);
        }

        using (AdminResponse revoke = await h.SendAsync(HttpMethod.Delete, "/api/v1/sessions/" + bId, cookie: a))
        {
            Assert.Equal(HttpStatusCode.NoContent, revoke.Status);
        }

        using (AdminResponse dead = await h.GetAsync("/api/v1/auth/me", b))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, dead.Status);
        }

        using (AdminResponse bad = await h.SendAsync(HttpMethod.Delete, "/api/v1/sessions/not-an-id", cookie: a))
        {
            Assert.Equal(HttpStatusCode.NotFound, bad.Status);
        }

        using (AdminResponse unknown = await h.SendAsync(HttpMethod.Delete, "/api/v1/sessions/ses_0000000000000000000000", cookie: a))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknown.Status);
        }
    }

    [Fact]
    public async Task RevokeOthersKeepsOnlyTheCallersSession_AndASessionOfAnotherUserIsNotFound()
    {
        await using AdminHarness h = await AdminHarness.StartAsync();
        string a = await h.SetUpOwnerAsync();
        string b = await h.SignInAsync("owner");
        string c = await h.SignInAsync("owner");
        await h.CreateUserAsync(a, "viewer1", "viewer");
        string viewer = await h.SignInAsync("viewer1");

        string ownerSessionId;
        using (AdminResponse list = await h.GetAsync("/api/v1/sessions", a))
        {
            ownerSessionId = list.Json.EnumerateArray().First(s => !s.GetProperty("current").GetBoolean()).GetProperty("id").GetString()!;
        }

        // Someone else's session is, to this caller, one that does not exist.
        using (AdminResponse foreign = await h.SendAsync(HttpMethod.Delete, "/api/v1/sessions/" + ownerSessionId, cookie: viewer))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreign.Status);
        }

        using (AdminResponse others = await h.PostAsync("/api/v1/sessions/revoke-others", cookie: a))
        {
            Assert.Equal(HttpStatusCode.NoContent, others.Status);
        }

        foreach (string gone in new[] { b, c })
        {
            using AdminResponse me = await h.GetAsync("/api/v1/auth/me", gone);
            Assert.Equal(HttpStatusCode.Unauthorized, me.Status);
        }

        using (AdminResponse kept = await h.GetAsync("/api/v1/auth/me", a))
        {
            Assert.Equal(HttpStatusCode.OK, kept.Status);
        }

        using (AdminResponse theirs = await h.GetAsync("/api/v1/auth/me", viewer))
        {
            Assert.Equal(HttpStatusCode.OK, theirs.Status);
        }
    }

    [Fact]
    public async Task ATokenNeverHasMoreThanItsScopes_AndNeverMoreThanItsUser()
    {
        await using AdminHarness h = await AdminHarness.StartAsync();
        string owner = await h.SetUpOwnerAsync();
        await h.CreateUserAsync(owner, "viewer1", "viewer");
        string viewer = await h.SignInAsync("viewer1");

        (string readId, string readOnly) = await h.IssueTokenAsync(owner, "users:read");
        using (AdminResponse me = await h.GetAsync("/api/v1/auth/me", bearer: readOnly))
        {
            Assert.Equal(HttpStatusCode.OK, me.Status);
            Assert.Equal("token", me.Json.GetProperty("authKind").GetString());
            Assert.Equal(readId, me.Json.GetProperty("tokenId").GetString());
            Assert.Equal(["users:read"], me.Json.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
            Assert.Equal("owner", me.Json.GetProperty("user").GetProperty("username").GetString());
        }

        using (AdminResponse allowed = await h.GetAsync("/api/v1/users", bearer: readOnly))
        {
            Assert.Equal(HttpStatusCode.OK, allowed.Status);
        }

        // The Owner could, the token may not: the scope narrows it.
        using (AdminResponse narrowed = await h.PostAsync("/api/v1/users", """{"username":"newbie","displayName":null,"password":"correct horse battery staple","roles":["viewer"]}""", bearer: readOnly))
        {
            Assert.Equal(HttpStatusCode.Forbidden, narrowed.Status);
            Assert.Contains("users:write", narrowed.Body, StringComparison.Ordinal);
        }

        // A user cannot hand out what they do not hold.
        using (AdminResponse tooMuch = await h.PostAsync("/api/v1/tokens", """{"name":"x","scopes":["users:write"],"expiresAt":null}""", viewer))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, tooMuch.Status);
        }
    }

    [Fact]
    public async Task ATokenCannotManageTokensOrSessions_ItGetsAClear403()
    {
        await using AdminHarness h = await AdminHarness.StartAsync();
        string owner = await h.SetUpOwnerAsync();
        (string id, string secret) = await h.IssueTokenAsync(owner, "tokens:own", "tokens:all", "sessions:all", "users:read");

        foreach ((HttpMethod method, string path) in new[]
        {
            (HttpMethod.Get, "/api/v1/tokens"),
            (HttpMethod.Post, "/api/v1/tokens"),
            (HttpMethod.Delete, "/api/v1/tokens/" + id),
            (HttpMethod.Get, "/api/v1/sessions"),
            (HttpMethod.Delete, "/api/v1/sessions/" + SessionId.New()),
            (HttpMethod.Post, "/api/v1/sessions/revoke-others"),
        })
        {
            using AdminResponse refused = await h.SendAsync(method, path, method == HttpMethod.Post && path.EndsWith("tokens", StringComparison.Ordinal) ? """{"name":"x","scopes":["config:read"],"expiresAt":null}""" : null, bearer: secret);
            Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
            Assert.Equal("auth.sessionRequired", refused.Json.GetProperty("code").GetString());
            Assert.Contains("use a signed-in session", refused.Json.GetProperty("detail").GetString(), StringComparison.Ordinal);
        }

        using AdminResponse stillWorks = await h.GetAsync("/api/v1/users", bearer: secret);
        Assert.Equal(HttpStatusCode.OK, stillWorks.Status);
    }

    [Fact]
    public async Task ARevokedTokenGets401AtOnce_AndTheSecretIsShownOnlyWhenIssued()
    {
        await using AdminHarness h = await AdminHarness.StartAsync();
        string owner = await h.SetUpOwnerAsync();
        (string id, string secret) = await h.IssueTokenAsync(owner, "users:read");
        Assert.StartsWith("pdk_", secret, StringComparison.Ordinal);

        using (AdminResponse works = await h.GetAsync("/api/v1/users", bearer: secret))
        {
            Assert.Equal(HttpStatusCode.OK, works.Status);
        }

        using (AdminResponse list = await h.GetAsync("/api/v1/tokens", owner))
        {
            Assert.Equal(HttpStatusCode.OK, list.Status);
            Assert.Equal(id, Assert.Single(list.Json.EnumerateArray()).GetProperty("id").GetString());
            Assert.DoesNotContain(secret, list.Body, StringComparison.Ordinal);
        }

        using (AdminResponse revoke = await h.SendAsync(HttpMethod.Delete, "/api/v1/tokens/" + id, cookie: owner))
        {
            Assert.Equal(HttpStatusCode.NoContent, revoke.Status);
        }

        using (AdminResponse dead = await h.GetAsync("/api/v1/users", bearer: secret))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, dead.Status);
            Assert.DoesNotContain(secret, dead.Body, StringComparison.Ordinal);
        }

        using (AdminResponse notFound = await h.SendAsync(HttpMethod.Delete, "/api/v1/tokens/nonsense", cookie: owner))
        {
            Assert.Equal(HttpStatusCode.NotFound, notFound.Status);
        }
    }

    [Fact]
    public async Task ATokenStopsWorkingWhenItsUserIsDisabled_AndTheirSessionsEnd()
    {
        await using AdminHarness h = await AdminHarness.StartAsync();
        string owner = await h.SetUpOwnerAsync();
        string userId = await h.CreateUserAsync(owner, "viewer1", "viewer");
        string viewer = await h.SignInAsync("viewer1");
        (_, string secret) = await h.IssueTokenAsync(viewer, "config:read");

        using (AdminResponse ok = await h.GetAsync("/api/v1/auth/me", bearer: secret))
        {
            Assert.Equal(HttpStatusCode.OK, ok.Status);
        }

        using (AdminResponse disable = await h.SendAsync(HttpMethod.Patch, "/api/v1/users/" + userId, """{"disabled":true}""", owner))
        {
            Assert.Equal(HttpStatusCode.OK, disable.Status);
            Assert.True(disable.Json.GetProperty("disabled").GetBoolean());
        }

        using (AdminResponse token = await h.GetAsync("/api/v1/auth/me", bearer: secret))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, token.Status);
        }

        using (AdminResponse session = await h.GetAsync("/api/v1/auth/me", viewer))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, session.Status);
        }
    }

    [Fact]
    public async Task OnlyTokensAllMayListAnotherUsersTokensOrSessions()
    {
        await using AdminHarness h = await AdminHarness.StartAsync();
        string owner = await h.SetUpOwnerAsync();
        string ownerId;
        using (AdminResponse me = await h.GetAsync("/api/v1/auth/me", owner))
        {
            ownerId = me.Json.GetProperty("user").GetProperty("id").GetString()!;
        }

        string viewerId = await h.CreateUserAsync(owner, "viewer1", "viewer");
        string viewer = await h.SignInAsync("viewer1");
        await h.IssueTokenAsync(viewer, "config:read");

        using (AdminResponse denied = await h.GetAsync("/api/v1/tokens?userId=" + ownerId, viewer))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.Status);
        }

        using (AdminResponse deniedSessions = await h.GetAsync("/api/v1/sessions?userId=" + ownerId, viewer))
        {
            Assert.Equal(HttpStatusCode.Forbidden, deniedSessions.Status);
        }

        using (AdminResponse allowed = await h.GetAsync("/api/v1/tokens?userId=" + viewerId, owner))
        {
            Assert.Equal(HttpStatusCode.OK, allowed.Status);
            Assert.Single(allowed.Json.EnumerateArray());
        }

        using (AdminResponse allowedSessions = await h.GetAsync("/api/v1/sessions?userId=" + viewerId, owner))
        {
            Assert.Equal(HttpStatusCode.OK, allowedSessions.Status);
        }

        using (AdminResponse bad = await h.GetAsync("/api/v1/tokens?userId=garbage", owner))
        {
            Assert.Equal(HttpStatusCode.NotFound, bad.Status);
        }
    }
}
