using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Paduraksa.Admin.Auth;
using Paduraksa.Admin.Json;
using Paduraksa.Application.ErrorPages;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

namespace Paduraksa.Admin.Api;

/// <summary>Previews an error page the way the proxy serves it. Stateless: it reads and changes nothing.</summary>
internal static class ErrorPageEndpoints
{
    public static void Map(RouteGroupBuilder api) =>
        api.MapPost("/error-pages/preview", (Delegate)PreviewAsync).WithName("PreviewErrorPage").WithTags("Error pages").WithSummary("Render an error page with sample values")
            .WithDescription("The page is judged by the rules that saving it is (error paths point into the request: /page/heading, /page/text, /page/html, /status), then rendered by the proxy's own writer with a sample request ID, host and CF-Ray; "
                + "html is the document it would serve, to be shown in a sandboxed frame without scripts, and contentSecurityPolicy the policy it is served with. Nothing is stored.")
            .Accepts<ErrorPagePreviewRequest>("application/json").Produces<ErrorPagePreview>().ProducesApiProblems(400, 415, 422)
            .WithAdminBodyLimit(ApiEndpointConventions.ErrorPagePreviewBodyLimit)
            .RequirePermission(Permission.ConfigRead);

    private static async Task<IResult> PreviewAsync(HttpContext http)
    {
        var body = await ApiBody.ReadJsonAsync(http, AdminJsonContext.Default.ErrorPagePreviewRequest).ConfigureAwait(false);
        if (!body.Ok)
        {
            return body.Problem!;
        }

        var (preview, errors) = ErrorPagePreviewer.Render(body.Value!);
        return preview is null
            ? ApiProblems.Problem(422, "The page can't be served as written; see errors.", null, errors, [], [])
            : EndpointSupport.Json(preview, AdminJsonContext.Default.ErrorPagePreview);
    }
}
