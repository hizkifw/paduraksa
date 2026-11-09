using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

using Paduraksa.Core.Hosting;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Logging;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Yarp;

using Yarp.ReverseProxy;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Transforms;

namespace Paduraksa.Proxy;

/// <summary>
/// Wires the proxy core into a public app. The pipeline itself stays explicit in the host (PLAN §7.1, spike S3):
/// <code>
/// app.Use(gate.InvokeAsync);   // GateMiddleware from DI
/// app.UseRouting();            // skips matching: the gate already set the endpoint
/// app.UseEndpoints(e => e.MapPaduraksaProxy());
/// </code>
/// </summary>
public static class ProxyHostingExtensions
{
    /// <summary>
    /// Registers YARP (fed by <paramref name="publisher"/>'s provider and listener, forwarding the path as received, clean
    /// forwarding headers built from the gate's client resolution instead of YARP's own <c>X-Forwarded-*</c>, the internal admin
    /// hop secret on admin routes only, and the admin-session cookie rules), the client factory, routing, the gate, and the
    /// default <see cref="IClientAddressSource"/> (<see cref="TrustedProxyClientAddressSource"/>: the connection's ClientInfo
    /// through the snapshot's trusted proxies) and the live destination health the request simulator reads
    /// (<see cref="IUpstreamHealth"/>).
    /// </summary>
    /// <param name="services">The public app's services.</param>
    /// <param name="publisher">The one snapshot publisher of the process.</param>
    /// <param name="adminInternal">The admin app's internal listener; null in a test app that can't compile an admin host.</param>
    public static IServiceCollection AddPaduraksaProxy(
        this IServiceCollection services, SnapshotPublisher publisher, AdminInternalListener? adminInternal = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(publisher);

        AdminInternalSecretTransform adminSecret = new(adminInternal);
        services.TryAddSingleton(publisher);
        services.TryAddSingleton(publisher.Store);
        services.AddSingleton<IProxyConfigProvider>(publisher.ConfigProvider);
        services.AddSingleton<IConfigChangeListener>(publisher.ApplyListener);
        services.AddRoutingCore();

        // The access log (PLAN §7.4). The ring, its options and the sink are shared with the core host's drain service,
        // which replaces the defaults here with the configured ones; without a core host the ring fills and nothing reads
        // it, which is what a test app wants.
        services.TryAddSingleton(new AccessLogRing());
        services.TryAddSingleton(AccessLogFormatOptions.Default);
        services.TryAddSingleton<IAccessLogSink>(NullAccessLogSink.Instance);
        services.TryAddSingleton<AccessLogMiddleware>();

        services.AddReverseProxy()
            .AddTransforms(transforms =>
            {
                transforms.UseDefaultForwarders = false;
                transforms.AddRequestTransform(AccessLogTransform.OnRequestAsync);
                transforms.AddRequestTransform(RawPathTransform.ApplyAsync);
                transforms.AddRequestTransform(ForwardedHeadersTransform.ApplyAsync);
                transforms.AddRequestTransform(adminSecret.ApplyAsync);
                transforms.AddRequestTransform(SessionCookieTransform.ApplyAsync);
                transforms.AddResponseTransform(SessionCookieTransform.ApplyResponseAsync);
                transforms.AddResponseTransform(AccessLogTransform.OnResponseAsync);
                transforms.AddResponseTransform(ResponseHeadersTransform.ApplyResponseAsync);
            });
        services.RemoveAll<IForwarderHttpClientFactory>();
        services.AddSingleton<IForwarderHttpClientFactory>(
            static provider => new PaduraksaForwarderHttpClientFactory(
                provider.GetRequiredService<ILogger<ForwarderHttpClientFactory>>(),
                provider.GetService<AdminInternalListener>()));
        services.TryAddSingleton<TrustedProxyClientAddressSource>();
        services.TryAddSingleton<IClientAddressSource>(static provider => provider.GetRequiredService<TrustedProxyClientAddressSource>());
        services.TryAddSingleton<GateMiddleware>();
        services.TryAddSingleton<IUpstreamHealth>(static provider => new YarpUpstreamHealth(provider.GetRequiredService<IProxyStateLookup>()));
        return services;
    }

    /// <summary>
    /// Maps YARP with Paduraksa's custom proxy pipeline and attaches the <see cref="SnapshotPublisher"/> to it.
    /// The custom pipeline replaces YARP's default one, so session affinity, load balancing and passive health are
    /// added back explicitly (PLAN §6.2); without them passive health silently does nothing.
    /// </summary>
    public static ReverseProxyConventionBuilder MapPaduraksaProxy(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        SnapshotPublisher publisher = endpoints.ServiceProvider.GetRequiredService<SnapshotPublisher>();
        IConfigValidator validator = endpoints.ServiceProvider.GetRequiredService<IConfigValidator>();
        return publisher.Attach(validator, () =>
        {
            ReverseProxyConventionBuilder builder = endpoints.MapReverseProxy(static proxy =>
            {
                proxy.Use((Func<HttpContext, RequestDelegate, Task>)RejectUngatedRequests);
                proxy.Use((Func<HttpContext, RequestDelegate, Task>)ReapplyGateHeaders);
                proxy.UseSessionAffinity();
                proxy.UseLoadBalancing();
                proxy.UsePassiveHealthChecks();
            });
            return (builder, YarpDataSource(endpoints));
        });
    }

    /// <summary>
    /// Runs the forward, then puts the gate's per-host response headers back if the response can still change. This is
    /// for YARP's body-error path, which clears the response and answers a bare 502 without running the response
    /// transforms again (<see cref="GateResponseHeaders.Reapply"/>).
    /// </summary>
    private static async Task ReapplyGateHeaders(HttpContext context, RequestDelegate next)
    {
        await next(context);
        GateResponseHeaders.Reapply(context);
    }

    /// <summary>
    /// Defence in depth: only the endpoint the gate chose may be proxied. If routing ever matched YARP's internal
    /// <c>/.paduraksa/r/...</c> routes by itself (the gate missing from the pipeline, or a future change letting a
    /// request through without an endpoint), the request is answered 404 instead of reaching an upstream unchecked.
    /// </summary>
    private static Task RejectUngatedRequests(HttpContext context, RequestDelegate next)
    {
        GateRequestState? state = context.Features.Get<IGateFeature>()?.State;
        if (state is { Route: { } route, Snapshot: { } snapshot }
            && (uint)route.Slot < (uint)snapshot.YarpEndpoints.Length
            && ReferenceEquals(snapshot.YarpEndpoints[route.Slot], context.GetEndpoint()))
        {
            return next(context);
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }

    /// <summary>The data source <c>MapReverseProxy</c> just added: YARP appends its own as the last one.</summary>
    private static EndpointDataSource YarpDataSource(IEndpointRouteBuilder endpoints)
    {
        EndpointDataSource? last = null;
        foreach (EndpointDataSource source in endpoints.DataSources)
        {
            last = source;
        }

        return last ?? throw new InvalidOperationException("MapReverseProxy didn't add an endpoint data source.");
    }
}
