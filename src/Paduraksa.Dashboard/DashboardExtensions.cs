using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using MudBlazor;
using MudBlazor.Services;

using Paduraksa.Dashboard.Auth;
using Paduraksa.Dashboard.Theme;

namespace Paduraksa.Dashboard;

/// <summary>Registers and maps the dashboard (PLAN §12): a Blazor Web App in the interactive server render mode, with MudBlazor.</summary>
public static class DashboardExtensions
{
    /// <summary>
    /// Registers the Razor components with interactive server rendering, MudBlazor (snackbars bottom-right, newest on top, one of each),
    /// the cascading authentication state built from the admin app's session (<see cref="DashboardAuthenticationStateProvider"/>), the
    /// circuit handler that ends a circuit whose session ended, and the per-circuit theme. The Application services, the admin app's
    /// authentication and Data Protection come from the host.
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
        return services;
    }

    /// <summary>
    /// Maps the dashboard's pages and its circuit hub. Blazor's own <c>frame-ancestors</c> header is turned off: the admin app's
    /// hardening sends the dashboard's whole CSP (<see cref="DashboardSecurity.ContentSecurityPolicy"/>), which forbids framing.
    /// </summary>
    public static RazorComponentsEndpointConventionBuilder MapPaduraksaDashboard(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode(server => server.ContentSecurityFrameAncestorsPolicy = null);
    }
}
