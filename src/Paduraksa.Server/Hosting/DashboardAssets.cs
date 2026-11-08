using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.StaticWebAssets;

namespace Paduraksa.Server.Hosting;

/// <summary>
/// The dashboard's static files (Blazor's script, MudBlazor's and the dashboard's own CSS and scripts), served by the admin app itself
/// with fingerprinted URLs, ETags and compression (<c>MapStaticAssets</c>): never from a CDN, so the admin works offline on a LAN.
/// </summary>
/// <remarks>
/// A published build carries the files in <c>wwwroot</c> beside the binary. A build output (<c>dotnet run</c>, the tests) carries only a
/// manifest pointing at the projects' and packages' own folders; the static web assets loader maps those, which the web host does by
/// itself only in the Development environment.
/// </remarks>
internal static class DashboardAssets
{
    /// <summary>The endpoints manifest the SDK writes beside the binary.</summary>
    public static string ManifestPath => Path.Combine(AppContext.BaseDirectory, "paduraksa.staticwebassets.endpoints.json");

    public static void Use(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        StaticWebAssetsLoader.UseStaticWebAssets(builder.Environment, builder.Configuration);
    }

    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapStaticAssets(ManifestPath);
    }
}
