using System.Net;
using Paduraksa.Tests.Integration.Hosting;

namespace Paduraksa.Tests.Integration.Admin;

/// <summary>
/// The one body type that is not JSON on the resource API: the certificate upload names multipart as its content type, so a same-origin request with the
/// session cookie gets through the CSRF check's body rule. Every refusal is <see cref="CsrfTests"/>'.
/// </summary>
public sealed class ApiCsrfTests(SharedAdminApi shared) : IClassFixture<SharedAdminApi>
{
    [Fact]
    public async Task ASameOriginMultipartUploadWithTheSessionCookieIsAcceptedThoughABodyMustOtherwiseBeJson()
    {
        var api = shared.Api;
        var cookie = await api.SignInAsync("owner");

        using var sameOrigin = await api.SendWithCookieAsync(HttpMethod.Post, "/api/v1/certificates", cookie, "same-origin", null, ResourceApiTests.Upload(ResourceApiTests.MakeCertificatePem("csrf.test").Pem));
        Assert.Equal(HttpStatusCode.Created, sameOrigin.StatusCode);
        using var byOrigin = await api.SendWithCookieAsync(HttpMethod.Post, "/api/v1/certificates", cookie, null, api.BaseAddress.GetLeftPart(UriPartial.Authority), ResourceApiTests.Upload(ResourceApiTests.MakeCertificatePem("csrf2.test").Pem));
        Assert.Equal(HttpStatusCode.Created, byOrigin.StatusCode);
    }
}
