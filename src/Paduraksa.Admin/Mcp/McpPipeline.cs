using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Paduraksa.Admin.Mcp;

/// <summary>
/// The admin app's own request pipeline, captured as it is built, so that a request a script makes goes through exactly what an HTTP request to the
/// API goes through (the setup gate, routing, body limit, CSRF check, the endpoint's permission filter, validation and problem details) with no
/// loopback socket and no second copy of any of it (docs/design/admin-surfaces.md "MCP in code mode (M10)").
/// </summary>
internal sealed class McpPipeline
{
    private RequestDelegate? _pipeline;

    /// <summary>The pipeline behind the capture, or <c>null</c> before the app has started.</summary>
    public RequestDelegate? Pipeline => Volatile.Read(ref _pipeline);

    /// <summary>
    /// Adds the capture as a middleware that passes everything on. It must be the first component of the app, ahead of the API's exception middleware,
    /// so a script's request gets that middleware's problem bodies like any other request.
    /// </summary>
    public static void Capture(IApplicationBuilder app)
    {
        var holder = app.ApplicationServices.GetService(typeof(McpPipeline)) as McpPipeline;
        app.Use(next =>
        {
            if (holder is not null)
            {
                Volatile.Write(ref holder._pipeline, next);
            }

            return next;
        });
    }
}
