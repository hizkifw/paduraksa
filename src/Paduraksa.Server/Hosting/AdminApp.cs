using System.Net;
using System.Security.Cryptography.X509Certificates;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;

using Paduraksa.Admin.Auth;
using Paduraksa.Admin.Identity;
using Paduraksa.Application.Commands;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Server.Configuration;
using Paduraksa.Server.Placeholders;

namespace Paduraksa.Server.Hosting;

/// <summary>
/// The admin <c>WebApplication</c>: HTTPS on <c>Paduraksa:Listen:Admin</c> (default 9443).
/// Health endpoints (PLAN §11.3), the security pipeline and identity API (<c>Paduraksa.Admin</c>: setup gate, authentication, CSRF
/// defence, hardening), a placeholder page, the optional source allowlist and the opt-in loopback plain-HTTP listener.
/// TODO(M4): /mcp, /metrics. TODO(M5): dashboard. TODO(§3.1): internal UDS listener.
/// </summary>
internal static class AdminApp
{
    private static readonly string[] GetAndHead = [HttpMethods.Get, HttpMethods.Head];

    private static readonly byte[] PlaceholderPageBody =
        "<!doctype html><html><head><title>Paduraksa</title></head><body><h1>Paduraksa admin</h1><p>Placeholder: the dashboard arrives in M5.</p></body></html>"u8.ToArray();

    public static WebApplication Build(HostContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        WebApplicationBuilder builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            ApplicationName = context.ApplicationName,
            EnvironmentName = context.EnvironmentName,
            ContentRootPath = context.ContentRootPath,
        });
        HostSetup.ApplyCommon(builder, context, HostRole.Admin);

        PaduraksaListenOptions listen = context.Options.Listen;
        // The validator already checked this; failing here would mean options that bypassed it.
        if (!IPAddress.TryParse(listen.AdminBind, out IPAddress? bind))
        {
            throw new InvalidOperationException(
                $"{PaduraksaOptions.SectionName}:Listen:AdminBind must be an IP address, was '{listen.AdminBind}'.");
        }

        // TODO(M3): certificate issued by the internal CA (vault).
        X509Certificate2 certificate = context.Shared.ServerCertificate.Certificate;

        AdminSourcePolicy sources = new(listen.AdminAllowedSources);
        builder.WebHost.UseKestrelCore().UseKestrelHttpsConfiguration();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Listen(bind, listen.Admin, admin =>
            {
                admin.Protocols = HttpProtocols.Http1AndHttp2;
                RestrictSources(admin, sources);
                admin.UseHttps(certificate);
            });

            // PLAN §3: opt-in, loopback only. ListenLocalhost binds 127.0.0.1 and ::1 and nothing else; no setting moves it.
            if (listen.AdminLoopbackHttp > 0)
            {
                kestrel.ListenLocalhost(listen.AdminLoopbackHttp, fallback => fallback.Protocols = HttpProtocols.Http1);
            }
        });
        builder.Services.AddRoutingCore();
        builder.Services.AddPaduraksaAdminAuth(context.Options.Admin.ToSecurityOptions(listen.AdminLoopbackHttp));

        HostSetup.ApplyCallerServices(builder, context, HostRole.Admin);
        HostSetup.PinListenerConfiguration(builder);

        WebApplication app = builder.Build();
        app.UsePaduraksaAdminAuth();
        MapHealth(app);
        app.MapPaduraksaIdentityApi();
        app.MapMethods("/", GetAndHead, PlaceholderPageAsync);
        return app;
    }

    /// <summary>
    /// Refuses connections from outside <c>AdminAllowedSources</c> before TLS starts: the TCP handshake completes, then the connection is
    /// aborted without a byte sent. Loopback is not implied. No-op without an allowlist. Only the main listener has it: the loopback
    /// fallback is loopback-only by construction.
    /// </summary>
    private static void RestrictSources(ListenOptions listener, AdminSourcePolicy sources)
    {
        if (!sources.IsRestricted)
        {
            return;
        }

        listener.Use(next => connection =>
        {
            if (sources.IsAllowed((connection.RemoteEndPoint as IPEndPoint)?.Address))
            {
                return next(connection);
            }

            connection.Abort();
            return Task.CompletedTask;
        });
    }

    /// <summary>PLAN §11.3: unauthenticated, status only, never any detail.</summary>
    /// <remarks>
    /// A degraded start (the proxy serves the last-known-good config because the config database can't be used, PLAN
    /// §6.1) is still <b>ready</b>: the proxy serves traffic, and taking it out of rotation would turn a config-store
    /// problem into an outage. <c>/readyz</c> then answers 200 with the body <c>degraded</c> instead of <c>ready</c>, a
    /// status a keyword monitor can alert on, and the cause is logged at Critical. The admin UI shows the banner (M5).
    /// </remarks>
    private static void MapHealth(WebApplication app)
    {
        SnapshotStore snapshots = app.Services.GetRequiredService<SnapshotStore>();
        HealthState health = app.Services.GetRequiredService<HealthState>();
        ConfigService config = app.Services.GetRequiredService<ConfigService>();

        // Live: the process is up and a snapshot is loaded.
        app.MapMethods("/healthz", GetAndHead, context =>
            WriteStatusAsync(context, snapshots.HasPublished, "ok\n", "unavailable\n"));

        // Ready: listeners bound, a snapshot live and not draining; degraded or not (see the remarks). TODO(M3): vault unlocked.
        app.MapMethods("/readyz", GetAndHead, context =>
            WriteStatusAsync(context, health.IsReady && snapshots.HasPublished, config.Status.IsDegraded ? "degraded\n" : "ready\n", "not ready\n"));
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
        HttpResponse response = context.Response;
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength = PlaceholderPageBody.Length;
        return HttpMethods.IsHead(context.Request.Method)
            ? Task.CompletedTask
            : response.Body.WriteAsync(PlaceholderPageBody, context.RequestAborted).AsTask();
    }
}
