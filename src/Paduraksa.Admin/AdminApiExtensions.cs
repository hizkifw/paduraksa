using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using Paduraksa.Admin.Api;
using Paduraksa.Admin.Json;
using Paduraksa.Admin.OpenApi;

namespace Paduraksa.Admin;

/// <summary>Registers and maps the resource REST API (<c>/api/v1</c>), its API description and reference UI.</summary>
public static class AdminApiExtensions
{
    /// <summary>
    /// Registers what the API needs: the typed-ID route constraints, the JSON contract (inserted first in the host's resolver chain, so
    /// it composes with other contexts) and the OpenAPI generator. Add <see cref="UsePaduraksaAdminApi"/> as the first middleware. The services the endpoints call (config, certificates, issuance, simulator,
    /// history, vault secrets) come from the host.
    /// </summary>
    public static IServiceCollection AddPaduraksaAdminApi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.Configure<RouteOptions>(TypedIdRouting.Register);
        services.ConfigureHttpJsonOptions(options => AdminJsonContext.AddTo(options.SerializerOptions));
        AdminOpenApi.AddServices(services);
        return services;
    }

    /// <summary>
    /// Adds the API's problem-details and exception middleware. Call it <b>first</b>, before <c>UsePaduraksaAdminAuth</c> and anything else, so an exception from
    /// any middleware or endpoint behind it is logged with a trace ID and answered as a 500 problem that leaks nothing, an error answer without a body gets one,
    /// and the reference page gets its own CSP. Where it sits is the call order of the pipeline, not a registration order.
    /// </summary>
    public static IApplicationBuilder UsePaduraksaAdminApi(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<ApiPipelineMiddleware>();
    }

    /// <summary>
    /// Maps <c>/api/v1</c>: one group per resource, each endpoint with <c>RequirePermission</c> (or, for the API description and the
    /// reference UI, <see cref="Auth.AdminAnonymousMetadata"/>), the OpenAPI document at <c>/api/v1/openapi.json</c> and the reference UI at
    /// <c>/api/docs</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapPaduraksaAdminApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        RouteGroupBuilder api = endpoints.MapGroup("/api/v1");
        AdminOpenApi.Map(endpoints);
        return endpoints;
    }
}
