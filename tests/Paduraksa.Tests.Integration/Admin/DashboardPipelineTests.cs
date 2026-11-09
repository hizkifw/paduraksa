using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

using Paduraksa.Admin.Web;
using Paduraksa.Dashboard;

namespace Paduraksa.Tests.Integration.Admin;

/// <summary>
/// The dashboard in the admin pipeline (PLAN §12): the setup gate sends a browser to the setup page and opens only it, its form and the
/// static files; pages need a signed-in session and send everyone else to the sign-in page; the sign-in forms need their antiforgery
/// token and the same origin and set the same session cookie as the API; the circuit hub takes only signed-in, same-origin callers; HTML
/// pages get the dashboard's CSP and the API keeps its own.
/// </summary>
public sealed partial class DashboardPipelineTests
{
    private const string FormType = "application/x-www-form-urlencoded";
    private const string Html = "text/html,application/xhtml+xml";

    [Fact]
    public async Task DuringSetup_ABrowserIsSentToTheSetupPage_WhichIsOpenWithItsFiles()
    {
        await using AdminHarness h = await AdminHarness.StartAsync();

        foreach (string path in new[] { "/", "/hosts", DashboardRoutes.SignIn })
        {
            using AdminResponse navigation = await GetPageAsync(h, path);
            Assert.Equal(HttpStatusCode.Found, navigation.Status);
            Assert.Equal(DashboardRoutes.Setup, navigation.Headers.Location?.OriginalString);
        }

        // A script (no text/html) still gets the problem it always did.
        using (AdminResponse script = await h.GetAsync("/"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, script.Status);
            Assert.Equal("setup.required", script.Json.GetProperty("code").GetString());
        }

        using (AdminResponse setup = await GetPageAsync(h, DashboardRoutes.Setup))
        {
            Assert.Equal(HttpStatusCode.OK, setup.Status);
            Assert.Equal(DashboardSecurity.ContentSecurityPolicy, setup.Header("Content-Security-Policy"));
            Assert.Contains($"action=\"{DashboardRoutes.Actions.Setup}\"", setup.Body, StringComparison.Ordinal);
        }

        foreach (string asset in new[] { "/_framework/blazor.web.js", "/_content/MudBlazor/MudBlazor.min.css", "/_content/Paduraksa.Dashboard/app.css" })
        {
            using AdminResponse file = await h.GetAsync(asset);
            Assert.Equal(HttpStatusCode.OK, file.Status);
        }

        // No circuit before an Owner exists.
        using AdminResponse negotiate = await h.SendAsync(HttpMethod.Post, "/_blazor/negotiate?negotiateVersion=1", json: "", contentType: "text/plain");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, negotiate.Status);
    }

    [Fact]
    public async Task TheSetupForm_CreatesTheOwner_SignsThemIn_AndIsAuditedAsTheDashboard()
    {
        await using AdminHarness h = await AdminHarness.StartAsync();
        Antiforgery form = await AntiforgeryAsync(h, DashboardRoutes.Setup);
        string token = await h.SetupTokenAsync();

        using (AdminResponse wrong = await PostFormAsync(h, DashboardRoutes.Actions.Setup, form, Setup("not-the-token", AdminHarness.Password, AdminHarness.Password)))
        {
            AssertSeeOther(wrong, "/setup?error=setup.invalidToken");
            Assert.Null(wrong.SessionCookie);
        }

        using (AdminResponse mismatch = await PostFormAsync(h, DashboardRoutes.Actions.Setup, form, Setup(token, AdminHarness.Password, "something else entirely")))
        {
            AssertSeeOther(mismatch, "/setup?error=setup.passwordMismatch");
        }

        using (AdminResponse weak = await PostFormAsync(h, DashboardRoutes.Actions.Setup, form, Setup(token, "short", "short")))
        {
            AssertSeeOther(weak, "/setup?error=identity.refused");
        }

        string cookie;
        using (AdminResponse created = await PostFormAsync(h, DashboardRoutes.Actions.Setup, form, Setup(token, AdminHarness.Password, AdminHarness.Password)))
        {
            // The Owner lands on the overview: there is no wizard after setup.
            AssertSeeOther(created, DashboardRoutes.Home);
            cookie = created.SessionCookie ?? throw new InvalidOperationException("Setup set no session cookie.");
            Assert.DoesNotContain(AdminHarness.Password, created.Body, StringComparison.Ordinal);
        }

        // The same session the API takes.
        using (AdminResponse me = await h.GetAsync("/api/v1/auth/me", cookie))
        {
            Assert.Equal(HttpStatusCode.OK, me.Status);
        }

        using (AdminResponse home = await GetPageAsync(h, "/", cookie))
        {
            Assert.Equal(HttpStatusCode.OK, home.Status);
            Assert.Contains(">Overview</h1>", home.Body, StringComparison.Ordinal);
        }

        // Done once: the setup page now points at the sign-in page.
        using (AdminResponse again = await GetPageAsync(h, DashboardRoutes.Setup))
        {
            Assert.Equal(HttpStatusCode.Found, again.Status);
            Assert.EndsWith(DashboardRoutes.SignIn, again.Headers.Location?.OriginalString, StringComparison.Ordinal);
        }

        IReadOnlyList<AuditRecord> audit = await h.AuditAsync("setup.complete");
        Assert.Equal(["denied", "success"], audit.Select(a => a.Outcome));
        Assert.All(audit, a =>
        {
            Assert.Equal("dashboard", a.Channel);
            Assert.DoesNotContain(token, a.Details ?? "", StringComparison.Ordinal);
            Assert.DoesNotContain(AdminHarness.Password, a.Details ?? "", StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task ThePagesNeedASession_AndSendEveryoneElseToSignIn_WithTheWayBack()
    {
        await using AdminHarness h = await AdminHarness.StartAsync();
        string owner = await h.SetUpOwnerAsync();

        using (AdminResponse home = await GetPageAsync(h, "/"))
        {
            Assert.Equal(HttpStatusCode.Found, home.Status);
            Assert.Equal("/signin", home.Headers.Location?.OriginalString);
        }

        using (AdminResponse hosts = await GetPageAsync(h, "/hosts?tag=media"))
        {
            Assert.Equal(HttpStatusCode.Found, hosts.Status);
            Assert.Equal("/signin?returnUrl=%2Fhosts%3Ftag%3Dmedia", hosts.Headers.Location?.OriginalString);
        }

        // A cookie that names no session is no session (and the sign-in page still opens for it).
        using (AdminResponse stale = await GetPageAsync(h, "/", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"))
        {
            Assert.Equal(HttpStatusCode.Found, stale.Status);
        }

        using (AdminResponse signIn = await GetPageAsync(h, DashboardRoutes.SignIn, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"))
        {
            Assert.Equal(HttpStatusCode.OK, signIn.Status);
        }

        using (AdminResponse page = await GetPageAsync(h, "/hosts", owner))
        {
            Assert.Equal(HttpStatusCode.OK, page.Status);
            Assert.Contains("Every name this instance answers for", page.Body, StringComparison.Ordinal);
        }

        // A bearer token is an API credential, never a dashboard one.
        (_, string token) = await h.IssueTokenAsync(owner, "config:read");
        using (AdminResponse bearer = await h.SendAsync(HttpMethod.Get, "/", bearer: token, headers: new Dictionary<string, string> { ["Accept"] = Html }))
        {
            Assert.Equal(HttpStatusCode.Found, bearer.Status);
        }
    }

    [Fact]
    public async Task SignInThroughTheForm_SetsTheSessionCookie_GoesBackOnlyToThisSite_AndIsAudited()
    {
        await using AdminHarness h = await AdminHarness.StartAsync();
        await h.SetUpOwnerAsync();
        Antiforgery form = await AntiforgeryAsync(h, DashboardRoutes.SignIn);

        using (AdminResponse wrong = await PostFormAsync(h, DashboardRoutes.Actions.SignIn, form, SignIn("not the password", "/certificates")))
        {
            AssertSeeOther(wrong, "/signin?error=auth.invalidCredentials&returnUrl=%2Fcertificates");
            Assert.Null(wrong.SessionCookie);
        }

        string cookie;
        using (AdminResponse right = await PostFormAsync(h, DashboardRoutes.Actions.SignIn, form, SignIn(AdminHarness.Password, "/certificates")))
        {
            AssertSeeOther(right, "/certificates");
            cookie = right.SessionCookie ?? throw new InvalidOperationException("No session cookie.");
            Assert.Contains(right.SetCookies, line => line.StartsWith("__Host-paduraksa-session=", StringComparison.Ordinal)
                && line.Contains("secure", StringComparison.OrdinalIgnoreCase)
                && line.Contains("httponly", StringComparison.OrdinalIgnoreCase)
                && line.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase));
        }

        using (AdminResponse me = await h.GetAsync("/api/v1/auth/me", cookie))
        {
            Assert.Equal(HttpStatusCode.OK, me.Status);
        }

        foreach (string foreign in new[] { "//evil.example/", "https://evil.example/", "/\\evil.example" })
        {
            using AdminResponse elsewhere = await PostFormAsync(h, DashboardRoutes.Actions.SignIn, form, SignIn(AdminHarness.Password, foreign));
            AssertSeeOther(elsewhere, "/");
        }

        IReadOnlyList<AuditRecord> logins = await h.AuditAsync("auth.login");
        Assert.Contains(logins, a => a.Outcome == "denied" && a.Channel == "dashboard");
        Assert.Contains(logins, a => a.Outcome == "success" && a.Channel == "dashboard");
        Assert.All(logins, a => Assert.DoesNotContain(AdminHarness.Password, a.Details ?? "", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SignInWithASecondFactor_TakesTheCodeOnTheSecondPage()
    {
        await using AdminHarness h = await AdminHarness.StartAsync();
        string owner = await h.SetUpOwnerAsync();
        string uri;
        using (AdminResponse begin = await h.PostAsync("/api/v1/me/totp/enrollment", Body.Password(AdminHarness.Password), owner))
        {
            uri = begin.Json.GetProperty("provisioningUri").GetString()!;
        }

        using (AdminResponse confirm = await h.PostAsync("/api/v1/me/totp/enrollment/confirm", $$"""{"code":"{{TotpOracle.CodeFor(uri)}}"}""", owner))
        {
            Assert.Equal(HttpStatusCode.OK, confirm.Status);
        }

        Antiforgery form = await AntiforgeryAsync(h, DashboardRoutes.SignIn);
        string partial;
        using (AdminResponse password = await PostFormAsync(h, DashboardRoutes.Actions.SignIn, form, SignIn(AdminHarness.Password, "/audit")))
        {
            AssertSeeOther(password, "/signin/verify?returnUrl=%2Faudit");
            partial = password.SessionCookie!;
        }

        // The partial session opens no page.
        using (AdminResponse home = await GetPageAsync(h, "/", partial))
        {
            Assert.Equal(HttpStatusCode.Found, home.Status);
        }

        Dictionary<string, string> wrongCode = new() { [AdminWebForm.Code] = "000000", [AdminWebForm.ReturnUrl] = "/audit" };
        using (AdminResponse wrong = await PostFormAsync(h, DashboardRoutes.Actions.SecondFactor, form, wrongCode, partial))
        {
            AssertSeeOther(wrong, "/signin/verify?error=auth.invalidCode&returnUrl=%2Faudit");
        }

        // An authenticator shows the code with a space in the middle; that's accepted.
        string code = TotpOracle.CodeFor(uri, 1);
        Dictionary<string, string> rightCode = new() { [AdminWebForm.Code] = $"{code[..3]} {code[3..]}", [AdminWebForm.ReturnUrl] = "/audit" };
        using (AdminResponse right = await PostFormAsync(h, DashboardRoutes.Actions.SecondFactor, form, rightCode, partial))
        {
            AssertSeeOther(right, "/audit");
            string full = right.SessionCookie!;
            Assert.NotEqual(partial, full);
            using AdminResponse me = await h.GetAsync("/api/v1/auth/me", full);
            Assert.Equal(HttpStatusCode.OK, me.Status);
        }

        // Without a partial session the step starts over.
        using AdminResponse gone = await PostFormAsync(h, DashboardRoutes.Actions.SecondFactor, form, rightCode);
        AssertSeeOther(gone, "/signin?error=auth.sessionExpired&returnUrl=%2Faudit");
    }

    [Fact]
    public async Task TheForms_NeedTheirAntiforgeryToken_AndTheSameOrigin()
    {
        await using AdminHarness h = await AdminHarness.StartAsync();
        string owner = await h.SetUpOwnerAsync();
        Antiforgery form = await AntiforgeryAsync(h, DashboardRoutes.SignIn);

        using (AdminResponse noToken = await PostFormAsync(h, DashboardRoutes.Actions.SignIn, form with { Token = "" }, SignIn(AdminHarness.Password, null)))
        {
            AssertSeeOther(noToken, "/signin?error=csrf.refused");
            Assert.Null(noToken.SessionCookie);
        }

        using (AdminResponse noCookie = await PostFormAsync(h, DashboardRoutes.Actions.SignIn, form with { Cookie = "" }, SignIn(AdminHarness.Password, null)))
        {
            AssertSeeOther(noCookie, "/signin?error=csrf.refused");
        }

        using (AdminResponse crossSite = await PostFormAsync(h, DashboardRoutes.Actions.SignIn, form, SignIn(AdminHarness.Password, null), fetchSite: "cross-site"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, crossSite.Status);
            Assert.Equal("csrf.refused", crossSite.Json.GetProperty("code").GetString());
            Assert.Null(crossSite.SessionCookie);
        }

        // Signing out with a token rendered for nobody (a stale or forged page) ends nothing.
        using (AdminResponse signOut = await PostFormAsync(h, DashboardRoutes.Actions.SignOut, form, new Dictionary<string, string>(), owner))
        {
            AssertSeeOther(signOut, "/");
        }

        using AdminResponse me = await h.GetAsync("/api/v1/auth/me", owner);
        Assert.Equal(HttpStatusCode.OK, me.Status);
    }

    [Fact]
    public async Task TheCircuitHub_TakesOnlySignedInSameOriginCallers()
    {
        await using AdminHarness h = await AdminHarness.StartAsync();
        string owner = await h.SetUpOwnerAsync();
        const string Negotiate = "/_blazor/negotiate?negotiateVersion=1";

        using (AdminResponse anonymous = await h.SendAsync(HttpMethod.Post, Negotiate, json: "", contentType: "text/plain"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.Status);
        }

        using (AdminResponse socket = await h.GetAsync("/_blazor?id=x"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, socket.Status);
        }

        using (AdminResponse crossSite = await h.SendAsync(
            HttpMethod.Post, Negotiate, json: "", cookie: owner, contentType: "text/plain", headers: new Dictionary<string, string> { ["Sec-Fetch-Site"] = "cross-site" }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, crossSite.Status);
        }

        using AdminResponse signedIn = await h.SendAsync(HttpMethod.Post, Negotiate, json: "", cookie: owner, contentType: "text/plain");
        Assert.Equal(HttpStatusCode.OK, signedIn.Status);
        Assert.False(string.IsNullOrEmpty(signedIn.Json.GetProperty("connectionToken").GetString()));
        Assert.Equal("no-store", signedIn.Header("Cache-Control"));
    }

    [Fact]
    public async Task Pages_GetTheDashboardsPolicy_AndTheApiAndFilesKeepTheStrictOne()
    {
        await using AdminHarness h = await AdminHarness.StartAsync();
        await h.SetUpOwnerAsync();

        using (AdminResponse page = await GetPageAsync(h, DashboardRoutes.SignIn))
        {
            Assert.Equal(DashboardSecurity.ContentSecurityPolicy, page.Header("Content-Security-Policy"));
            Assert.Equal("no-store", page.Header("Cache-Control"));
            Assert.Equal("DENY", page.Header("X-Frame-Options"));
            Assert.Equal("nosniff", page.Header("X-Content-Type-Options"));
            Assert.Equal("no-referrer", page.Header("Referrer-Policy"));
            Assert.Contains("__Host-paduraksa-antiforgery=", string.Join('\n', page.SetCookies), StringComparison.Ordinal);
            Assert.Contains(page.SetCookies, line => line.StartsWith("__Host-paduraksa-antiforgery=", StringComparison.Ordinal)
                && line.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase) && line.Contains("secure", StringComparison.OrdinalIgnoreCase));
        }

        using (AdminResponse api = await h.GetAsync("/api/v1/setup"))
        {
            Assert.Equal("default-src 'none'; frame-ancestors 'none'", api.Header("Content-Security-Policy"));
        }

        using AdminResponse css = await h.GetAsync("/_content/Paduraksa.Dashboard/app.css");
        Assert.Equal("default-src 'none'; frame-ancestors 'none'", css.Header("Content-Security-Policy"));
        Assert.NotEqual("no-store", css.Header("Cache-Control"));
    }

    [Fact]
    public async Task StaticFiles_CostNoSessionLookup_WhileTheHubAndThePagesStillDo()
    {
        await using AdminHarness h = await AdminHarness.StartAsync();
        string owner = await h.SetUpOwnerAsync();
        long before = h.Host.Shared.Sessions.ValidationCount;

        // A page load pulls dozens of these, each with the browser's cookie on it.
        foreach (string asset in new[] { "/_framework/blazor.web.js", "/_content/MudBlazor/MudBlazor.min.css", "/_content/Paduraksa.Dashboard/app.css" })
        {
            using AdminResponse file = await h.GetAsync(asset, owner);
            Assert.Equal(HttpStatusCode.OK, file.Status);
        }

        // Not even a cookie that names no session is looked up for one.
        using (AdminResponse stale = await h.GetAsync("/_content/Paduraksa.Dashboard/app.css", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"))
        {
            Assert.Equal(HttpStatusCode.OK, stale.Status);
        }

        Assert.Equal(0L, h.Host.Shared.Sessions.ValidationCount - before);

        // Pages and the circuit hub are not static files: they still check the session, and still refuse or redirect without one.
        using (AdminResponse signedOut = await GetPageAsync(h, "/"))
        {
            Assert.Equal(HttpStatusCode.Found, signedOut.Status);
            Assert.Equal(DashboardRoutes.SignIn, signedOut.Headers.Location?.OriginalString);
        }

        using (AdminResponse anonymousHub = await h.GetAsync("/_blazor?id=x"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymousHub.Status);
        }

        Assert.Equal(0L, h.Host.Shared.Sessions.ValidationCount - before);

        using (AdminResponse page = await GetPageAsync(h, "/", owner))
        {
            Assert.Equal(HttpStatusCode.OK, page.Status);
        }

        Assert.Equal(1L, h.Host.Shared.Sessions.ValidationCount - before);

        using (AdminResponse hub = await h.SendAsync(HttpMethod.Post, "/_blazor/negotiate?negotiateVersion=1", json: "", cookie: owner, contentType: "text/plain"))
        {
            Assert.Equal(HttpStatusCode.OK, hub.Status);
        }

        Assert.Equal(2L, h.Host.Shared.Sessions.ValidationCount - before);
    }

    private static Task<AdminResponse> GetPageAsync(AdminHarness h, string path, string? session = null) =>
        h.SendAsync(HttpMethod.Get, path, cookie: session, headers: new Dictionary<string, string> { ["Accept"] = Html });

    /// <summary>A page's antiforgery token and the cookie it set, as a browser would hold them.</summary>
    private sealed record Antiforgery(string Token, string Cookie);

    private static async Task<Antiforgery> AntiforgeryAsync(AdminHarness h, string path)
    {
        using AdminResponse page = await GetPageAsync(h, path);
        Assert.Equal(HttpStatusCode.OK, page.Status);
        string token = TokenField().Match(page.Body).Groups[1].Value;
        string cookie = page.SetCookies.Select(line => line.Split(';', 2)[0]).Single(pair => pair.StartsWith("__Host-paduraksa-antiforgery=", StringComparison.Ordinal));
        Assert.NotEmpty(token);
        return new Antiforgery(WebUtility.HtmlDecode(token), cookie);
    }

    /// <summary>Posts a form as a same-origin browser page: the fields, the page's antiforgery token and cookie, and the session cookie.</summary>
    private static Task<AdminResponse> PostFormAsync(
        AdminHarness h, string path, Antiforgery antiforgery, IReadOnlyDictionary<string, string> fields, string? session = null, string fetchSite = "same-origin")
    {
        Dictionary<string, string> all = new(fields);
        if (antiforgery.Token.Length > 0)
        {
            all["__RequestVerificationToken"] = antiforgery.Token;
        }

        string body = string.Join('&', all.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        List<string> cookies = [];
        if (antiforgery.Cookie.Length > 0)
        {
            cookies.Add(antiforgery.Cookie);
        }

        if (session is not null)
        {
            cookies.Add($"__Host-paduraksa-session={session}");
        }

        Dictionary<string, string> headers = new() { ["Sec-Fetch-Site"] = fetchSite };
        if (cookies.Count > 0)
        {
            headers["Cookie"] = string.Join("; ", cookies);
        }

        return h.SendAsync(HttpMethod.Post, path, body, browser: false, headers: headers, contentType: FormType);
    }

    private static Dictionary<string, string> SignIn(string password, string? returnUrl)
    {
        Dictionary<string, string> fields = new() { [AdminWebForm.Username] = AdminHarness.OwnerName, [AdminWebForm.Password] = password };
        if (returnUrl is not null)
        {
            fields[AdminWebForm.ReturnUrl] = returnUrl;
        }

        return fields;
    }

    private static Dictionary<string, string> Setup(string token, string password, string confirm) => new()
    {
        [AdminWebForm.SetupToken] = token,
        [AdminWebForm.Username] = AdminHarness.OwnerName,
        [AdminWebForm.DisplayName] = "Ada Lovelace",
        [AdminWebForm.Password] = password,
        [AdminWebForm.ConfirmPassword] = confirm,
    };

    private static void AssertSeeOther(AdminResponse response, string location)
    {
        Assert.Equal(HttpStatusCode.SeeOther, response.Status);
        Assert.Equal(location, response.Headers.Location?.OriginalString);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]*)\"")]
    private static partial Regex TokenField();
}
