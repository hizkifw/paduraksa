using Paduraksa.Application.ErrorPages;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Pages;

namespace Paduraksa.Tests.Unit.Config;

public sealed class ErrorPagePreviewerTests
{
    [Fact]
    public void AMessageIsRenderedInTheBuiltInLayoutWithTheSampleValues()
    {
        var (preview, errors) = ErrorPagePreviewer.Render(new ErrorPagePreviewRequest(new ErrorPageContent { Heading = "{status} {statusText}", Text = "Seen at {host}" }, 429));

        Assert.Empty(errors);
        Assert.NotNull(preview);
        Assert.Contains("<title>429 Too Many Requests</title>", preview.Html, StringComparison.Ordinal);
        Assert.Contains("<p>Seen at app.example.com</p>", preview.Html, StringComparison.Ordinal);
        Assert.Contains($"Request ID: <code>{preview.SampleRequest}</code>", preview.Html, StringComparison.Ordinal);
        Assert.StartsWith("default-src 'none'; style-src 'sha256-", preview.ContentSecurityPolicy, StringComparison.Ordinal);
    }

    [Fact]
    public void ADocumentIsRenderedAsWrittenWithItsVariablesFilledInAndTheDocumentPolicy()
    {
        var (preview, errors) = ErrorPagePreviewer.Render(new ErrorPagePreviewRequest(
            new ErrorPageContent { Mode = ErrorPageMode.Html, Html = "<style>p{color:red}</style><p>{status} {requestId} {cfRay}</p>" }, 503, Maintenance: true));

        Assert.Empty(errors);
        Assert.Equal($"<style>p{{color:red}}</style><p>503 {ErrorPagePreviews.SampleRequestId} {ErrorPagePreviews.SampleRay}</p>", preview!.Html);
        Assert.Contains("style-src 'unsafe-inline'", preview.ContentSecurityPolicy, StringComparison.Ordinal);
        Assert.DoesNotContain("script-src", preview.ContentSecurityPolicy, StringComparison.Ordinal);
    }

    [Fact]
    public void AMaintenancePageMayCarryAnyOfTheMaintenanceStatuses()
    {
        var (preview, errors) = ErrorPagePreviewer.Render(new ErrorPagePreviewRequest(new ErrorPageContent { Heading = "{status} {statusText}" }, 200, Maintenance: true));
        Assert.Empty(errors);
        Assert.Contains("<h1>200 OK</h1>", preview!.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void APageTheValidatorRefusesIsRefusedWithItsErrorsAsPointersIntoTheRequest()
    {
        var (preview, errors) = ErrorPagePreviewer.Render(new ErrorPagePreviewRequest(new ErrorPageContent { Mode = ErrorPageMode.Html, Heading = "x" }, 404));

        Assert.Null(preview);
        Assert.Equal(
            [("/page/heading", "errorPages.heading.unexpected"), ("/page/html", "errorPages.html.required")],
            errors.Select(e => (e.Path, e.Code)).Order());
    }

    [Theory]
    [InlineData(99)]
    [InlineData(600)]
    [InlineData(418)]
    public void AStatusWithoutAPageIsRefused(int status)
    {
        var (preview, errors) = ErrorPagePreviewer.Render(new ErrorPagePreviewRequest(new ErrorPageContent { Heading = "x" }, status));

        Assert.Null(preview);
        Assert.Equal("/status", Assert.Single(errors).Path);
    }
}
