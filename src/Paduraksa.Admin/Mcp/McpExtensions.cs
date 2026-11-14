using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol.Protocol;

using Paduraksa.Admin.Auth;

namespace Paduraksa.Admin.Mcp;

/// <summary>How the admin app runs the MCP sandbox (<c>Paduraksa:Mcp</c>): the child process to start and how long an idle one lives.</summary>
public sealed record AdminMcpOptions(Paduraksa.Mcp.SandboxChildCommand ChildCommand, TimeSpan IdleTimeout);

/// <summary>The MCP endpoint (<c>/mcp</c>, streamable HTTP, docs/design/admin-surfaces.md "MCP in code mode (M10)"): two tools, <c>search</c> and <c>execute</c>, that run the model's JavaScript in a sandbox.</summary>
public static class McpExtensions
{
    public const string Path = "/mcp";

    /// <summary>
    /// A request carries one JSON-RPC message holding the code, at most <see cref="Paduraksa.Mcp.SandboxLimits.MaxCodeChars"/> characters of it; the
    /// message also has to fit its envelope and JSON escaping.
    /// </summary>
    private const int MaxMessageBytes = 192 * 1024;

    /// <summary>Registers the MCP server and what dispatches a script's requests. Stateless: every request is authenticated and answered on its own.</summary>
    public static IServiceCollection AddPaduraksaAdminMcp(this IServiceCollection services, AdminMcpOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton(options);
        services.AddHttpContextAccessor();
        services.AddSingleton<McpPipeline>();
        services.AddSingleton(static sp =>
        {
            var toolkit = ActivatorUtilities.CreateInstance<McpToolkit>(sp);
            sp.GetService<McpSwitch>()?.Attach(toolkit.Detach);
            return toolkit;
        });
        services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = "paduraksa", Version = "1" };
                options.ServerInstructions = Paduraksa.Mcp.ToolDescriptions.ServerInstructions;
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
