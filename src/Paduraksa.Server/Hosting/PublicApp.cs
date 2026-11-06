using System.Security.Cryptography.X509Certificates;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;

using Paduraksa.Server.Configuration;

namespace Paduraksa.Server.Hosting;

/// <summary>
/// The public <c>WebApplication</c>: :80 (H1) and :443 (H1/H2). Its pipeline contains only the proxy path,
/// so admin endpoints can't be routed here at all (PLAN §3). Built on the empty builder so little is implicit:
/// no default middleware, no routing, and Kestrel isn't bound to the <c>Kestrel</c> config section. The hosting
/// keys that could still add or replace listeners (<c>urls</c>, <c>http_ports</c>, ...) are pinned by
/// <see cref="HostSetup.PinListenerConfiguration"/>, so the listeners below are the only ones.
/// </summary>
internal static class PublicApp
{
    private static readonly byte[] NoHostConfiguredBody = "no host configured\n"u8.ToArray();

    public static WebApplication Build(HostContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        WebApplicationBuilder builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            ApplicationName = context.ApplicationName,
            EnvironmentName = context.EnvironmentName,
            ContentRootPath = context.ContentRootPath,
        });
        HostSetup.ApplyCommon(builder, context, HostRole.Public);

        PaduraksaListenOptions listen = context.Options.Listen;
        // TODO(M1): per-SNI selection via the snapshot's TLS selector. TODO(M3): certificates from the vault.
        X509Certificate2 certificate = context.Shared.ServerCertificate.Certificate;

        builder.WebHost.UseKestrelCore().UseKestrelHttpsConfiguration();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.ListenAnyIP(listen.Http, http => http.Protocols = HttpProtocols.Http1);
            kestrel.ListenAnyIP(listen.Https, https =>
            {
                // TODO(M1, spike S1): H3 on 443/udp with Paduraksa-emitted Alt-Svc (PLAN §7.5).
                https.Protocols = HttpProtocols.Http1AndHttp2;
                https.UseHttps(certificate);
            });
        });

        HostSetup.ApplyCallerServices(builder, context, HostRole.Public);
        HostSetup.PinListenerConfiguration(builder);

        WebApplication app = builder.Build();
        // TODO(M1): connection middleware -> gate -> host policies -> YARP (PLAN §7.2).
        app.Run(NoHostConfiguredAsync);
        return app;
    }

    /// <summary>Terminal placeholder: no host table yet, so every request is "unknown host".</summary>
    private static Task NoHostConfiguredAsync(HttpContext context)
    {
        HttpResponse response = context.Response;
        response.StatusCode = StatusCodes.Status404NotFound;
        response.ContentType = "text/plain; charset=utf-8";
        response.ContentLength = NoHostConfiguredBody.Length;
        return HttpMethods.IsHead(context.Request.Method)
            ? Task.CompletedTask
            : response.Body.WriteAsync(NoHostConfiguredBody, context.RequestAborted).AsTask();
    }
}
