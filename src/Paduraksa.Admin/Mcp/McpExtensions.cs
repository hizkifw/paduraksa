using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol.Protocol;

using Paduraksa.Admin.Auth;

namespace Paduraksa.Admin.Mcp;

/// <summary>The MCP endpoint (<c>/mcp</c>, streamable HTTP, PLAN §11.2): two tools, <c>search</c> and <c>execute</c>, that run the model's JavaScript in a sandbox.</summary>
public static class McpExtensions
{
    public const string Path = "/mcp";

    /// <summary>
    /// A request carries one JSON-RPC message holding the code, at most <see cref="Paduraksa.Mcp.SandboxLimits.MaxCodeChars"/> characters of it; the
    /// message also has to fit its envelope and JSON escaping.
    /// </summary>
    private const int MaxMessageBytes = 192 * 1024;

    /// <summary>Registers the MCP server and what dispatches a script's requests. Stateless: every request is authenticated and answered on its own.</summary>
    public static IServiceCollection AddPaduraksaAdminMcp(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHttpContextAccessor();
        services.AddSingleton<McpPipeline>();
        services.AddSingleton<McpToolkit>();
        services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = "paduraksa", Version = "1" };
                options.ServerInstructions = "Two tools: search finds endpoints in the API description, execute calls the API. Writes need confirm: true after a dry run.";
                options.Handlers.ListToolsHandler = static (context, _) => ValueTask.FromResult(McpToolkit.List());
                options.Handlers.CallToolHandler = static async (context, cancellationToken) =>
                    await context.Services!.GetRequiredService<McpToolkit>().CallAsync(context.Params!, cancellationToken).ConfigureAwait(false);
            })
            .WithHttpTransport(options => options.Stateless = true);
        return services;
    }

    /// <summary>Maps <c>/mcp</c>. Authentication is the admin pipeline's: a bearer token, checked on every request.</summary>
    public static IEndpointRouteBuilder MapPaduraksaAdminMcp(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapMcp(Path).WithAdminBodyLimit(MaxMessageBytes);
        return endpoints;
    }
}
