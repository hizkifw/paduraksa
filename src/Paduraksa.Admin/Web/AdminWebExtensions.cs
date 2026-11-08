using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Paduraksa.Admin.Auth;
using Paduraksa.Application.Identity;

namespace Paduraksa.Admin.Web;

/// <summary>
/// The browser side of the admin app (PLAN §12): the authentication scheme the dashboard's pages are authorized against, antiforgery,
/// the sign-in form endpoints, and the pipeline hooks that open the setup page and its static files during setup, guard the circuit hub
/// and give HTML pages the dashboard's CSP. The dashboard itself (its components and render modes) is mapped by the composition root.
/// </summary>
public static class AdminWebExtensions
{
    /// <summary>
    /// Registers the scheme (<see cref="SessionIdentity.Scheme"/>: the session the admin's authentication middleware already checked, with a
    /// challenge that redirects to the sign-in page), authorization, and antiforgery with a <c>__Host-</c>, <c>SameSite=Strict</c> cookie.
    /// Data Protection (which antiforgery and the circuits need) is the host's to register.
    /// </summary>
    public static IServiceCollection AddPaduraksaAdminWeb(this IServiceCollection services, AdminWebOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton(options);
        services.AddAuthentication(SessionIdentity.Scheme)
            .AddScheme<AuthenticationSchemeOptions, AdminSessionAuthenticationHandler>(SessionIdentity.Scheme, displayName: null, configureOptions: null);
        services.AddAuthorization();
        services.AddAntiforgery(antiforgery =>
        {
            antiforgery.Cookie.Name = AntiforgeryCookieName;
            antiforgery.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            antiforgery.Cookie.SameSite = SameSiteMode.Strict;
            antiforgery.Cookie.Path = "/";
            antiforgery.Cookie.HttpOnly = true;

            // The admin's own hardening sends X-Frame-Options: DENY on every response.
            antiforgery.SuppressXFrameOptionsHeader = true;
        });
        return services;
    }

    /// <summary>The antiforgery cookie: <c>__Host-</c> so a browser requires <c>Secure</c>, <c>Path=/</c> and no <c>Domain</c>.</summary>
    public const string AntiforgeryCookieName = "__Host-paduraksa-antiforgery";

    /// <summary>
    /// Adds authorization and antiforgery validation, after <see cref="AdminAuthExtensions.UsePaduraksaAdminAuth"/> (they need routing and
    /// the request's user), and fills the pipeline hooks from the <see cref="AdminWebOptions"/>: the setup page, its form and the static
    /// files are open during setup (a browser opening a page is sent to the setup page), the circuit hub needs a signed-in, same-origin
    /// caller, and HTML documents get the dashboard's CSP.
    /// </summary>
    public static WebApplication UsePaduraksaAdminWeb(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        AdminWebOptions web = app.Services.GetRequiredService<AdminWebOptions>();
        AdminPipelineHooks hooks = app.Services.GetRequiredService<AdminPipelineHooks>();
        hooks.SetupPaths.Add(web.SetupPage);
        hooks.SetupPaths.Add(web.SetupAction);
        foreach (string prefix in web.StaticAssetPathPrefixes)
        {
            hooks.SetupAllowedPathPrefixes.Add(prefix);
            hooks.StaticAssetPathPrefixes.Add(prefix);
        }

        hooks.SetupPagePath = web.SetupPage;
        hooks.AuthenticatedPathPrefixes.Add(web.CircuitHubPath);
        hooks.SameOriginPathPrefixes.Add(web.CircuitHubPath);
        hooks.HtmlContentSecurityPolicy = web.ContentSecurityPolicy;

        app.UseAuthorization();
        app.UseAntiforgery();
        return app;
    }

    /// <summary>Maps the sign-in, second-factor, sign-out and setup form endpoints (<see cref="AdminWebSignInEndpoints"/>).</summary>
    public static IEndpointRouteBuilder MapPaduraksaAdminWeb(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        AdminWebSignInEndpoints.Map(endpoints, endpoints.ServiceProvider.GetRequiredService<AdminWebOptions>());
        return endpoints;
    }
}

/// <summary>
/// The dashboard's authentication scheme. It does not read the cookie itself: the admin's authentication middleware has already checked
/// it through <see cref="SessionService"/> and set the request's user to a <see cref="SessionIdentity"/>; this reports that user, and a
/// challenge (a page that needs a signed-in user, asked for anonymously) is a redirect to the sign-in page with the page to come back to.
/// </summary>
internal sealed class AdminSessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, AdminWebOptions web)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
        Task.FromResult(SessionIdentity.Of(Context.User) is not null
            ? AuthenticateResult.Success(new AuthenticationTicket(Context.User, Scheme.Name))
            : AuthenticateResult.NoResult());

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) || !HttpMethods.IsGet(Request.Method))
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }

        string returnUrl = Request.PathBase.Add(Request.Path) + Request.QueryString;
        Response.Redirect(AdminWebSignInEndpoints.IsLocalPath(returnUrl) && returnUrl != web.HomePage
            ? QueryHelpers.AddQueryString(web.SignInPage, AdminWebForm.ReturnUrl, returnUrl)
            : web.SignInPage);
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}
