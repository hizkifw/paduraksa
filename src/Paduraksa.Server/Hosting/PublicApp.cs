using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Paduraksa.Proxy;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Tls;
using Paduraksa.Server.Configuration;

namespace Paduraksa.Server.Hosting;

/// <summary>
/// The public app, built on the <b>generic host with an explicit pipeline</b>, not <c>WebApplication</c> (PLAN §7.1,
/// spike S3). YARP's <c>AddReverseProxy</c> registers authorization services, and <c>WebApplication</c> would then insert
/// <c>UseAuthorization()</c> ahead of the gate on every request; here nothing is added that isn't written below. The
/// pipeline holds only the proxy path, so admin endpoints can't be routed here at all (PLAN §3):
/// <code>
/// gate (decides; answers or sets YARP's endpoint) → UseRouting (skips matching) → YARP (affinity → LB → passive health)
/// </code>
/// Listeners (PLAN §3, §7.5), all on dual-mode <c>[::]</c> (<c>0.0.0.0</c> without IPv6):
/// <list type="bullet">
/// <item><c>Paduraksa:Listen:Http</c>/tcp, H1: ClientInfo connection middleware (PROXY protocol if enabled).</item>
/// <item><c>Paduraksa:Listen:Https</c>/tcp, H1/H2: ClientInfo (PROXY protocol if enabled), then TLS with per-SNI
/// options from the live snapshot.</item>
/// <item><c>Paduraksa:Listen:Https</c>/udp, H3, when enabled and libmsquic is present: the QUIC handshake callback
/// selects the certificate and attaches ClientInfo.</item>
/// </list>
/// Kestrel's own <c>Alt-Svc</c> is off; the gate sends one per host with the public port. Kestrel isn't bound to the
/// <c>Kestrel</c> config section, and the hosting keys that could still add or replace listeners (<c>urls</c>,
/// <c>http_ports</c>, ...) are pinned by <see cref="HostSetup.PinListenerConfiguration(IConfigurationBuilder)"/>, so
/// these are the only listeners.
/// </summary>
internal static partial class PublicApp
{
    public static IHost Build(HostContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        PaduraksaListenOptions listen = context.Options.Listen;
        PublicListenerSetup listeners = context.Shared.PublicListeners;
        if (listeners.Http3Requested && !listeners.Http3)
        {
            ILogger logger = new RoleLoggerFactory(context.Shared.LoggerFactory, HostRole.Public).CreateLogger(typeof(PublicApp).FullName!);
            LogHttp3Unavailable(logger);
        }

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
                    .UsePublicHttpsTransports()
                    .ConfigureKestrel(kestrel =>
                    {
                        kestrel.AddServerHeader = false;
                        kestrel.ListenPublicHttp(address: null, listen.Http, http => http.UseClientInfo(listeners.HttpProxyProtocol));
                        bool http3 = kestrel.ListenPublicHttps(
                            new PublicHttpsListenerOptions
                            {
                                Port = listen.Https,
                                EnableHttp3 = listeners.Http3,
                                ConfigureTcpConnection = https => https.UseClientInfo(listeners.HttpsProxyProtocol),
                            },
                            listeners.Selector);
                        if (http3 != listeners.Http3)
                        {
                            throw new InvalidOperationException("The HTTP/3 listener doesn't match what the snapshot compiler advertises.");
                        }
                    })
                    .ConfigureServices(services => services.AddPaduraksaProxy(context.Shared.SnapshotPublisher))
                    .Configure(static app =>
                    {
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

    [LoggerMessage(Level = LogLevel.Information, Message = "HTTP/3 is off: libmsquic isn't available on this system (public HTTPS serves HTTP/1.1 and HTTP/2 only)")]
    private static partial void LogHttp3Unavailable(ILogger logger);
}
