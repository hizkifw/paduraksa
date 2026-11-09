using System.Net;

using Paduraksa.Admin.Auth;
using Paduraksa.Server.Hosting;

namespace Paduraksa.Tests.Integration.Admin;

/// <summary>Until the first Owner exists the admin app serves only the setup flow, which needs the setup token.</summary>
public sealed class SetupGateTests
{
    [Fact]
    public async Task SetupEndToEnd_TheGateIsClosedUntilTheRightTokenCreatesTheOwner()
    {
        await using AdminHarness h = await AdminHarness.StartAsync();

        // Closed: health and the status endpoint answer, everything else is a 503 problem that points at the setup step.
        using (AdminResponse healthz = await h.GetAsync("/healthz"))
        {
            Assert.Equal(HttpStatusCode.OK, healthz.Status);
        }

        using (AdminResponse status = await h.GetAsync("/api/v1/setup"))
        {
            Assert.Equal(HttpStatusCode.OK, status.Status);
            Assert.False(status.Json.GetProperty("complete").GetBoolean());
            Assert.DoesNotContain(await h.SetupTokenAsync(), status.Body, StringComparison.Ordinal);
        }

        foreach (string path in new[] { "/", "/api/v1/users", "/api/v1/auth/me", "/api/v1/nothing-here" })
        {
            using AdminResponse gated = await h.GetAsync(path);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, gated.Status);
            Assert.Equal("setup.required", gated.Json.GetProperty("code").GetString());
            Assert.Contains("/api/v1/setup", gated.Body, StringComparison.Ordinal);
        }

        // A wrong token is refused (403) and the gate stays shut.
        using (AdminResponse wrong = await h.PostAsync("/api/v1/setup", Body.Setup("not-the-token", "owner", AdminHarness.Password)))
        {
            Assert.Equal(HttpStatusCode.Forbidden, wrong.Status);
            Assert.DoesNotContain("not-the-token", wrong.Body, StringComparison.Ordinal);
            Assert.Null(wrong.SessionCookie);
        }

        string token = await h.SetupTokenAsync();
        using (AdminResponse weak = await h.PostAsync("/api/v1/setup", Body.Setup(token, "owner", "short")))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, weak.Status);
        }

        // The right token creates the Owner and signs them in.
        string cookie;
        using (AdminResponse created = await h.PostAsync("/api/v1/setup", Body.Setup(token, "owner", AdminHarness.Password)))
        {
            Assert.Equal(HttpStatusCode.Created, created.Status);
            Assert.Equal("owner", created.Json.GetProperty("owner").GetProperty("username").GetString());
            Assert.Equal(["owner"], created.Json.GetProperty("owner").GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
            Assert.False(created.Json.GetProperty("session").GetProperty("secondFactorRequired").GetBoolean());
            Assert.DoesNotContain(AdminHarness.Password, created.Body, StringComparison.Ordinal);
            cookie = created.SessionCookie!;
        }

        // Open at once: the gate reads the state per request.
        using (AdminResponse me = await h.GetAsync("/api/v1/auth/me", cookie))
        {
            Assert.Equal(HttpStatusCode.OK, me.Status);
            Assert.Equal("session", me.Json.GetProperty("authKind").GetString());
            Assert.Contains("vault:manage", me.Json.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
        }

        // The dashboard is open too: an anonymous browser is sent to its sign-in page.
        using (AdminResponse page = await h.GetAsync("/"))
        {
            Assert.Equal(HttpStatusCode.Found, page.Status);
            Assert.Equal("/signin", page.Headers.Location?.OriginalString);
        }

        using (AdminResponse status = await h.GetAsync("/api/v1/setup"))
        {
            Assert.True(status.Json.GetProperty("complete").GetBoolean());
        }

        // Setup cannot be run twice.
        using (AdminResponse again = await h.PostAsync("/api/v1/setup", Body.Setup(token, "second", AdminHarness.Password)))
        {
            Assert.Equal(HttpStatusCode.Conflict, again.Status);
        }

        // Both attempts are audited, with the API channel and the client's address; neither entry holds the token or the password.
        IReadOnlyList<AuditRecord> audit = await h.AuditAsync("setup.complete");
        Assert.Equal(["denied", "success"], audit.Select(a => a.Outcome));
        Assert.All(audit, a =>
        {
            Assert.Equal("api", a.Channel);
            Assert.Equal("127.0.0.1", a.ClientIp);
            Assert.DoesNotContain(token, a.Details ?? "", StringComparison.Ordinal);
            Assert.DoesNotContain(AdminHarness.Password, a.Details ?? "", StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task TheDocumentedPrefixesStayOpenDuringSetup()
    {
        await using AdminHarness h = await AdminHarness.StartAsync();
        AdminPipelineHooks hooks = h.Host.GetServices(HostRole.Admin).GetService(typeof(AdminPipelineHooks)) as AdminPipelineHooks
            ?? throw new InvalidOperationException("No hooks registered.");
        hooks.SetupAllowedPathPrefixes.Add("/extra-docs");

        // Open means anything but the gate's 503: a 404 where nothing is mapped, the real page (or its redirect) where the API's docs are.
        foreach (string path in new[] { "/openapi/v1.json", "/api/v1/openapi.json", "/api/docs", "/api/docs/index.html", "/extra-docs/x" })
        {
            using AdminResponse response = await h.GetAsync(path);
            Assert.NotEqual(HttpStatusCode.ServiceUnavailable, response.Status);
        }

        using AdminResponse near = await h.GetAsync("/api/docsX");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, near.Status);
    }
}
