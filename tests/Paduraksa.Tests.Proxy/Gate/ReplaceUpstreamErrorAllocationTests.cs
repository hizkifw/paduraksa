using System.Net;
using System.Net.Http;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Yarp;
using Paduraksa.Tests.Proxy.Testing;

using Yarp.ReverseProxy.Transforms;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.Gate;

/// <summary>
/// The replacement of an upstream's error costs nothing where it doesn't happen and nothing extra where it does: a successful response pays
/// an integer comparison, and a replaced one is written like any other page of the proxy, from byte segments.
/// </summary>
public sealed class ReplaceUpstreamErrorAllocationTests : IDisposable
{
    private readonly SnapshotStore _store = new();
    private readonly CompiledHost _on;
    private readonly CompiledHost _off;

    public ReplaceUpstreamErrorAllocationTests()
    {
        ProxyConfigModel config = new()
        {
            Upstreams = [Upstream("web", "http://127.0.0.1:1/")],
            Hosts =
            [
                ProxyHost("on", "on.test", Route("on-root", "web")) with
                {
                    ErrorPages = new HostErrorPagesSpec
                    {
                        ReplaceUpstream = UpstreamErrorReplacement.Any,
                        Pages = new Dictionary<int, ErrorPageSpec> { [404] = new() { Heading = "Lost {status}", Text = "{host} {requestId}" } },
                    },
                },
                ProxyHost("off", "off.test", Route("off-root", "web")),
            ],
        };
        _store.Publish(Bound(config));
        _on = _store.Current.Hosts.Find("on.test", out _)!;
        _off = _store.Current.Hosts.Find("off.test", out _)!;
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public void ASuccessfulResponseAllocatesNothing()
    {
        using HttpResponseMessage upstream = UpstreamResponse(HttpStatusCode.OK, "text/html");
        var (context, transform) = Prepare(_on, upstream);
        AllocAssert.Zero((context, transform), static s => Apply(s.transform), "successful response through the replace transform");
        Assert.False(transform.SuppressResponseBody);
    }

    [Fact]
    public void AnErrorOnAHostThatDidNotAskAllocatesNothing()
    {
        using HttpResponseMessage upstream = UpstreamResponse(HttpStatusCode.NotFound, "text/html");
        var (context, transform) = Prepare(_off, upstream);
        AllocAssert.Zero((context, transform), static s => Apply(s.transform), "error on a host that didn't ask");
        Assert.False(transform.SuppressResponseBody);
    }

    [Fact]
    public void AnErrorThatIsNotReplacedAllocatesNothing()
    {
        // The statuses without a page, and the content types that are never replaced, are told apart before anything is written.
        foreach (var (status, type) in new (HttpStatusCode, string)[] { (HttpStatusCode.InternalServerError, "text/html"), (HttpStatusCode.ServiceUnavailable, "text/event-stream"), (HttpStatusCode.ServiceUnavailable, "application/grpc") })
        {
            using HttpResponseMessage upstream = UpstreamResponse(status, type);
            var (context, transform) = Prepare(_on, upstream);
            AllocAssert.Zero((context, transform), static s => Apply(s.transform), $"{(int)status} {type} on a host that replaces");
            Assert.False(transform.SuppressResponseBody);
        }
    }

    [Fact]
    public void AReplacedErrorIsWrittenFromItsSegmentsAndAllocatesNothingBeyondTheWriter()
    {
        using HttpResponseMessage upstream = UpstreamResponse(HttpStatusCode.NotFound, "text/html");
        var (context, transform) = Prepare(_on, upstream);
        context.HttpContext.Request.Headers.Accept = "text/html";

        // Same measure as a built-in page: the replacement is the page writer, not a second path.
        AllocAssert.Zero(
            (context, transform),
            static s =>
            {
                s.context.HttpContext.Response.StatusCode = 404;
                Apply(s.transform);
            },
            "replaced upstream error");
        Assert.True(transform.SuppressResponseBody);
    }

    private static void Apply(ResponseTransformContext context)
    {
        // Awaited here, not stored: a transform that completes synchronously gives a completed task, and a replaced error's write does too.
        ReplaceUpstreamErrorTransform.ApplyResponseAsync(context).AsTask().GetAwaiter().GetResult();
    }

    private static HttpResponseMessage UpstreamResponse(HttpStatusCode status, string contentType)
    {
        HttpResponseMessage response = new(status) { Content = new StringContent("upstream", System.Text.Encoding.UTF8, "text/plain") };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return response;
    }

    private (ResponseContextPair Context, ResponseTransformContext Transform) Prepare(CompiledHost host, HttpResponseMessage upstream)
    {
        var http = TestContexts.Create(host.Names[0]);
        http.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(Stream.Null));
        var state = new GateRequestState();
        state.InitRequestId(http.Request, peerTrusted: false);
        state.Host = host;
        state.Snapshot = _store.Current;
        http.Features.Set<IGateFeature>(state);
        http.Response.StatusCode = (int)upstream.StatusCode;
        var transform = new ResponseTransformContext { HttpContext = http, ProxyResponse = upstream, HeadersCopied = true };
        return (new ResponseContextPair(http), transform);
    }

    private sealed record ResponseContextPair(DefaultHttpContext HttpContext);
}
