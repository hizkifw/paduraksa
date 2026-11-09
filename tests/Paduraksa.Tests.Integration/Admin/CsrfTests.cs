using System.Globalization;
using System.Net;

using Paduraksa.Tests.Integration.Hosting;

namespace Paduraksa.Tests.Integration.Admin;

/// <summary>Cookie-authenticated unsafe requests must come from the same origin and carry JSON; bearer requests are exempt.</summary>
public sealed class CsrfTests(SharedOwnerSession shared) : IClassFixture<SharedOwnerSession>
{
    private const string Path = "/api/v1/me/password";

    private static int s_tag;

    private static string Wrong => """{"currentPassword":"wrong wrong wrong","newPassword":"a brand new passphrase"}""";

    /// <summary>
    /// A user of this test's own, signed in: the wrong password these tests send counts toward that account's lockout, which
    /// must not be the account the rest of the class signs in as.
    /// </summary>
    private async Task<(string User, string Cookie)> NewSessionAsync()
    {
        string user = "csrf" + Interlocked.Increment(ref s_tag).ToString(CultureInfo.InvariantCulture);
        await shared.Harness.CreateUserAsync(shared.OwnerCookie, user, "admin");
        return (user, await shared.Harness.SignInAsync(user));
    }

    [Fact]
    public async Task ASameOriginRequestIsAllowed()
    {
        (_, string cookie) = await NewSessionAsync();

        using AdminResponse viaFetchMetadata = await shared.Harness.SendAsync(HttpMethod.Post, Path, Wrong, cookie);
        using AdminResponse viaOrigin = await shared.Harness.SendAsync(
            HttpMethod.Post, Path, Wrong, cookie, browser: false, headers: new Dictionary<string, string> { ["Origin"] = shared.Harness.Admin.GetLeftPart(UriPartial.Authority) });

        // The request got through to the service, which refuses the wrong password (422), not the CSRF check (403).
        Assert.Equal(HttpStatusCode.UnprocessableEntity, viaFetchMetadata.Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, viaOrigin.Status);
    }

    [Theory]
    [InlineData("cross-site")]
    [InlineData("same-site")]
    [InlineData("none")]
    public async Task ACookieRequestFromAnotherSiteIsRefused(string site)
    {
        (_, string cookie) = await NewSessionAsync();

        using AdminResponse response = await shared.Harness.SendAsync(HttpMethod.Post, Path, Wrong, cookie, headers: new Dictionary<string, string> { ["Sec-Fetch-Site"] = site });

        Assert.Equal(HttpStatusCode.Forbidden, response.Status);
        Assert.Equal("csrf.refused", response.Json.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("null")]
    [InlineData("http://127.0.0.1")]
    [InlineData("not a url")]
    public async Task AnOriginThatIsNotTheServersIsRefused(string origin)
    {
        (_, string cookie) = await NewSessionAsync();

        using AdminResponse response = await shared.Harness.SendAsync(HttpMethod.Post, Path, Wrong, cookie, browser: false, headers: new Dictionary<string, string> { ["Origin"] = origin });

        Assert.Equal(HttpStatusCode.Forbidden, response.Status);
    }

    [Fact]
    public async Task TheRightHostWithAnotherPortIsAnotherOrigin()
    {
        (_, string cookie) = await NewSessionAsync();
        string otherPort = $"https://{shared.Harness.Admin.Host}:{shared.Harness.Admin.Port + 1}";

        using AdminResponse response = await shared.Harness.SendAsync(HttpMethod.Post, Path, Wrong, cookie, browser: false, headers: new Dictionary<string, string> { ["Origin"] = otherPort });

        Assert.Equal(HttpStatusCode.Forbidden, response.Status);
    }

    [Fact]
    public async Task ACookieRequestThatSaysNothingAboutWhereItCameFromIsRefused_ButAScriptsSignInIsNot()
    {
        (string user, string cookie) = await NewSessionAsync();

        using AdminResponse withCookie = await shared.Harness.SendAsync(HttpMethod.Post, Path, Wrong, cookie, browser: false);
        Assert.Equal(HttpStatusCode.Forbidden, withCookie.Status);

        // curl: no cookie, no Origin, no Sec-Fetch-Site.
        using AdminResponse login = await shared.Harness.SendAsync(HttpMethod.Post, "/api/v1/auth/login", Body.Login(user, AdminHarness.Password), browser: false);
        Assert.Equal(HttpStatusCode.OK, login.Status);

        // A browser's cross-site sign-in (login CSRF) is refused even without a cookie.
        using AdminResponse crossSite = await shared.Harness.SendAsync(
            HttpMethod.Post, "/api/v1/auth/login", Body.Login(user, AdminHarness.Password), headers: new Dictionary<string, string> { ["Sec-Fetch-Site"] = "cross-site" });
        Assert.Equal(HttpStatusCode.Forbidden, crossSite.Status);
        Assert.Null(crossSite.SessionCookie);
    }

    [Fact]
    public async Task ACookieRequestWithABodyMustBeJson_ButOneWithoutABodyNeedsNoContentType()
    {
        (_, string cookie) = await NewSessionAsync();

        foreach (string type in new[] { "text/plain", "application/x-www-form-urlencoded", "multipart/form-data; boundary=x" })
        {
            using AdminResponse refused = await shared.Harness.SendAsync(HttpMethod.Post, Path, Wrong, cookie, contentType: type);
            Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        }

        using AdminResponse problemJson = await shared.Harness.SendAsync(HttpMethod.Post, Path, Wrong, cookie, contentType: "application/merge-patch+json");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, problemJson.Status);

        using AdminResponse noBody = await shared.Harness.SendAsync(HttpMethod.Post, "/api/v1/sessions/revoke-others", cookie: cookie);
        Assert.Equal(HttpStatusCode.NoContent, noBody.Status);
    }

