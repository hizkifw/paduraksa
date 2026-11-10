using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using MudBlazor;
using MudBlazor.Services;

using Paduraksa.Dashboard.Audit;
using Paduraksa.Dashboard.Auth;
using Paduraksa.Dashboard.Config;
using Paduraksa.Dashboard.Identity;
using Paduraksa.Dashboard.Theme;

namespace Paduraksa.Dashboard;

/// <summary>Registers and maps the dashboard: a Blazor Web App in the interactive server render mode, with MudBlazor.</summary>
public static class DashboardExtensions
{
    /// <summary>
    /// Registers the Razor components with interactive server rendering, MudBlazor (snackbars bottom-right, newest on top, one of each),
    /// the cascading authentication state built from the admin app's session (<see cref="DashboardAuthenticationStateProvider"/>), the
    /// circuit handler that ends a circuit whose session ended, the per-circuit theme, and what the config pages use per circuit: the
    /// Application services behind <see cref="IDashboardConfig"/>, the Undo toast, the change under review and the drawer stack; what the settings, account and audit
    /// pages use (<see cref="IDashboardIdentity"/> with its change feed, <see cref="IDashboardAudit"/>). The Application services, the admin app's
    /// authentication and Data Protection come from the host, and so does the bound on password checks (<see cref="IPasswordCheckSlots"/>; unbounded
    /// when the host gives none).
    /// </summary>
    public static IServiceCollection AddPaduraksaDashboard(this IServiceCollection services, DashboardOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddRazorComponents().AddInteractiveServerComponents(circuits => circuits.DetailedErrors = false);
        services.AddMudServices(mud =>
        {
            mud.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomRight;
            mud.SnackbarConfiguration.NewestOnTop = true;
            mud.SnackbarConfiguration.PreventDuplicates = true;
            mud.SnackbarConfiguration.ShowCloseIcon = true;
            mud.SnackbarConfiguration.VisibleStateDuration = 5000;
            // MudBlazor's default fades take half a second each way; a dismissed toast should be gone at once.
            mud.SnackbarConfiguration.ShowTransitionDuration = 200;
            mud.SnackbarConfiguration.HideTransitionDuration = 150;
            mud.SnackbarConfiguration.SnackbarVariant = Variant.Filled;
        });
        services.AddCascadingAuthenticationState();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(options ?? new DashboardOptions());
        services.TryAddScoped<IDashboardSessions, DashboardSessions>();
        services.AddScoped<DashboardAuthenticationStateProvider>();
        services.Replace(ServiceDescriptor.Scoped<AuthenticationStateProvider>(static sp => sp.GetRequiredService<DashboardAuthenticationStateProvider>()));
        services.TryAddEnumerable(ServiceDescriptor.Scoped<CircuitHandler, SessionCircuitHandler>());
        services.AddScoped<DashboardCaller>();
        services.AddScoped<ThemeState>();
        services.AddScoped<DashboardBrowser>();
        services.TryAddScoped<IDashboardConfig, DashboardConfig>();
        services.AddScoped<ConfigUndo>();
        services.AddScoped<ReviewedDraft>();
        services.AddScoped<Drawers.DrawerStack>();
        services.AddScoped<Providers.ProviderActions>();
        services.TryAddSingleton<Application.Probes.UpstreamProbe>();
        services.TryAddSingleton<IPasswordCheckSlots, UnboundedPasswordChecks>();
        services.TryAddSingleton<IdentityChangeFeed>();
        services.TryAddScoped<IDashboardIdentity, DashboardIdentity>();
        services.TryAddScoped<IDashboardAudit, DashboardAudit>();
        return services;
    }

    /// <summary>
    /// Maps the dashboard's pages and its circuit hub, and a fallback that answers an unknown page address with the styled 404. Blazor's own <c>frame-ancestors</c> header is turned off: the admin app's
    /// hardening sends the dashboard's whole CSP (<see cref="DashboardSecurity.ContentSecurityPolicy"/>), which forbids framing.
    /// </summary>
    public static RazorComponentsEndpointConventionBuilder MapPaduraksaDashboard(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var components = endpoints.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode(server => server.ContentSecurityFrameAncestorsPolicy = null);

        // Last in line (like MapFallback), and only a candidate for a browser's page request: everything else keeps the answer routing
        // gives it (an API's 405 or 415, the hub's and the static files' own 404).
        endpoints.Map(RoutePatternFactory.Parse("{*path}", defaults: null, parameterPolicies: new { path = new PageRequestConstraint() }), NotFoundAsync)
            .WithOrder(int.MaxValue)
            .WithDisplayName("Dashboard not found")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get, HttpMethods.Head]));
        return components;
    }

    /// <summary>
    /// An address nothing else answers, asked for by a browser: the dashboard's styled 404 (the router's not-found page in the app's
    /// shell) for a signed-in user; anyone else is sent to sign in, as for every page.
    /// </summary>
    private static Task NotFoundAsync(HttpContext context) =>
        context.User.Identity?.IsAuthenticated == true
            ? new RazorComponentResult<App> { StatusCode = StatusCodes.Status404NotFound }.ExecuteAsync(context)
            : context.ChallengeAsync();

    /// <summary>A browser asking for a page: GET or HEAD, accepting HTML, outside the API, the circuit hub and the static files.</summary>
    private sealed class PageRequestConstraint : IRouteConstraint
    {
        public bool Match(HttpContext? httpContext, IRouter? route, string routeKey, RouteValueDictionary values, RouteDirection routeDirection)
        {
            if (httpContext is null || routeDirection != RouteDirection.IncomingRequest)
            {
                return false;
            }

            var path = httpContext.Request.Path;
            return (HttpMethods.IsGet(httpContext.Request.Method) || HttpMethods.IsHead(httpContext.Request.Method))
                && !path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
                && !path.StartsWithSegments(DashboardRoutes.CircuitHub, StringComparison.OrdinalIgnoreCase)
                && !DashboardRoutes.StaticAssetPrefixes.Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
                && httpContext.Request.Headers.Accept.Any(accept => accept?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true);
        }
    }
}
