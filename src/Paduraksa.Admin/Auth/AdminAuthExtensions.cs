using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Paduraksa.Contracts.Identity;

namespace Paduraksa.Admin.Auth;

/// <summary>Endpoint metadata: the endpoint needs a signed-in caller but no particular permission (the services check those). For the API description and tests.</summary>
public sealed class AdminAuthenticatedMetadata
{
    public static AdminAuthenticatedMetadata Instance { get; } = new();

    private AdminAuthenticatedMetadata()
    {
    }
}

/// <summary>Wires the admin app's security pipeline: hardening, the setup gate, authentication and CSRF defence.</summary>
public static class AdminAuthExtensions
{
    /// <summary>
    /// Registers what <see cref="UsePaduraksaAdminAuth"/> and <c>MapPaduraksaIdentityApi</c> need: the options, the pipeline
    /// hooks, the sign-in throttle, and the identity DTOs' JSON contract (first in the resolver chain, with its converters, so typed IDs and
    /// role and permission names apply whatever other contexts are added). Needs the identity services
    /// (<c>AddPaduraksaIdentity</c> or the server's shared registrations).
    /// </summary>
    public static IServiceCollection AddPaduraksaAdminAuth(this IServiceCollection services, AdminSecurityOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        options ??= new AdminSecurityOptions();

        services.TryAddSingleton(options);
        services.TryAddSingleton<AdminPipelineHooks>();
        services.TryAddSingleton(static sp => new AdminThrottle(sp.GetRequiredService<AdminSecurityOptions>()));
        services.ConfigureHttpJsonOptions(static json => IdentityJsonContext.AddTo(json.SerializerOptions));
        return services;
    }

    /// <summary>
    /// Adds the admin middleware, in this order: the loopback listener's host check, security headers, the first-run setup gate, routing, the request body limit, the CSRF
    /// check, authentication. Call it first, before any endpoint is mapped (it adds <c>UseRouting</c>, so the middleware can read each
    /// endpoint's metadata; nothing else should). Endpoints are served after the last middleware.
    /// </summary>
    public static WebApplication UsePaduraksaAdminAuth(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseMiddleware<AdminLoopbackHostMiddleware>();
        app.UseMiddleware<AdminSecurityHeadersMiddleware>();
        app.UseMiddleware<AdminSetupGateMiddleware>();
        app.UseRouting();
        app.UseMiddleware<AdminRequestLimitMiddleware>();
        app.UseMiddleware<AdminCsrfMiddleware>();
        app.UseMiddleware<AdminAuthenticationMiddleware>();
        return app;
    }

    /// <summary>Requires that the request is authenticated, whatever permission it holds (the services check permissions): anonymous is a 401 problem.</summary>
    public static TBuilder RequireAuthenticated<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.WithMetadata(AdminAuthenticatedMetadata.Instance);
        builder.AddEndpointFilter(static (invocation, next) => invocation.HttpContext.GetCaller() is null
            ? ValueTask.FromResult<object?>(AdminProblems.Unauthorized(invocation.HttpContext, "Sign in, or send a bearer token.", "authentication_required"))
            : next(invocation));
        return builder;
    }
}
