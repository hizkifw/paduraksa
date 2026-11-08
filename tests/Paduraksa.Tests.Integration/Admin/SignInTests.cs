using System.Net;

using Paduraksa.Admin.Auth;

namespace Paduraksa.Tests.Integration.Admin;

/// <summary>Signing in: both steps, lockout, logout, the cookie, and what a partial session may reach.</summary>
public sealed class SignInTests
{
    private static string WithoutTrace(string body) => System.Text.RegularExpressions.Regex.Replace(body, "\"traceId\":\"[^\"]*\"", string.Empty);

    private static readonly Dictionary<string, string?> RoomyLimits = new()
    {
        ["Paduraksa:Admin:RateLimit:PermitsPerIp"] = "1000",
        ["Paduraksa:Admin:RateLimit:PermitsPerUser"] = "1000",
    };

    [Fact]
    public async Task LoginSetsAnHttpOnlyHostPrefixedStrictCookieAndNothingSecretInTheBody()
    {
        await using AdminHarness h = await AdminHarness.StartAsync();
        await h.SetUpOwnerAsync();

        using AdminResponse login = await h.PostAsync("/api/v1/auth/login", Body.Login("OWNER", AdminHarness.Password));

        Assert.Equal(HttpStatusCode.OK, login.Status);
        string line = Assert.Single(login.SetCookies);
        Assert.StartsWith("__Host-paduraksa-session=", line, StringComparison.Ordinal);
        string[] attributes = [.. line.Split(';').Skip(1).Select(a => a.Trim().ToLowerInvariant())];
        Assert.Contains("secure", attributes);
        Assert.Contains("httponly", attributes);
        Assert.Contains("samesite=strict", attributes);
        Assert.Contains("path=/", attributes);
        Assert.DoesNotContain(attributes, a => a.StartsWith("domain", StringComparison.Ordinal));
        Assert.Equal(43, login.SessionCookie!.Length);
        Assert.DoesNotContain(login.SessionCookie, login.Body, StringComparison.Ordinal);
        Assert.StartsWith("ses_", login.Json.GetProperty("sessionId").GetString(), StringComparison.Ordinal);

        IReadOnlyList<AuditRecord> audit = await h.AuditAsync("auth.login");
        Assert.All(audit, a => Assert.Equal("api", a.Channel));
        Assert.Contains(audit, a => a.Outcome == "success" && a.ClientIp == "127.0.0.1");
    }

