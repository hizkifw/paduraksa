// Linked into Paduraksa.Tests.Proxy and Paduraksa.Tests.Integration (<Compile Include Link>), like HeaderEchoUpstreamHost.
using System.Net;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Paduraksa.Tests.Shared;

/// <summary>
/// A loopback upstream that reads every request body to the end and counts the requests and the bytes it was given, so a
/// test can say what a proxy forwarded and what it kept from it.
/// </summary>
internal sealed class BodyCountingUpstream : IAsyncDisposable
{
    private readonly IHost _host;
    private long _bytes;
    private long _requests;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _targets = new();

    private BodyCountingUpstream(IHost host, string address)
    {
        _host = host;
        Address = address;
    }

    /// <summary>The base address, with a trailing <c>/</c>.</summary>
    public string Address { get; }

    /// <summary>Body bytes received, over every request.</summary>
    public long Bytes => Interlocked.Read(ref _bytes);

    /// <summary>The method and target of each request that reached the upstream, in arrival order.</summary>
    public IReadOnlyCollection<string> Targets => _targets;

    /// <summary>Requests that reached the upstream.</summary>
    public long Requests => Interlocked.Read(ref _requests);

    public static async Task<BodyCountingUpstream> StartAsync(CancellationToken cancellationToken)
    {
        BodyCountingUpstream? self = null;
        var host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseKestrel(kestrel =>
                {
                    // Whatever the proxy lets through, this takes: a test about the proxy's limits can't be refused by the upstream's.
                    kestrel.Limits.MaxRequestBodySize = null;
                    kestrel.Limits.MaxRequestLineSize = 64 * 1024;
                    kestrel.Limits.MaxRequestHeadersTotalSize = 256 * 1024;
                    kestrel.Limits.MaxRequestHeaderCount = 256;
                    kestrel.Listen(IPAddress.Loopback, 0);
                })
                .Configure(app => app.Run(async context =>
                {
                    Interlocked.Increment(ref self!._requests);
                    self._targets.Enqueue(context.Request.Method + " " + context.Request.Path);
                    var buffer = new byte[16 * 1024];
                    int read;
                    while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
                    {
                        Interlocked.Add(ref self._bytes, read);
                    }

                    await context.Response.WriteAsync("ok", context.RequestAborted);
                })))
            .Build();
        await host.StartAsync(cancellationToken);
        var address = Assert.Single(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        self = new BodyCountingUpstream(host, address + "/");
        return self;
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync(CancellationToken.None);
        _host.Dispose();
    }
}
