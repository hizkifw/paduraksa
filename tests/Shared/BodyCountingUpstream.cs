// Linked into Paduraksa.Tests.Proxy and Paduraksa.Tests.Integration (<Compile Include Link>), like HeaderEchoUpstreamHost.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace Paduraksa.Tests.Shared;

/// <summary>
/// A loopback upstream that reads every request body to the end and counts the requests and the bytes it was given, so a
/// test can say what a proxy forwarded and what it kept from it.
/// </summary>
internal sealed class BodyCountingUpstream : IAsyncDisposable
{
    private readonly LoopbackUpstream _upstream;
    private long _bytes;
    private long _requests;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _targets = new();

    private BodyCountingUpstream(LoopbackUpstream upstream) => _upstream = upstream;

    /// <summary>The base address, with a trailing <c>/</c>.</summary>
    public string Address => _upstream.Address;

    /// <summary>Body bytes received, over every request.</summary>
    public long Bytes => Interlocked.Read(ref _bytes);

    /// <summary>The method and target of each request that reached the upstream, in arrival order.</summary>
    public IReadOnlyCollection<string> Targets => _targets;

    /// <summary>Requests that reached the upstream.</summary>
    public long Requests => Interlocked.Read(ref _requests);

    public static async Task<BodyCountingUpstream> StartAsync(CancellationToken cancellationToken)
    {
        BodyCountingUpstream? self = null;
        var upstream = await LoopbackUpstream.StartAsync(
            async context =>
            {
                // The proxy always sends X-Forwarded-Proto; a request without it is a stranger (a dev tool probing new localhost ports was
                // seen doing GET /) and must not count as one the proxy let through.
                if (!context.Request.Headers.ContainsKey("X-Forwarded-Proto"))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                Interlocked.Increment(ref self!._requests);
                self._targets.Enqueue(context.Request.Method + " " + context.Request.Path);
                var buffer = new byte[16 * 1024];
                int read;
                while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
                {
                    Interlocked.Add(ref self._bytes, read);
                }

                await context.Response.WriteAsync("ok", context.RequestAborted);
            },
            kestrel: static kestrel =>
            {
                // Whatever the proxy lets through, this takes: a test about the proxy's limits can't be refused by the upstream's.
                kestrel.Limits.MaxRequestBodySize = null;
                kestrel.Limits.MaxRequestLineSize = 64 * 1024;
                kestrel.Limits.MaxRequestHeadersTotalSize = 256 * 1024;
                kestrel.Limits.MaxRequestHeaderCount = 256;
            },
            cancellationToken: cancellationToken);
        self = new BodyCountingUpstream(upstream);
        return self;
    }

    public ValueTask DisposeAsync() => _upstream.DisposeAsync();
}
