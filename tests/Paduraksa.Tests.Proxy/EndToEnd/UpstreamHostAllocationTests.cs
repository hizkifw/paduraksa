using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Yarp;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;

using Yarp.ReverseProxy.Model;
using Yarp.ReverseProxy.Transforms;
using YarpConfiguration = Yarp.ReverseProxy.Configuration;
using YarpHealth = Yarp.ReverseProxy.Health;

namespace Paduraksa.Tests.Proxy.EndToEnd;

/// <summary>The Host transform reads a precomputed string off the compiled route: 0 B per request in every mode.</summary>
public sealed class UpstreamHostAllocationTests
{
    [Theory]
    [InlineData(UpstreamHostHeaderMode.Original, "app.test")]
    [InlineData(UpstreamHostHeaderMode.Destination, null)]
    [InlineData(UpstreamHostHeaderMode.Custom, "api.internal:8443")]
    public void ChoosingTheHostAllocatesNothing(UpstreamHostHeaderMode mode, string? sent)
    {
        var context = ContextFor(new UpstreamHost(mode, sent));

        AllocAssert.Zero(context, static c => _ = UpstreamHostTransform.HostFor(c), $"choosing the Host, {mode}");

        Assert.Equal(sent, UpstreamHostTransform.HostFor(context));
    }

    [Theory]
    [InlineData(UpstreamHostHeaderMode.Original, "app.test")]
    [InlineData(UpstreamHostHeaderMode.Custom, "api.internal:8443")]
    public void ARequestPaysForOneHostWriteAsItDidBefore(UpstreamHostHeaderMode mode, string sent)
    {
        // The chain is AddOriginalHost(false), which clears the header, then ApplyAsync. A request that had YARP's AddOriginalHost(true) paid one write.
        // Both are measured on a fresh request each time, so what is compared is only the Host handling.
        var context = ContextFor(new UpstreamHost(mode, sent));
        var before = AllocAssert.Measure(
            (context, sent),
            static s => new RequestTransformContext { HttpContext = s.context, ProxyRequest = new HttpRequestMessage() }.ProxyRequest.Headers.Host = s.sent);
        var now = AllocAssert.Measure(
            context,
            static c =>
            {
                RequestTransformContext transform = new() { HttpContext = c, ProxyRequest = new HttpRequestMessage() };
                transform.ProxyRequest.Headers.Host = null;
                UpstreamHostTransform.ApplyAsync(transform).AsTask().GetAwaiter().GetResult();
            });

        Assert.True(now.BytesPerCall <= before.BytesPerCall, $"{mode}: {now.BytesPerCall:F1} B a request now, {before.BytesPerCall:F1} B with one write");
    }

    [Fact]
    public void ProbesWrapYarpsFactoryAndASetupWithNoneToWrapFailsAtStartup()
    {
        ServiceCollection yarp = new();
        yarp.AddLogging();
        yarp.AddReverseProxy();
        _ = UpstreamHostProbes.SendCustomHostWithProbes(yarp);
        Assert.DoesNotContain(yarp, d => d.ServiceType == typeof(YarpHealth.IProbingRequestFactory) && d.ImplementationType is not null);

        Assert.Throws<InvalidOperationException>(() => UpstreamHostProbes.SendCustomHostWithProbes(new ServiceCollection()));
    }

    [Fact]
    public async Task TheTransformWritesTheChosenHost()
    {
        var context = ContextFor(new UpstreamHost(UpstreamHostHeaderMode.Custom, "api.internal:8443"));
        RequestTransformContext transform = new() { HttpContext = context, ProxyRequest = new HttpRequestMessage() };
        transform.ProxyRequest.Headers.Host = "app.test";

        await UpstreamHostTransform.ApplyAsync(transform);

        Assert.Equal("api.internal:8443", transform.ProxyRequest.Headers.Host);
    }

    [Fact]
    public void AProbeOfACustomHostSendsItAndOtherUpstreamsKeepYarpsRequest()
    {
        var custom = Cluster(new Dictionary<string, string> { [UpstreamHost.MetadataKey] = "api.internal:8443" });
        var other = Cluster(null);
        UpstreamHostProbes probes = new(new FakeProbeRequests());

        using var withHost = probes.CreateRequest(custom, Destination(custom));
        using var without = probes.CreateRequest(other, Destination(other));

        Assert.Equal("api.internal:8443", withHost.Headers.Host);
        Assert.Null(without.Headers.Host);
    }

    private static DefaultHttpContext ContextFor(UpstreamHost host)
    {
        DefaultHttpContext context = new();
        context.Request.Host = new HostString("app.test");
        context.Features.Set<IGateFeature>(new GateRequestState
        {
            Route = new CompiledRoute
            {
                Id = TestIds.Route("r"),
                MatchKind = RouteMatchKind.Prefix,
                Path = "/",
                Slot = 0,
                Policies = [],
                UpstreamHost = host,
            },
        });
        return context;
    }

    private static ClusterModel Cluster(Dictionary<string, string>? metadata) =>
        new(new YarpConfiguration.ClusterConfig { ClusterId = "up", Metadata = metadata }, new HttpMessageInvoker(new HttpClientHandler()));

    private static DestinationModel Destination(ClusterModel cluster) =>
        new(new YarpConfiguration.DestinationConfig { Address = "http://127.0.0.1:1/" });

    private sealed class FakeProbeRequests : YarpHealth.IProbingRequestFactory
    {
        public HttpRequestMessage CreateRequest(ClusterModel cluster, DestinationModel destination) =>
            new(HttpMethod.Get, "http://127.0.0.1:1/health");
    }
}
