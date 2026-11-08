using System.Net;

namespace Paduraksa.Tests.Integration.Admin;

/// <summary>Cookie-authenticated unsafe requests must come from the same origin and carry JSON; bearer requests are exempt.</summary>
public sealed class CsrfTests
{
    private const string Path = "/api/v1/me/password";

    private static string Wrong => """{"currentPassword":"wrong wrong wrong","newPassword":"a brand new passphrase"}""";

    [Fact]
    public async Task ASameOriginRequestIsAllowed()
    {
        await using AdminHarness h = await AdminHarness.StartAsync();
        string cookie = await h.SetUpOwnerAsync();

        using AdminResponse viaFetchMetadata = await h.SendAsync(HttpMethod.Post, Path, Wrong, cookie);
        using AdminResponse viaOrigin = await h.SendAsync(
            HttpMethod.Post, Path, Wrong, cookie, browser: false, headers: new Dictionary<string, string> { ["Origin"] = h.Admin.GetLeftPart(UriPartial.Authority) });

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
        await using AdminHarness h = await AdminHarness.StartAsync();
        string cookie = await h.SetUpOwnerAsync();

        using AdminResponse response = await h.SendAsync(HttpMethod.Post, Path, Wrong, cookie, headers: new Dictionary<string, string> { ["Sec-Fetch-Site"] = site });

        Assert.Equal(HttpStatusCode.Forbidden, response.Status);
        Assert.Equal("csrf", response.Json.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("null")]
    [InlineData("http://127.0.0.1")]
    [InlineData("not a url")]
    public async Task AnOriginThatIsNotTheServersIsRefused(string origin)
    {
        await using AdminHarness h = await AdminHarness.StartAsync();
        string cookie = await h.SetUpOwnerAsync();

        using AdminResponse response = await h.SendAsync(HttpMethod.Post, Path, Wrong, cookie, browser: false, headers: new Dictionary<string, string> { ["Origin"] = origin });

        Assert.Equal(HttpStatusCode.Forbidden, response.Status);
    }

    [Fact]
    public async Task TheRightHostWithAnotherPortIsAnotherOrigin()
    {
        await using AdminHarness h = await AdminHarness.StartAsync();
        string cookie = await h.SetUpOwnerAsync();
        string otherPort = $"https://{h.Admin.Host}:{h.Admin.Port + 1}";

        using AdminResponse response = await h.SendAsync(HttpMethod.Post, Path, Wrong, cookie, browser: false, headers: new Dictionary<string, string> { ["Origin"] = otherPort });

        Assert.Equal(HttpStatusCode.Forbidden, response.Status);
    }

    [Fact]
    public async Task ACookieRequestThatSaysNothingAboutWhereItCameFromIsRefused_ButAScriptsSignInIsNot()
    {
        await using AdminHarness h = await AdminHarness.StartAsync();
        string cookie = await h.SetUpOwnerAsync();

        using AdminResponse withCookie = await h.SendAsync(HttpMethod.Post, Path, Wrong, cookie, browser: false);
        Assert.Equal(HttpStatusCode.Forbidden, withCookie.Status);

        // curl: no cookie, no Origin, no Sec-Fetch-Site.
        using AdminResponse login = await h.SendAsync(HttpMethod.Post, "/api/v1/auth/login", Body.Login("owner", AdminHarness.Password), browser: false);
        Assert.Equal(HttpStatusCode.OK, login.Status);

        // A browser's cross-site sign-in (login CSRF) is refused even without a cookie.
        using AdminResponse crossSite = await h.SendAsync(
            HttpMethod.Post, "/api/v1/auth/login", Body.Login("owner", AdminHarness.Password), headers: new Dictionary<string, string> { ["Sec-Fetch-Site"] = "cross-site" });
        Assert.Equal(HttpStatusCode.Forbidden, crossSite.Status);
        Assert.Null(crossSite.SessionCookie);
    }

    [Fact]
    public async Task ACookieRequestWithABodyMustBeJson_ButOneWithoutABodyNeedsNoContentType()
    {
        await using AdminHarness h = await AdminHarness.StartAsync();
        string cookie = await h.SetUpOwnerAsync();

        foreach (string type in new[] { "text/plain", "application/x-www-form-urlencoded", "multipart/form-data; boundary=x" })
        {
            using AdminResponse refused = await h.SendAsync(HttpMethod.Post, Path, Wrong, cookie, contentType: type);
            Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        }

        using AdminResponse problemJson = await h.SendAsync(HttpMethod.Post, Path, Wrong, cookie, contentType: "application/merge-patch+json");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, problemJson.Status);

        using AdminResponse noBody = await h.SendAsync(HttpMethod.Post, "/api/v1/sessions/revoke-others", cookie: cookie);
        Assert.Equal(HttpStatusCode.NoContent, noBody.Status);
    }

    [Fact]
    public async Task BearerRequestsAreExempt()
    {
        await using AdminHarness h = await AdminHarness.StartAsync();
        string cookie = await h.SetUpOwnerAsync();
        (_, string secret) = await h.IssueTokenAsync(cookie, "users:write");

        // Not a browser's request, so the cross-site header means nothing: the user is created.
        using AdminResponse crossSite = await h.SendAsync(
            HttpMethod.Post,
            "/api/v1/users",
            """{"username":"made-by-token","displayName":null,"password":"correct horse battery staple","roles":["viewer"]}""",
            bearer: secret,
            headers: new Dictionary<string, string> { ["Sec-Fetch-Site"] = "cross-site" });

        Assert.Equal(HttpStatusCode.Created, crossSite.Status);
    }

    [Fact]
    public async Task ABodyOfUnknownLengthIsStillCheckedForItsContentType_OverHttp1AndHttp2()
    {
        await using AdminHarness h = await AdminHarness.StartAsync();
        string cookie = await h.SetUpOwnerAsync();

        foreach (Version version in new[] { System.Net.HttpVersion.Version11, System.Net.HttpVersion.Version20 })
        {
            using AdminResponse refused = await h.SendAsync(HttpMethod.Post, Path, Wrong, cookie, contentType: "text/plain", version: version, streamed: true);
            Assert.Equal(HttpStatusCode.Forbidden, refused.Status);

            using AdminResponse allowed = await h.SendAsync(HttpMethod.Post, Path, Wrong, cookie, version: version, streamed: true);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, allowed.Status);
        }
    }

    [Fact]
    public async Task SafeMethodsAreNotChecked()
    {
        await using AdminHarness h = await AdminHarness.StartAsync();
        string cookie = await h.SetUpOwnerAsync();

        using AdminResponse response = await h.SendAsync(HttpMethod.Get, "/api/v1/auth/me", cookie: cookie, headers: new Dictionary<string, string> { ["Sec-Fetch-Site"] = "cross-site" });

        Assert.Equal(HttpStatusCode.OK, response.Status);
    }
}
