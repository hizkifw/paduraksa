using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Yarp;

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
    /// Registers YARP (fed by <paramref name="publisher"/>'s provider and listener, forwarding the path as received and
    /// clean forwarding headers built from the gate's client resolution instead of YARP's own <c>X-Forwarded-*</c>),
    /// the client factory, routing, the gate, and the default <see cref="IClientAddressSource"/>
    /// (<see cref="TrustedProxyClientAddressSource"/>: the connection's ClientInfo through the snapshot's trusted proxies).
    /// </summary>
    public static IServiceCollection AddPaduraksaProxy(this IServiceCollection services, SnapshotPublisher publisher)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(publisher);

        services.TryAddSingleton(publisher);
        services.TryAddSingleton(publisher.Store);
        services.AddSingleton<IProxyConfigProvider>(publisher.ConfigProvider);
        services.AddSingleton<IConfigChangeListener>(publisher.ApplyListener);
        services.AddRoutingCore();
        services.AddReverseProxy()
            .AddTransforms(static transforms =>
            {
                transforms.UseDefaultForwarders = false;
                transforms.AddRequestTransform(RawPathTransform.ApplyAsync);
                transforms.AddRequestTransform(ForwardedHeadersTransform.ApplyAsync);
            });
        services.RemoveAll<IForwarderHttpClientFactory>();
        services.AddSingleton<IForwarderHttpClientFactory, PaduraksaForwarderHttpClientFactory>();
        services.TryAddSingleton<TrustedProxyClientAddressSource>();
        services.TryAddSingleton<IClientAddressSource>(static provider => provider.GetRequiredService<TrustedProxyClientAddressSource>());
        services.TryAddSingleton<GateMiddleware>();
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
                proxy.UseSessionAffinity();
                proxy.UseLoadBalancing();
                proxy.UsePassiveHealthChecks();
            });
            return (builder, YarpDataSource(endpoints));
        });
    }

    /// <summary>
    /// Defence in depth: only the endpoint the gate chose may be proxied. If routing ever matched YARP's internal
    /// <c>/.paduraksa/r/…</c> routes by itself (the gate missing from the pipeline, or a future change letting a
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
