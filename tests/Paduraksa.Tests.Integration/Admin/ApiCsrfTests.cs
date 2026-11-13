using System.Net;
using Paduraksa.Contracts.Api;
using Paduraksa.Core.Model;
using Paduraksa.Tests.Integration.Hosting;

namespace Paduraksa.Tests.Integration.Admin;

/// <summary>
/// The CSRF defence on the resource API for a request that authenticates with the session cookie, whatever its method or body: the multipart upload
/// and the bodyless renew included. (A bearer token is exempt: a browser never adds an Authorization header by itself.)
/// </summary>
public sealed class ApiCsrfTests(SharedAdminApi shared) : IClassFixture<SharedAdminApi>
{
    private const string Evil = "https://evil.example";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ACrossSiteMultipartUploadWithTheSessionCookieIsRefusedAndASameOriginOneIsNot()
    {
        var api = shared.Api;
        var cookie = await api.SignInAsync("owner");
        var pem = ResourceApiTests.MakeCertificatePem("csrf.test").Pem;

        foreach (var (site, origin) in new (string?, string?)[] { ("cross-site", null), ("same-site", null), (null, Evil), (null, null), ("cross-site", api.BaseAddress.GetLeftPart(UriPartial.Authority)) })
        {
            using var refused = await api.SendWithCookieAsync(HttpMethod.Post, "/api/v1/certificates", cookie, site, origin, ResourceApiTests.Upload(pem));
            var problem = await AdminApiHarness.ProblemAsync(refused, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal("csrf.refused", problem.Code);
        }

        // Nothing was uploaded: the inventory holds only what the process itself made at startup (the local CA root and the
        // admin listener's certificate).
        using var inventory = await api.GetAsync("/api/v1/certificates?source=uploaded");
        Assert.Empty((await NodeOf(inventory))["items"]!.AsArray());

        using var sameOrigin = await api.SendWithCookieAsync(HttpMethod.Post, "/api/v1/certificates", cookie, "same-origin", null, ResourceApiTests.Upload(pem));
        Assert.Equal(HttpStatusCode.Created, sameOrigin.StatusCode);
        using var byOrigin = await api.SendWithCookieAsync(HttpMethod.Post, "/api/v1/certificates", cookie, null, api.BaseAddress.GetLeftPart(UriPartial.Authority), ResourceApiTests.Upload(ResourceApiTests.MakeCertificatePem("csrf2.test").Pem));
        Assert.Equal(HttpStatusCode.Created, byOrigin.StatusCode);
    }

    [Fact]
    public async Task ABodylessWriteWithTheSessionCookieNeedsASameOriginSignal()
    {
        var api = shared.Api;
        var cookie = await api.SignInAsync("owner");
        var id = CertificateId.New();

        foreach (var (method, path) in new[]
        {
            (HttpMethod.Post, $"/api/v1/certificates/{id}/renew"),
            (HttpMethod.Post, $"/api/v1/certificates/{id}/reissue"),
            (HttpMethod.Delete, $"/api/v1/certificates/{id}"),
            (HttpMethod.Post, "/api/v1/certificates/local-ca"),
        })
        {
            foreach (var (site, origin) in new (string?, string?)[] { (null, null), ("cross-site", null), (null, Evil) })
            {
                using var refused = await api.SendWithCookieAsync(method, path, cookie, site, origin);
                Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
                Assert.Equal("csrf.refused", (await AdminApiHarness.ProblemAsync(refused, Ct)).Code);
            }
        }

        // Same origin passes the CSRF check and reaches the endpoint (which doesn't know this certificate).
        using var renew = await api.SendWithCookieAsync(HttpMethod.Post, $"/api/v1/certificates/{id}/renew", cookie, "same-origin");
        Assert.Equal(HttpStatusCode.NotFound, renew.StatusCode);
        using var delete = await api.SendWithCookieAsync(HttpMethod.Delete, $"/api/v1/certificates/{id}", cookie, null, api.BaseAddress.GetLeftPart(UriPartial.Authority));
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
    }

    [Fact]
    public async Task ASessionCookieStillReadsAndABearerTokenNeedsNoOrigin()
    {
        var api = shared.Api;
        var cookie = await api.SignInAsync("viewer");

        using var read = await api.SendWithCookieAsync(HttpMethod.Get, "/api/v1/config", cookie);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var forbiddenForViewer = await api.SendWithCookieAsync(HttpMethod.Post, "/api/v1/certificates/self-signed", cookie, "same-origin", null, AdminApiHarness.JsonBody("""{"names":["viewer.test"]}"""));
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenForViewer.StatusCode);
        Assert.NotEqual("csrf.refused", (await AdminApiHarness.ProblemAsync(forbiddenForViewer, Ct)).Code);
        // Creating the local CA is the process's own work at startup now (the admin certificate is issued from it), so the
        // bearer token proves its exemption on a certificate operation that is still the caller's.
        using var bearer = await api.SendAsync(HttpMethod.Post, "/api/v1/certificates/self-signed", content: AdminApiHarness.JsonBody("""{"names":["bearer.test"]}"""));
        Assert.Equal(HttpStatusCode.Created, bearer.StatusCode);
    }

    private static async Task<System.Text.Json.Nodes.JsonNode> NodeOf(HttpResponseMessage response) =>
        System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
}
