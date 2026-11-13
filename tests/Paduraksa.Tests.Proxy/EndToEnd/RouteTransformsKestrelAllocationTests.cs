using System.Net;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Yarp;
using Paduraksa.Tests.Proxy.Testing;

using Yarp.ReverseProxy.Transforms;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.EndToEnd;

/// <summary>
/// The route transforms' 0 B claims on a real Kestrel, over HTTP/1.1 and h2c, on its own request and header stores: the gate's
/// decision for a rewriting route (the strip check reads Kestrel's raw target), and the response transform's custom rules (set,
/// remove, an add with nothing to join) with the Location transform finding no Location. A mapped Location is the one string a
/// redirect pays, and is held to that.
/// </summary>
public sealed class RouteTransformsKestrelAllocationTests(TestUpstream upstream) : IClassFixture<TestUpstream>
{
    private const int Connections = 4;
    private const int Warmup = 1_000;
    private const int Measured = 3_000;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HeaderRulesAndARewritingRouteAllocateNothingOnKestrel(bool h2c)
    {
        HostHeadersSpec rules = new()
        {
            Response =
            [
                new HeaderRuleSpec("X-Frame-Options", HeaderOperation.Set, "DENY"),
                new HeaderRuleSpec("X-Powered-By", HeaderOperation.Remove, null),
                new HeaderRuleSpec("Vary", HeaderOperation.Add, "Origin"),
            ],
        };
        ProxyConfigModel model = new()
        {
            Upstreams = [Upstream("web", upstream.Address("a"))],
            Hosts = [ProxyHost("app", "app.test", Route("app-app", "web", "/app") with { StripPrefix = true, AddPrefix = "/api" }) with { Headers = rules }],
        };
        GateStats transforms = new();
        GateStats locations = new();
        await using var server = await ProxyTestServer.StartAsync(
            model,
            new()
            {
                MeasureGate = true,
                Protocols = h2c ? HttpProtocols.Http2 : HttpProtocols.Http1,
                AfterGate = (context, next) =>
                {
                    // The transform contexts are YARP's objects, made per request by YARP anyway: they are created outside the measure.
                    ResponseTransformContext transform = new() { HttpContext = context, HeadersCopied = true };
                    var fresh = context.Items[GateStats.FreshKey] is true;

                    // The request ID's string is the one a request materialises, made by the gate's finish that MeasureGate skips.
                    _ = context.Features.Get<Paduraksa.Proxy.Gate.IGateFeature>()!.State.RequestIdText();
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    Synchronously(ResponseHeadersTransform.ApplyResponseAsync(transform));
                    Synchronously(LocationRewriteTransform.ApplyResponseAsync(transform));
                    transforms.Record(GC.GetAllocatedBytesForCurrentThread() - before, fresh);

                    // A redirect into the upstream's paths: the mapped Location is one string, and nothing else.
                    context.Response.Headers.Location = "/api/login";
                    before = GC.GetAllocatedBytesForCurrentThread();
                    Synchronously(LocationRewriteTransform.ApplyResponseAsync(transform));
                    locations.Record(GC.GetAllocatedBytesForCurrentThread() - before, fresh);
                    if (context.Response.Headers.Location.ToString() != "/app/login")
                    {
                        throw new InvalidOperationException($"the Location was mapped to '{context.Response.Headers.Location}'");
                    }

                    context.Response.Headers.Remove("Location");
                    return next(context);
                },
            });
        using var client = ProxyTestServer.CreateClient(maxConnections: Connections);

        await SendAsync(server, client, Warmup);
        server.GateStats!.Reset();
        transforms.Reset();
        locations.Reset();
        await SendAsync(server, client, Measured);

        Assert.True(server.GateStats.Requests >= Measured, $"only {server.GateStats.Requests} requests measured");
        Assert.True(server.GateStats.NonZero <= 1, $"gate allocated on Kestrel: {server.GateStats}");
        Assert.True(transforms.Requests >= Measured, $"only {transforms.Requests} transforms measured");
        Assert.True(transforms.NonZero <= 1, $"the response rules allocated on Kestrel: {transforms}");
        // One string of "/app/login" (42 B) a mapped redirect; a runtime one-off of a few KB may land in one request of the run.
        Assert.True(locations.Bytes <= (locations.Requests * 48) + 32_768, $"the Location mapping allocated more than its string: {locations}");
    }

    private static void Synchronously(ValueTask task)
    {
        if (!task.IsCompletedSuccessfully)
        {
            throw new InvalidOperationException("the transform should complete synchronously");
        }
    }

    private static async Task SendAsync(ProxyTestServer server, HttpClient client, int count)
    {
        var ct = TestContext.Current.CancellationToken;
        var next = 0;
        var workers = new Task[Connections];
        for (var w = 0; w < workers.Length; w++)
        {
            workers[w] = Task.Run(
                async () =>
                {
                    while (Interlocked.Increment(ref next) <= count)
                    {
                        using var request = server.Request("app.test", "/app/items?q=1");
                        using var response = await client.SendAsync(request, ct);
                        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                        await response.Content.LoadIntoBufferAsync(ct);
                    }
                },
                ct);
        }

        await Task.WhenAll(workers);
    }
}
