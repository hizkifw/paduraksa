using System.Net;

using Paduraksa.Admin.Json;
using Paduraksa.Contracts.Api;
using Paduraksa.Contracts.Certificates;
using Paduraksa.Core.Model;

namespace Paduraksa.Tests.Integration.Admin;

/// <summary>
/// The CSRF defence on the resource API for a request that authenticates with the session cookie, whatever its method or body: the multipart upload
/// and the bodyless renew included. (A bearer token is exempt: a browser never adds an Authorization header by itself.)
/// </summary>
public sealed class ApiCsrfTests
{
    private const string Evil = "https://evil.example";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ACrossSiteMultipartUploadWithTheSessionCookieIsRefusedAndASameOriginOneIsNot()
    {
        await using AdminApiHarness api = await AdminApiHarness.StartAsync(Ct);
        string cookie = await api.SignInAsync("owner");
        byte[] pem = ResourceApiTests.MakeCertificatePem("csrf.test").Pem;

        foreach ((string? site, string? origin) in new (string?, string?)[] { ("cross-site", null), ("same-site", null), (null, Evil), (null, null), ("cross-site", api.BaseAddress.GetLeftPart(UriPartial.Authority)) })
        {
            using HttpResponseMessage refused = await api.SendWithCookieAsync(HttpMethod.Post, "/api/v1/certificates", cookie, site, origin, ResourceApiTests.Upload(pem));
            ApiProblem problem = await AdminApiHarness.ProblemAsync(refused, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal("csrf", problem.Code);
        }

        using HttpResponseMessage inventory = await api.GetAsync("/api/v1/certificates");
        Assert.Empty((await NodeOf(inventory)).AsArray());

        using HttpResponseMessage sameOrigin = await api.SendWithCookieAsync(HttpMethod.Post, "/api/v1/certificates", cookie, "same-origin", null, ResourceApiTests.Upload(pem));
        Assert.Equal(HttpStatusCode.Created, sameOrigin.StatusCode);
        using HttpResponseMessage byOrigin = await api.SendWithCookieAsync(HttpMethod.Post, "/api/v1/certificates", cookie, null, api.BaseAddress.GetLeftPart(UriPartial.Authority), ResourceApiTests.Upload(ResourceApiTests.MakeCertificatePem("csrf2.test").Pem));
        Assert.Equal(HttpStatusCode.Created, byOrigin.StatusCode);
    }

    [Fact]
    public async Task ABodylessWriteWithTheSessionCookieNeedsASameOriginSignal()
    {
        await using AdminApiHarness api = await AdminApiHarness.StartAsync(Ct);
        string cookie = await api.SignInAsync("owner");
        CertificateId id = CertificateId.New();

        foreach ((HttpMethod method, string path) in new[]
        {
            (HttpMethod.Post, $"/api/v1/certificates/{id}/renew"),
            (HttpMethod.Post, $"/api/v1/certificates/{id}/reissue"),
            (HttpMethod.Delete, $"/api/v1/certificates/{id}"),
            (HttpMethod.Post, "/api/v1/certificates/local-ca"),
        })
        {
            foreach ((string? site, string? origin) in new (string?, string?)[] { (null, null), ("cross-site", null), (null, Evil) })
            {
                using HttpResponseMessage refused = await api.SendWithCookieAsync(method, path, cookie, site, origin);
                Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
                Assert.Equal("csrf", (await AdminApiHarness.ProblemAsync(refused, Ct)).Code);
            }
        }

        // Same origin passes the CSRF check and reaches the endpoint (which doesn't know this certificate).
        using HttpResponseMessage renew = await api.SendWithCookieAsync(HttpMethod.Post, $"/api/v1/certificates/{id}/renew", cookie, "same-origin");
        Assert.Equal(HttpStatusCode.NotFound, renew.StatusCode);
        using HttpResponseMessage delete = await api.SendWithCookieAsync(HttpMethod.Delete, $"/api/v1/certificates/{id}", cookie, null, api.BaseAddress.GetLeftPart(UriPartial.Authority));
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
    }

    [Fact]
    public async Task ASessionCookieStillReadsAndABearerTokenNeedsNoOrigin()
    {
        await using AdminApiHarness api = await AdminApiHarness.StartAsync(Ct);
        string cookie = await api.SignInAsync("viewer");

        using HttpResponseMessage read = await api.SendWithCookieAsync(HttpMethod.Get, "/api/v1/config", cookie);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using HttpResponseMessage forbiddenForViewer = await api.SendWithCookieAsync(HttpMethod.Post, "/api/v1/certificates/local-ca", cookie, "same-origin");
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenForViewer.StatusCode);
        Assert.NotEqual("csrf", (await AdminApiHarness.ProblemAsync(forbiddenForViewer, Ct)).Code);
        using HttpResponseMessage bearer = await api.SendAsync(HttpMethod.Post, "/api/v1/certificates/local-ca");
        Assert.Equal(HttpStatusCode.Created, bearer.StatusCode);
    }

    private static async Task<System.Text.Json.Nodes.JsonNode> NodeOf(HttpResponseMessage response) =>
        System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
}