    [Fact]
    public async Task BearerRequestsAreExempt()
    {
        // The Owner's own session: issuing a token with users:write needs that permission, which only the Owner holds.
        string cookie = shared.OwnerCookie;
        (_, string secret) = await shared.Harness.IssueTokenAsync(cookie, "users:write");

        // Not a browser's request, so the cross-site header means nothing: the user is created.
        using AdminResponse crossSite = await shared.Harness.SendAsync(
            HttpMethod.Post,
            "/api/v1/users",
            $$"""{"username":"made-by-token-{{Interlocked.Increment(ref s_tag).ToString(CultureInfo.InvariantCulture)}}","displayName":null,"password":"correct horse battery staple","roles":["viewer"]}""",
            bearer: secret,
            headers: new Dictionary<string, string> { ["Sec-Fetch-Site"] = "cross-site" });

        Assert.Equal(HttpStatusCode.Created, crossSite.Status);
    }

    [Fact]
    public async Task ABodyOfUnknownLengthIsStillCheckedForItsContentType_OverHttp1AndHttp2()
    {
        (_, string cookie) = await NewSessionAsync();

        foreach (Version version in new[] { System.Net.HttpVersion.Version11, System.Net.HttpVersion.Version20 })
        {
            using AdminResponse refused = await shared.Harness.SendAsync(HttpMethod.Post, Path, Wrong, cookie, contentType: "text/plain", version: version, streamed: true);
            Assert.Equal(HttpStatusCode.Forbidden, refused.Status);

            using AdminResponse allowed = await shared.Harness.SendAsync(HttpMethod.Post, Path, Wrong, cookie, version: version, streamed: true);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, allowed.Status);
        }
    }

    [Fact]
    public async Task SafeMethodsAreNotChecked()
    {
        (_, string cookie) = await NewSessionAsync();

        using AdminResponse response = await shared.Harness.SendAsync(HttpMethod.Get, "/api/v1/auth/me", cookie: cookie, headers: new Dictionary<string, string> { ["Sec-Fetch-Site"] = "cross-site" });

        Assert.Equal(HttpStatusCode.OK, response.Status);
    }
}
