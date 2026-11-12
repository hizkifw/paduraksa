using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

using Paduraksa.Core.Hosting;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Logging;
using Paduraksa.Proxy.Metrics;
using Paduraksa.Proxy.Pages;
using Paduraksa.Proxy.RateLimiting;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Yarp;

using Yarp.ReverseProxy;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Transforms;

namespace Paduraksa.Proxy;

/// <summary>
/// Wires the proxy core into a public app. The pipeline itself stays explicit in the host:
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
    /// <param name="proofOfWork">The process's proof-of-work key, clock and counters; null: a fresh, keyless runtime (a
    /// protected route then fails closed). The snapshot compiler must carry the same instance.</param>
    /// <param name="rateLimits">The process's rate-limit table, slots and counters; null: a fresh runtime. The snapshot
    /// compiler and the publisher must carry the same instance.</param>
    /// <param name="metrics">The process's request counters and latency histograms; null: a fresh registry. The snapshot compiler
    /// and the publisher must carry the same instance, or the compiled objects get no slot and nothing is counted.</param>
    public static IServiceCollection AddPaduraksaProxy(
        this IServiceCollection services, SnapshotPublisher publisher, AdminInternalListener? adminInternal = null, ProofOfWorkRuntime? proofOfWork = null,
        RateLimitRuntime? rateLimits = null, MetricsRegistry? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(publisher);

        ProofOfWorkRuntime runtime = proofOfWork ?? new ProofOfWorkRuntime();
        AdminInternalSecretTransform adminSecret = new(adminInternal);
        services.TryAddSingleton(publisher);
        services.TryAddSingleton(publisher.Store);
        services.TryAddSingleton(runtime);
        services.TryAddSingleton(runtime.Stats);
        services.AddSingleton<IProxyConfigProvider>(publisher.ConfigProvider);
        services.AddSingleton<IConfigChangeListener>(publisher.ApplyListener);
        services.AddRoutingCore();

        // The access log. The ring, its options and the sink are shared with the core host's drain service,
        // which replaces the defaults here with the configured ones; without a core host the ring fills and nothing reads
        // it, which is what a test app wants.
        services.TryAddSingleton(new AccessLogRing());
        services.TryAddSingleton(AccessLogFormatOptions.Default);
        services.TryAddSingleton<IAccessLogSink>(NullAccessLogSink.Instance);
        services.TryAddSingleton(static provider => new AccessLogTail(
            AccessLogTail.DefaultCapacity, provider.GetRequiredService<AccessLogFormatOptions>(), provider.GetRequiredService<SnapshotStore>()));
        services.TryAddSingleton(metrics ?? new MetricsRegistry());
        services.TryAddSingleton<AccessLogMiddleware>();

        services.AddReverseProxy()
            .AddTransforms(transforms =>
            {
                transforms.UseDefaultForwarders = false;
                // YARP's default sends the destination's address as Host. Upstreams see the client's, as behind nginx or
                // Caddy; on an HTTPS upstream that also makes it the SNI and the name the upstream's certificate is checked
                // against.
                transforms.AddOriginalHost(true);
                transforms.AddRequestTransform(AccessLogTransform.OnRequestAsync);
                transforms.AddRequestTransform(RawPathTransform.ApplyAsync);
                transforms.AddRequestTransform(ForwardedHeadersTransform.ApplyAsync);
                transforms.AddRequestTransform(adminSecret.ApplyAsync);
                transforms.AddRequestTransform(SessionCookieTransform.ApplyAsync);
                transforms.AddResponseTransform(SessionCookieTransform.ApplyResponseAsync);
                transforms.AddResponseTransform(AccessLogTransform.OnResponseAsync);
                transforms.AddResponseTransform(ResponseHeadersTransform.ApplyResponseAsync);
                transforms.AddResponseTransform(ReplaceUpstreamErrorTransform.ApplyResponseAsync);
            });
        services.RemoveAll<IForwarderHttpClientFactory>();
        services.AddSingleton<IForwarderHttpClientFactory>(
            static provider => new PaduraksaForwarderHttpClientFactory(
                provider.GetRequiredService<ILogger<ForwarderHttpClientFactory>>(),
                provider.GetService<AdminInternalListener>()));
        services.TryAddSingleton<TrustedProxyClientAddressSource>();
        services.TryAddSingleton<IClientAddressSource>(static provider => provider.GetRequiredService<TrustedProxyClientAddressSource>());
        services.TryAddSingleton<GateMiddleware>();
        services.RecordActiveProbes();
        services.TryAddSingleton<IUpstreamHealth>(static provider => new YarpUpstreamHealth(provider.GetRequiredService<IProxyStateLookup>(), provider.GetRequiredService<ProbeLog>()));
        services.AddPaduraksaRateLimiting(rateLimits);
        return services;
    }

    /// <summary>
    /// Maps YARP with Paduraksa's custom proxy pipeline and attaches the <see cref="SnapshotPublisher"/> to it.
    /// The custom pipeline replaces YARP's default one, so session affinity, load balancing and passive health are
    /// added back explicitly; without them passive health silently does nothing.
    /// </summary>
    public static ReverseProxyConventionBuilder MapPaduraksaProxy(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var publisher = endpoints.ServiceProvider.GetRequiredService<SnapshotPublisher>();
        var validator = endpoints.ServiceProvider.GetRequiredService<IConfigValidator>();
        return publisher.Attach(validator, () =>
        {
            var builder = endpoints.MapReverseProxy(static proxy =>
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
    /// Registers a response-start hook that puts the gate's per-host response headers back after a YARP error, writes the
    /// built-in page for the error, and calls the rest of the pipeline. A body error clears the response and a missing
    /// destination bypasses it, neither running the response transforms again (<see cref="GateResponseHeaders.Reapply"/>);
    /// on a successful forward the transforms already applied the headers, so applying them again, as the old
    /// post-forward step did for every bodyless response such as a 204 or a 304, is wasted work. The hook checks
    /// <see cref="IForwarderErrorFeature"/> first. The callback is static with the context as its state, and Kestrel
    /// creates the per-connection hook stack once; see <see cref="UpstreamErrors"/> for why the error body can be written
    /// there and why the successful path stays free.
    /// </summary>
    private static Task ReapplyGateHeaders(HttpContext context, RequestDelegate next)
    {
        RegisterReapply(context);
        return next(context);
    }

    /// <summary>Registers the reapply hook; internal so the allocation test can measure the registration on Kestrel.</summary>
    internal static void RegisterReapply(HttpContext context)
    {
        // Kestrel throws for a hook registered after the response started, and then there is nothing to put back anyway.
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.OnStarting(OnResponseStarting, context);
    }

    /// <summary>The response-start callback; internal so the allocation test can measure the successful path through it.</summary>
    internal static Task OnResponseStarting(object state)
    {
        var http = (HttpContext)state;
        // Every YARP path that answers without the transforms sets the feature before the response starts: no healthy
        // destination (503), a failed request or timeout (502/504), a request-body error (400/408/502/504), a malformed
        // response (502), a body error (502) and a failed upgrade (502).
        if (http.Features.Get<IForwarderErrorFeature>() is null)
        {
            return Task.CompletedTask;
        }

        GateResponseHeaders.Reapply(http);
        return UpstreamErrors.WriteAsync(http);
    }

    /// <summary>
    /// Defence in depth: only the endpoint the gate chose may be proxied. If routing ever matched YARP's internal
    /// <c>/.paduraksa/r/...</c> routes by itself (the gate missing from the pipeline, or a future change letting a
    /// request through without an endpoint), the request is answered 404 instead of reaching an upstream unchecked.
    /// </summary>
    private static Task RejectUngatedRequests(HttpContext context, RequestDelegate next)
    {
        var state = context.Features.Get<IGateFeature>()?.State;
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
        foreach (var source in endpoints.DataSources)
        {
            last = source;
        }

        return last ?? throw new InvalidOperationException("MapReverseProxy didn't add an endpoint data source.");
    }
}
