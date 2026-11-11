using System.Net;

using Paduraksa.Admin.Json;
using Paduraksa.Application.ErrorPages;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Pages;
using Paduraksa.Tests.Integration.Hosting;

namespace Paduraksa.Tests.Integration.Admin;

public sealed class ErrorPageApiTests(SharedAdminApi shared) : IClassFixture<SharedAdminApi>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task APageIsRenderedWithSampleValuesAndItsPolicy()
    {
        ErrorPagePreviewRequest request = new(new ErrorPageContent { Heading = "{status} {statusText}", Text = "{host} {requestId} {cfRay}" }, 404);

        using var response = await shared.Api.SendJsonAsync(HttpMethod.Post, "/api/v1/error-pages/preview", request, AdminJsonContext.Default.ErrorPagePreviewRequest);
        var preview = await AdminApiHarness.ReadAsync(response, AdminJsonContext.Default.ErrorPagePreview, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<h1>404 Not Found</h1>", preview.Html, StringComparison.Ordinal);
        Assert.Contains($"<p>{ErrorPagePreviews.SampleHost} {ErrorPagePreviews.SampleRequestId} {ErrorPagePreviews.SampleRay}</p>", preview.Html, StringComparison.Ordinal);
        Assert.StartsWith("default-src 'none'; style-src 'sha256-", preview.ContentSecurityPolicy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APageThatCouldNotBeSavedIsRefusedWithTheValidatorsErrorsAsPointersIntoTheRequest()
    {
        ErrorPagePreviewRequest request = new(new ErrorPageContent { Heading = "{nope}", Text = new string('a', 600) }, 404);

        using var response = await shared.Api.SendJsonAsync(HttpMethod.Post, "/api/v1/error-pages/preview", request, AdminJsonContext.Default.ErrorPagePreviewRequest);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("\"/page/heading\"", body, StringComparison.Ordinal);
        Assert.Contains("errorPages.variable.unknown", body, StringComparison.Ordinal);
        Assert.Contains("\"/page/text\"", body, StringComparison.Ordinal);
        Assert.Contains("errorPages.text.tooLong", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACallerWithoutCredentialsIsUnauthorized()
    {
        ErrorPagePreviewRequest request = new(new ErrorPageContent { Heading = "x" }, 404);

        using var response = await shared.Api.SendJsonAsync(HttpMethod.Post, "/api/v1/error-pages/preview", request, AdminJsonContext.Default.ErrorPagePreviewRequest, caller: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
