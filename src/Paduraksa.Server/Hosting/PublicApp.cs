using System.Security.Cryptography.X509Certificates;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Paduraksa.Proxy;
using Paduraksa.Proxy.Gate;
using Paduraksa.Server.Configuration;

namespace Paduraksa.Server.Hosting;

/// <summary>
/// The public app: :80 (H1) and :443 (H1/H2), built on the <b>generic host with an explicit pipeline</b>, not
/// <c>WebApplication</c> (PLAN §7.1, spike S3). YARP's <c>AddReverseProxy</c> registers authorization services, and
/// <c>WebApplication</c> would then insert <c>UseAuthorization()</c> ahead of the gate on every request; here
/// nothing is added that isn't written below. The pipeline holds only the proxy path, so admin endpoints can't be
/// routed here at all (PLAN §3):
/// <code>
/// gate (decides; answers or sets YARP's endpoint) → UseRouting (skips matching) → YARP (affinity → LB → passive health)
/// </code>
/// Kestrel isn't bound to the <c>Kestrel</c> config section, and the hosting keys that could still add or replace
/// listeners (<c>urls</c>, <c>http_ports</c>, ...) are pinned by <see cref="HostSetup.PinListenerConfiguration(IConfigurationBuilder)"/>,
/// so the listeners below are the only ones.
/// </summary>
internal static class PublicApp
{
    public static IHost Build(HostContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        PaduraksaListenOptions listen = context.Options.Listen;
        // TODO(M1 integration): listener layout, per-SNI selection and H3 from Paduraksa.Proxy.Tls.
        // TODO(M3): certificates from the vault.
        X509Certificate2 certificate = context.Shared.ServerCertificate.Certificate;

        IHostBuilder builder = new HostBuilder()
            .UseContentRoot(context.ContentRootPath)
            .UseEnvironment(context.EnvironmentName)
            .ConfigureHostConfiguration(host => host.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [HostDefaults.ApplicationKey] = context.ApplicationName,
            }))
            .ConfigureAppConfiguration((_, configuration) => HostSetup.ApplySharedConfiguration(configuration, context))
            .ConfigureServices((_, services) => HostSetup.ApplySharedServices(services, context, HostRole.Public))
            .ConfigureWebHost(
                web => web
                    .UseKestrelCore()
                    .UseKestrelHttpsConfiguration()
                    .ConfigureKestrel(kestrel =>
                    {
                        kestrel.AddServerHeader = false;
                        kestrel.ListenAnyIP(listen.Http, http => http.Protocols = HttpProtocols.Http1);
                        kestrel.ListenAnyIP(listen.Https, https =>
                        {
                            // TODO(M1 integration, spike S1): H3 on 443/udp with Paduraksa-emitted Alt-Svc (PLAN §7.5).
                            https.Protocols = HttpProtocols.Http1AndHttp2;
                            https.UseHttps(certificate);
                        });
                    })
                    .ConfigureServices(services => services.AddPaduraksaProxy(context.Shared.SnapshotPublisher))
                    .Configure(static app =>
                    {
                        // TODO(M1 integration): connection middleware (PROXY protocol, IP gate, ClientInfo) on the listeners,
                        // and the Paduraksa.Proxy.Net client-IP resolver as the gate's IClientAddressSource.
                        GateMiddleware gate = app.ApplicationServices.GetRequiredService<GateMiddleware>();
                        app.Use(gate.InvokeAsync);
                        app.UseRouting();
                        app.UseEndpoints(static endpoints => endpoints.MapPaduraksaProxy());
                    }),
                static web => web.SuppressEnvironmentConfiguration = true)
            .ConfigureServices((_, services) => HostSetup.ApplyCallerServices(services, context, HostRole.Public))
            .ConfigureAppConfiguration((_, configuration) => HostSetup.PinListenerConfiguration(configuration));

        return builder.Build();
    }
}
