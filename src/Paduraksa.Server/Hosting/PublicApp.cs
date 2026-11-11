using Paduraksa.Proxy;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Logging;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Tls;
using Paduraksa.Proxy.Yarp;
using Paduraksa.Server.Configuration;

namespace Paduraksa.Server.Hosting;

/// <summary>
/// The public app, built on the <b>generic host with an explicit pipeline</b>, not <c>WebApplication</c>: YARP's
/// <c>AddReverseProxy</c> registers authorization services, and <c>WebApplication</c> would then insert
/// <c>UseAuthorization()</c> ahead of the gate on every request; here nothing is added that isn't written below. The
/// pipeline holds only the proxy path, so admin endpoints can't be routed here at all:
/// <code>
/// gate (decides; answers or sets YARP's endpoint) → UseRouting (skips matching) → YARP (affinity → LB → passive health)
/// </code>
/// The listeners (dual-mode <c>[::]</c>, or <c>0.0.0.0</c> without IPv6) are <c>Paduraksa:Listen:Http</c> and <c>:Https</c>
/// over TCP, the latter with TLS from the live snapshot's per-SNI options, plus QUIC when HTTP/3 is available. Kestrel's own
/// <c>Alt-Svc</c> is off (the gate sends one per host), and the hosting keys that could add or replace listeners
/// (<c>urls</c>, <c>http_ports</c>, ...) are pinned by
/// <see cref="HostSetup.PinListenerConfiguration(IConfigurationBuilder)"/>.
/// </summary>
internal static partial class PublicApp
{
    public static IHost Build(HostContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var listen = context.Options.Listen;
        var listeners = context.Shared.PublicListeners;
        var logger = new RoleLoggerFactory(context.Shared.LoggerFactory, HostRole.Public).CreateLogger(typeof(PublicApp).FullName!);
        if (listeners.Http3)
        {
            // The positive half of the libmsquic check: this line only appears when QUIC actually loaded.
            LogHttp3Enabled(logger, listen.Https, listen.PublicHttpsPort);
        }
        else if (listeners.Http3Requested)
        {
            LogHttp3Unavailable(logger);
        }

        var builder = new HostBuilder()
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
                        // The body limit is per host and route and the gate puts it on each request. Kestrel's 30 MB default
                        // would silently break the uploads people put behind this, so what the gate leaves unlimited stays so.
                        kestrel.Limits.MaxRequestBodySize = null;
                        RequestLimitsGate.ConfigureTransport(kestrel.Limits);
                        kestrel.ListenPublicHttp(address: null, listen.Http, http => http.UseClientInfo(listeners.HttpProxyProtocol));
                        var http3 = kestrel.ListenPublicHttps(
                            new PublicHttpsListenerOptions
                            {
                                Port = listen.Https,
                                EnableHttp3 = listeners.Http3,
                                ConfigureTcpConnection = https => https
                                    .UseClientInfo(listeners.HttpsProxyProtocol)
                                    .UseOriginLockdown(context.Shared.SnapshotStore, listeners.OriginLockdownCounters),
                            },
                            listeners.Selector);
                        if (http3 != listeners.Http3)
                        {
                            throw new InvalidOperationException("The HTTP/3 listener doesn't match what the snapshot compiler advertises.");
                        }
                    })
                    .ConfigureServices(services =>
        {
            services.AddPaduraksaProxy(context.Shared.SnapshotPublisher, context.Shared.AdminInternal, context.Shared.ProofOfWork, context.Shared.RateLimits, context.Shared.Metrics);
            // YARP's AddReverseProxy registers Data Protection and a key-ring warm-up hosted service. The public app's
            // provider is the in-memory ephemeral one (HostSetup), so the warm-up must go: it would create a key elsewhere.
            EphemeralDataProtection.RemoveKeyRingWarmup(services);
        })
                    .Configure(static app =>
                    {
                        // The simulator reads destination health from this app's YARP while it runs.
                        var health = app.ApplicationServices.GetRequiredService<DeferredUpstreamHealth>();
                        health.Attach(app.ApplicationServices.GetRequiredService<IUpstreamHealth>());
                        app.ApplicationServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopped.Register(health.Detach);
                        // The access log wraps everything, the gate included: every outcome gets a line.
                        app.Use(app.ApplicationServices.GetRequiredService<AccessLogMiddleware>().InvokeAsync);
                        var gate = app.ApplicationServices.GetRequiredService<GateMiddleware>();
                        app.Use(gate.InvokeAsync);
                        app.UseRouting();
                        app.UseEndpoints(static endpoints => endpoints.MapPaduraksaProxy());
                    }),
                static web => web.SuppressEnvironmentConfiguration = true)
            .ConfigureServices((_, services) => HostSetup.ApplyCallerServices(services, context, HostRole.Public))
            .ConfigureAppConfiguration((_, configuration) => HostSetup.PinListenerConfiguration(configuration));

        return builder.Build();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "HTTP/3 is on: QUIC on UDP {Port} (libmsquic loaded; Alt-Svc advertises port {PublicPort})")]
    private static partial void LogHttp3Enabled(ILogger logger, int port, int publicPort);

    [LoggerMessage(Level = LogLevel.Information, Message = "HTTP/3 is off: libmsquic isn't available on this system (public HTTPS serves HTTP/1.1 and HTTP/2 only)")]
    private static partial void LogHttp3Unavailable(ILogger logger);
}
