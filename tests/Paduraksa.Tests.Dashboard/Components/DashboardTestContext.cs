using System.Security.Claims;

using Bunit;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

using MudBlazor.Services;

using Paduraksa.Application.Identity;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard;
using Paduraksa.Dashboard.Auth;
using Paduraksa.Dashboard.Drawers;
using Paduraksa.Dashboard.Theme;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>A bUnit context with what the dashboard's components need: MudBlazor, a loose JS runtime, an antiforgery token, the theme.</summary>
internal sealed class DashboardTestContext : BunitContext
{
    public const string AntiforgeryValue = "test-antiforgery-token";

    public DashboardTestContext()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton<AntiforgeryStateProvider>(new FixedAntiforgery());
        Services.AddSingleton(new DashboardOptions { MinPasswordLength = 12 });
        Services.AddScoped<ThemeState>();
        Services.AddScoped<DisplayFormat>();
        Services.AddScoped<DrawerStack>();
        Services.AddScoped<DashboardBrowser>();
    }

    /// <summary>A cascading authentication state for <paramref name="user"/> (anonymous when null).</summary>
    public static Task<AuthenticationState> StateOf(ClaimsPrincipal? user) =>
        Task.FromResult(new AuthenticationState(user ?? new ClaimsPrincipal(new ClaimsIdentity())));

    private sealed class FixedAntiforgery : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken? GetAntiforgeryToken() => new(AntiforgeryValue, "__RequestVerificationToken");
    }
}

/// <summary>Signed-in users for the component tests.</summary>
internal static class TestUsers
{
    public static SessionIdentity Session(params Role[] roles) =>
        new(new SessionPrincipal(UserId.New(), SessionId.New(), "ada", "Ada Lovelace", roles));

    public static DashboardPrincipal User(params Role[] roles) => new(Session(roles), RolePermissions.Of(roles));
}
