using System.Net;
using System.Security.Cryptography.X509Certificates;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;

using Paduraksa.Server.Configuration;
using Paduraksa.Server.Placeholders;

namespace Paduraksa.Server.Hosting;

/// <summary>
/// The admin <c>WebApplication</c>: HTTPS on <c>Paduraksa:Listen:Admin</c> (default 9443).
/// Today: health endpoints (PLAN §11.3) and a placeholder page.
/// TODO(M4): auth, /api/v1, /mcp, /metrics. TODO(M5): dashboard. TODO(M4/§3.1): internal UDS listener,
/// loopback plain-HTTP fallback listener.
/// </summary>
internal static class AdminApp
{
    private static readonly string[] GetAndHead = [HttpMethods.Get, HttpMethods.Head];

    public static WebApplication Build(HostContext context)
    {
        WebApplicationBuilder builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            ApplicationName = context.ApplicationName,
            EnvironmentName = context.EnvironmentName,
            ContentRootPath = context.ContentRootPath,
        });
        HostSetup.ApplyCommon(builder, context, HostRole.Admin);

        PaduraksaListenOptions listen = context.Options.Listen;
        IPAddress bind = IPAddress.Parse(listen.AdminBind);
        // TODO(M3): certificate issued by the internal CA (vault).
        X509Certificate2 certificate = context.Shared.ServerCertificate.Certificate;

        builder.WebHost.UseKestrelCore().UseKestrelHttpsConfiguration();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Listen(bind, listen.Admin, admin =>
            {
                admin.Protocols = HttpProtocols.Http1AndHttp2;
                admin.UseHttps(certificate);
            });
        });
        builder.Services.AddRoutingCore();

        HostSetup.ApplyCallerServices(builder, context, HostRole.Admin);
        HostSetup.PinListenerConfiguration(builder);

        WebApplication app = builder.Build();
        MapHealth(app);
        app.MapGet("/", PlaceholderPageAsync);
        return app;
    }

    /// <summary>PLAN §11.3: unauthenticated, status only, never any detail.</summary>
    private static void MapHealth(WebApplication app)
    {
        SnapshotStore snapshots = app.Services.GetRequiredService<SnapshotStore>();
        HealthState health = app.Services.GetRequiredService<HealthState>();

        // Live: the process is up and a snapshot is loaded.
        app.MapMethods("/healthz", GetAndHead, context =>
            WriteStatusAsync(context, snapshots.Current is not null, "ok\n", "unavailable\n"));

        // Ready: listeners bound and not draining. TODO(M2): DB reachable. TODO(M3): vault unlocked.
        app.MapMethods("/readyz", GetAndHead, context =>
            WriteStatusAsync(context, health.IsReady && snapshots.Current is not null, "ready\n", "not ready\n"));
    }

    private static Task WriteStatusAsync(HttpContext context, bool healthy, string okBody, string failBody)
    {
        HttpResponse response = context.Response;
        response.StatusCode = healthy ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable;
        response.Headers.CacheControl = "no-store";
        response.ContentType = "text/plain; charset=utf-8";
        return HttpMethods.IsHead(context.Request.Method)
            ? Task.CompletedTask
            : response.WriteAsync(healthy ? okBody : failBody, context.RequestAborted);
    }

    // TODO(M5): replaced by the Blazor dashboard.
    private static Task PlaceholderPageAsync(HttpContext context)
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        return context.Response.WriteAsync(
            "<!doctype html><html><head><title>Paduraksa</title></head><body><h1>Paduraksa admin</h1><p>Placeholder: the dashboard arrives in M5.</p></body></html>",
            context.RequestAborted);
    }
}