    [Fact]
    public async Task AWrongPasswordAndAnUnknownUserAreTheSameGeneric401()
    {
        await using AdminHarness h = await AdminHarness.StartAsync();
        await h.SetUpOwnerAsync();

        using AdminResponse wrong = await h.PostAsync("/api/v1/auth/login", Body.Login("owner", "not the password at all"));
        using AdminResponse unknown = await h.PostAsync("/api/v1/auth/login", Body.Login("nobody", "not the password at all"));

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.Status);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.Status);
        Assert.Equal(WithoutTrace(wrong.Body), WithoutTrace(unknown.Body)); // the trace ID names the request, not the account
        Assert.Null(wrong.SessionCookie);
        Assert.DoesNotContain("not the password", wrong.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALockedAccountSurfacesAsTheSameGeneric401EvenWithTheRightPassword()
    {
        await using AdminHarness h = await AdminHarness.StartAsync(RoomyLimits);
        await h.SetUpOwnerAsync();

        string? generic = null;
        for (int i = 0; i < 5; i++)
        {
            using AdminResponse failed = await h.PostAsync("/api/v1/auth/login", Body.Login("owner", "wrong password number " + i));
            Assert.Equal(HttpStatusCode.Unauthorized, failed.Status);
            generic = WithoutTrace(failed.Body);
        }

        using AdminResponse locked = await h.PostAsync("/api/v1/auth/login", Body.Login("owner", AdminHarness.Password));

        Assert.Equal(HttpStatusCode.Unauthorized, locked.Status);
        Assert.Equal(generic, WithoutTrace(locked.Body));
        Assert.Null(locked.SessionCookie);
        Assert.NotEmpty(await h.AuditAsync("auth.lockout"));
    }

    [Fact]
    public async Task LoginWithASecondFactor_PartialSessionReachesOnlyTheSecondFactorStep_AndTheCookieRotates()
    {
        await using AdminHarness h = await AdminHarness.StartAsync();
        string owner = await h.SetUpOwnerAsync();

        // Enrol: the URI is shown once, the confirmation returns the recovery codes.
        string uri;
        using (AdminResponse begin = await h.PostAsync("/api/v1/me/totp/enrollment", Body.Password(AdminHarness.Password), owner))
        {
            Assert.Equal(HttpStatusCode.OK, begin.Status);
            uri = begin.Json.GetProperty("provisioningUri").GetString()!;
            Assert.StartsWith("otpauth://totp/", uri, StringComparison.Ordinal);
        }

        string[] recovery;
        using (AdminResponse confirm = await h.PostAsync("/api/v1/me/totp/enrollment/confirm", $$"""{"code":"{{TotpOracle.CodeFor(uri)}}"}""", owner))
        {
            Assert.Equal(HttpStatusCode.OK, confirm.Status);
            recovery = [.. confirm.Json.GetProperty("codes").EnumerateArray().Select(c => c.GetString()!)];
            Assert.Equal(10, recovery.Length);
        }

        using (AdminResponse logout = await h.PostAsync("/api/v1/auth/logout", cookie: owner))
        {
            Assert.Equal(HttpStatusCode.NoContent, logout.Status);
        }

        // Step one: a partial session.
        string partial;
        using (AdminResponse login = await h.PostAsync("/api/v1/auth/login", Body.Login("owner", AdminHarness.Password)))
        {
            Assert.Equal(HttpStatusCode.OK, login.Status);
            Assert.True(login.Json.GetProperty("secondFactorRequired").GetBoolean());
            partial = login.SessionCookie!;
        }

        // It authenticates nothing else.
        foreach (string path in new[] { "/api/v1/auth/me", "/api/v1/users", "/api/v1/sessions", "/api/v1/tokens" })
        {
            using AdminResponse refused = await h.GetAsync(path, partial);
            Assert.Equal(HttpStatusCode.Unauthorized, refused.Status);
        }

        using (AdminResponse wrong = await h.PostAsync("/api/v1/auth/second-factor", """{"code":"000000","recoveryCode":null}""", partial))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.Status);
            Assert.Equal("invalid_code", wrong.Json.GetProperty("code").GetString());
        }

        // The next step's code (the confirmation used the current one, and a step is single-use); the window allows one ahead.
        string full;
        using (AdminResponse second = await h.PostAsync("/api/v1/auth/second-factor", $$"""{"code":"{{TotpOracle.CodeFor(uri, 1)}}","recoveryCode":null}""", partial))
        {
            Assert.Equal(HttpStatusCode.OK, second.Status);
            Assert.False(second.Json.GetProperty("secondFactorRequired").GetBoolean());
            full = second.SessionCookie!;
            Assert.NotEqual(partial, full);
        }

        using (AdminResponse me = await h.GetAsync("/api/v1/auth/me", full))
        {
            Assert.Equal(HttpStatusCode.OK, me.Status);
        }

        using (AdminResponse old = await h.GetAsync("/api/v1/auth/me", partial))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, old.Status);
        }

        // A recovery code works once.
        string second2;
        using (AdminResponse login = await h.PostAsync("/api/v1/auth/login", Body.Login("owner", AdminHarness.Password)))
        {
            second2 = login.SessionCookie!;
        }

        using (AdminResponse used = await h.PostAsync("/api/v1/auth/second-factor", $$"""{"code":null,"recoveryCode":"{{recovery[0]}}"}""", second2))
        {
            Assert.Equal(HttpStatusCode.OK, used.Status);
            Assert.DoesNotContain(recovery[0], used.Body, StringComparison.Ordinal);
        }

        string third;
        using (AdminResponse login = await h.PostAsync("/api/v1/auth/login", Body.Login("owner", AdminHarness.Password)))
        {
            third = login.SessionCookie!;
        }

        using (AdminResponse reused = await h.PostAsync("/api/v1/auth/second-factor", $$"""{"code":null,"recoveryCode":"{{recovery[0]}}"}""", third))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, reused.Status);
        }

        // Turning it off needs the password.
        using (AdminResponse wrongPassword = await h.SendAsync(HttpMethod.Delete, "/api/v1/me/totp", Body.Password("nope nope nope nope"), full))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, wrongPassword.Status);
        }

        using (AdminResponse off = await h.SendAsync(HttpMethod.Delete, "/api/v1/me/totp", Body.Password(AdminHarness.Password), full))
        {
            Assert.Equal(HttpStatusCode.NoContent, off.Status);
        }

        // The codes are never audited.
        foreach (AuditRecord entry in await h.AuditAsync("auth.login"))
        {
            Assert.DoesNotContain(recovery[0], entry.Details ?? "", StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task LogoutClearsTheCookieAndRevokesTheSession()
    {
        await using AdminHarness h = await AdminHarness.StartAsync();
        string cookie = await h.SetUpOwnerAsync();

        using AdminResponse logout = await h.PostAsync("/api/v1/auth/logout", cookie: cookie);

        Assert.Equal(HttpStatusCode.NoContent, logout.Status);
        string line = Assert.Single(logout.SetCookies);
        Assert.StartsWith("__Host-paduraksa-session=;", line, StringComparison.Ordinal);
        Assert.Contains("expires=Thu, 01 Jan 1970", line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("max-age=0", line, StringComparison.OrdinalIgnoreCase);

        using AdminResponse me = await h.GetAsync("/api/v1/auth/me", cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, me.Status);
        IReadOnlyList<AuditRecord> audit = await h.AuditAsync("auth.logout");
        Assert.Contains(audit, a => a.Channel == "api" && a.ClientIp == "127.0.0.1");
    }

    [Fact]
    public async Task AStaleCookieDoesNotStopASignIn_ButIsRefusedOnEveryOtherEndpoint()
    {
        await using AdminHarness h = await AdminHarness.StartAsync();
        await h.SetUpOwnerAsync();
        string stale = new('a', 43);

        using AdminResponse login = await h.PostAsync("/api/v1/auth/login", Body.Login("owner", AdminHarness.Password), stale);
        Assert.Equal(HttpStatusCode.OK, login.Status);
        Assert.Single(login.SetCookies); // the new session's cookie, not also a clearing of the stale one
        Assert.NotNull(login.SessionCookie);

        using AdminResponse status = await h.GetAsync("/api/v1/setup", stale);
        Assert.Equal(HttpStatusCode.OK, status.Status);

        using AdminResponse logout = await h.PostAsync("/api/v1/auth/logout", cookie: stale);
        Assert.Equal(HttpStatusCode.NoContent, logout.Status);

        using AdminResponse other = await h.GetAsync("/api/v1/auth/me", stale);
        Assert.Equal(HttpStatusCode.Unauthorized, other.Status);
        Assert.StartsWith("__Host-paduraksa-session=;", Assert.Single(other.SetCookies), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AmbiguousOrMalformedCredentialsAreRefusedNeverTreatedAsAnonymous()
    {
        await using AdminHarness h = await AdminHarness.StartAsync();
        string cookie = await h.SetUpOwnerAsync();
        (_, string secret) = await h.IssueTokenAsync(cookie, "config:read");

        // Both credentials: not guessed.
        using (AdminResponse both = await h.GetAsync("/api/v1/auth/me", cookie, secret))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, both.Status);
            Assert.Equal("ambiguous_credentials", both.Json.GetProperty("code").GetString());
            Assert.Equal("Bearer error=\"invalid_request\"", both.Header("WWW-Authenticate"));
        }

        // Anything but Bearer.
        using (AdminResponse basic = await h.SendAsync(HttpMethod.Get, "/api/v1/auth/me", headers: new Dictionary<string, string> { ["Authorization"] = "Basic dXNlcjpwYXNz" }))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, basic.Status);
        }

        // A bad token, with or without the right shape, on an endpoint that would also accept anonymous callers.
        foreach (string bad in new[] { "pdk_garbage", "garbage", secret[..^2] + "xx" })
        {
            using AdminResponse badToken = await h.SendAsync(HttpMethod.Get, "/api/v1/setup", bearer: bad);
            Assert.Equal(HttpStatusCode.Unauthorized, badToken.Status); // even where anonymous callers are welcome: a bad bearer is never ignored
            using AdminResponse refused = await h.GetAsync("/api/v1/auth/me", bearer: bad);
            Assert.Equal(HttpStatusCode.Unauthorized, refused.Status);
            Assert.Equal("Bearer error=\"invalid_token\"", refused.Header("WWW-Authenticate"));
            Assert.DoesNotContain(bad, refused.Body, StringComparison.Ordinal);
        }

        // Two cookies of that name.
        using AdminResponse twice = await h.SendAsync(
            HttpMethod.Get,
            "/api/v1/auth/me",
            headers: new Dictionary<string, string> { ["Cookie"] = $"{AdminSessionCookie.Name}={cookie}; {AdminSessionCookie.Name}={cookie}" });
        Assert.Equal(HttpStatusCode.Unauthorized, twice.Status);
    }

    [Fact]
    public async Task ChangingThePasswordNeedsTheCurrentOneAndKeepsOnlyTheCurrentSession()
    {
        await using AdminHarness h = await AdminHarness.StartAsync();
        string first = await h.SetUpOwnerAsync();
        string other = await h.SignInAsync("owner");

        using (AdminResponse wrong = await h.PostAsync("/api/v1/me/password", """{"currentPassword":"wrong wrong wrong","newPassword":"a brand new passphrase"}""", first))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, wrong.Status);
        }

        using (AdminResponse changed = await h.PostAsync("/api/v1/me/password", $$"""{"currentPassword":"{{AdminHarness.Password}}","newPassword":"a brand new passphrase"}""", first))
        {
            Assert.Equal(HttpStatusCode.NoContent, changed.Status);
        }

        using (AdminResponse stillIn = await h.GetAsync("/api/v1/auth/me", first))
        {
            Assert.Equal(HttpStatusCode.OK, stillIn.Status);
        }

        using (AdminResponse ended = await h.GetAsync("/api/v1/auth/me", other))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, ended.Status);
        }

        using (AdminResponse old = await h.PostAsync("/api/v1/auth/login", Body.Login("owner", AdminHarness.Password)))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, old.Status);
        }

        await h.SignInAsync("owner", "a brand new passphrase");
    }

    [Fact]
    public async Task TooManyAttemptsPerUsernameAre429WithRetryAfter_DifferentUsernamesAreNot()
    {
        await using AdminHarness h = await AdminHarness.StartAsync(new Dictionary<string, string?>
        {
            ["Paduraksa:Admin:RateLimit:PermitsPerIp"] = "1000",
            ["Paduraksa:Admin:RateLimit:PermitsPerUser"] = "3",
        });
        await h.SetUpOwnerAsync();

        for (int i = 0; i < 3; i++)
        {
            using AdminResponse attempt = await h.PostAsync("/api/v1/auth/login", Body.Login("victim", "wrong password " + i));
            Assert.Equal(HttpStatusCode.Unauthorized, attempt.Status);
        }

        // The username is normalised: another case is the same budget.
        using AdminResponse limited = await h.PostAsync("/api/v1/auth/login", Body.Login("VICTIM", "wrong password 4"));
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.Status);
        Assert.True(int.Parse(limited.Header("Retry-After")!, System.Globalization.CultureInfo.InvariantCulture) >= 1);
        Assert.Equal("rate_limited", limited.Json.GetProperty("code").GetString());

        using AdminResponse someoneElse = await h.PostAsync("/api/v1/auth/login", Body.Login("owner", AdminHarness.Password));
        Assert.Equal(HttpStatusCode.OK, someoneElse.Status);

        // Only failures count against a name: the owner can sign in as often as they like (within the per-client budget).
        for (int i = 0; i < 5; i++)
        {
            using AdminResponse again = await h.PostAsync("/api/v1/auth/login", Body.Login("owner", AdminHarness.Password));
            Assert.Equal(HttpStatusCode.OK, again.Status);
        }

        // Retry-After is the time left in the window.
        Assert.InRange(int.Parse(limited.Header("Retry-After")!, System.Globalization.CultureInfo.InvariantCulture), 1, 60);
    }

    [Fact]
    public async Task TooManyAttemptsPerClientIpAre429EvenForDifferentUsernames()
    {
        await using AdminHarness h = await AdminHarness.StartAsync(new Dictionary<string, string?>
        {
            ["Paduraksa:Admin:RateLimit:PermitsPerIp"] = "3",
            ["Paduraksa:Admin:RateLimit:PermitsPerUser"] = "1000",
        });

        // Setup is its own scope with its own budget; a wrong token is refused, and counted.
        for (int i = 0; i < 3; i++)
        {
            using AdminResponse attempt = await h.PostAsync("/api/v1/auth/login", Body.Login("user" + i, "wrong password"));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, attempt.Status); // the gate is closed: not counted
        }

        for (int i = 0; i < 3; i++)
        {
            using AdminResponse attempt = await h.PostAsync("/api/v1/setup", Body.Setup("wrong", "user" + i, AdminHarness.Password));
            Assert.Equal(HttpStatusCode.Forbidden, attempt.Status);
        }

        using AdminResponse limited = await h.PostAsync("/api/v1/setup", Body.Setup(await h.SetupTokenAsync(), "owner", AdminHarness.Password));
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.Status);
        Assert.NotNull(limited.Header("Retry-After"));
    }
}
