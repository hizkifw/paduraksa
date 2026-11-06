using System.Globalization;
using System.Net;
using System.Net.Sockets;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Paduraksa.Proxy;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Yarp;

using Yarp.ReverseProxy.Health;

namespace Paduraksa.Tests.Proxy.Testing;

/// <summary>
/// The proxy core on real Kestrel + YARP, wired like the public app (generic host, explicit pipeline): gate →
/// routing → <c>MapPaduraksaProxy</c>. One plain-HTTP H1 listener on loopback.
/// </summary>
internal sealed class ProxyTestServer : IAsyncDisposable
{
    private readonly IHost _host;

    private ProxyTestServer(IHost host, SnapshotPublisher publisher, GateStats? stats, HttpProtocols protocols, SnapshotCompilerOptions? compilerOptions)
    {
        _host = host;
        CompilerOptions = compilerOptions;
        Http2 = protocols == HttpProtocols.Http2;
        Publisher = publisher;
        GateStats = stats;
        IServerAddressesFeature addresses = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
        string address = Assert.Single(addresses.Addresses);
        BaseAddress = new Uri(address);
    }

    public SnapshotPublisher Publisher { get; }

    /// <summary>Used for every config this server compiles.</summary>
    public SnapshotCompilerOptions? CompilerOptions { get; }

    public SnapshotStore Store => Publisher.Store;

    public Uri BaseAddress { get; }

    /// <summary>The listener speaks cleartext HTTP/2 only (h2c, prior knowledge).</summary>
    public bool Http2 { get; }

    public IServiceProvider Services => _host.Services;

    /// <summary>Set when started with <c>measureGate</c>: bytes allocated by <see cref="GateMiddleware.Decide"/>.</summary>
    public GateStats? GateStats { get; }

    /// <summary>
    /// Builds and starts the server. <paramref name="initial"/> is published before YARP exists, like the startup
    /// publish; <paramref name="afterGate"/> is a middleware placed between the gate and routing. Without the gate
    /// (<paramref name="withoutGate"/>), routing matches YARP's internal routes by itself. <paramref name="clientInfo"/>
    /// installs the stage-A ClientInfo connection middleware, as the public listeners do.
    /// </summary>
    public static async Task<ProxyTestServer> StartAsync(
        ProxyConfigModel? initial = null,
        Action<IServiceCollection>? services = null,
        Func<HttpContext, RequestDelegate, Task>? afterGate = null,
        bool measureGate = false,
        SnapshotPublisherOptions? options = null,
        SnapshotPublisher? publisher = null,
        HttpProtocols protocols = HttpProtocols.Http1,
        bool withoutGate = false,
        SnapshotCompilerOptions? compilerOptions = null,
        bool clientInfo = false)
    {
        publisher ??= new SnapshotPublisher(new SnapshotStore(), options: options);
        Task<PublishResult>? initialPublish = initial is null ? null : publisher.PublishAsync(TestSnapshots.Compile(initial, compilerOptions));
        GateStats? stats = measureGate ? new GateStats() : null;

        IHost host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseKestrel(kestrel =>
                {
                    kestrel.AddServerHeader = false;
                    kestrel.Listen(IPAddress.Loopback, 0, listen =>
                    {
                        listen.Protocols = protocols;
                        if (clientInfo)
                        {
                            listen.UseClientInfo();
                        }
                    });
                })
                .ConfigureServices(s =>
                {
                    s.AddPaduraksaProxy(publisher);
                    s.Configure<TransportFailureRateHealthPolicyOptions>(o =>
                    {
                        o.MinimalTotalCountThreshold = 2;
                        o.DefaultFailureRateLimit = 0.3;
                    });
                    services?.Invoke(s);
                })
                .Configure(app =>
                {
                    GateMiddleware gate = app.ApplicationServices.GetRequiredService<GateMiddleware>();
                    if (withoutGate)
                    {
                    }
                    else if (stats is null)
                    {
                        app.Use(gate.InvokeAsync);
                    }
                    else
                    {
                        app.Use((context, next) =>
                        {
                            // Only the gate uses this dictionary: empty means a new connection (H1) or a new stream
                            // (H2), where the gate creates its pooled state once. Those are counted apart.
                            bool fresh = context.Features.Get<IPersistentStateFeature>()?.State.Count == 0;
                            long before = GC.GetAllocatedBytesForCurrentThread();
                            GateDecision decision = gate.Decide(context);
                            stats.Record(GC.GetAllocatedBytesForCurrentThread() - before, fresh);
                            return GateMiddleware.CompleteAsync(context, decision, next);
                        });
                    }

                    if (afterGate is not null)
                    {
                        app.Use(afterGate);
                    }

                    app.UseRouting();
                    app.UseEndpoints(static endpoints => endpoints.MapPaduraksaProxy());
                }))
            .Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        if (initialPublish is not null)
        {
            PublishResult result = await initialPublish;
            Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        }

        return new ProxyTestServer(host, publisher, stats, protocols, compilerOptions);
    }

    public async Task<PublishResult> PublishAsync(ProxyConfigModel model) =>
        await Publisher.PublishAsync(TestSnapshots.Compile(model, CompilerOptions), TestContext.Current.CancellationToken);

    public static HttpClient CreateClient(int maxConnections = 16) => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        MaxConnectionsPerServer = maxConnections,
        PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
    })
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    public HttpRequestMessage Request(string host, string pathAndQuery = "/", HttpMethod? method = null)
    {
        HttpRequestMessage request = new(method ?? HttpMethod.Get, new Uri(BaseAddress, pathAndQuery));
        request.Headers.Host = host;
        if (Http2)
        {
            request.Version = HttpVersion.Version20;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }

        return request;
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync(CancellationToken.None);
        _host.Dispose();
        await Publisher.DisposeAsync();
        Store.Dispose();
    }
}

/// <summary>
/// Per-request allocation of the gate's decision, summed across threads. Requests that had to create the pooled
/// per-connection/per-stream state are counted in <see cref="FreshState"/> and excluded from the byte totals.
/// </summary>
internal sealed class GateStats
{
    private long _requests;
    private long _fresh;
    private long _bytes;
    private long _nonZero;
    private long _max;

    public long FreshState => Volatile.Read(ref _fresh);

    public long Requests => Volatile.Read(ref _requests);

    public long Bytes => Volatile.Read(ref _bytes);

    public long NonZero => Volatile.Read(ref _nonZero);

    public long Max => Volatile.Read(ref _max);

    public void Record(long bytes, bool freshState)
    {
        Interlocked.Increment(ref _requests);
        if (freshState)
        {
            Interlocked.Increment(ref _fresh);
            return;
        }

        if (bytes == 0)
        {
            return;
        }

        Interlocked.Add(ref _bytes, bytes);
        Interlocked.Increment(ref _nonZero);
        long seen;
        while (bytes > (seen = Volatile.Read(ref _max)) && Interlocked.CompareExchange(ref _max, bytes, seen) != seen)
        {
        }
    }

    public void Reset()
    {
        Volatile.Write(ref _requests, 0);
        Volatile.Write(ref _fresh, 0);
        Volatile.Write(ref _bytes, 0);
        Volatile.Write(ref _nonZero, 0);
        Volatile.Write(ref _max, 0);
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Bytes} B over {Requests} requests ({NonZero} non-zero, max {Max} B; {FreshState} on fresh connections/streams)");
}

/// <summary>
/// An in-process upstream: answers 200 with the request path <b>as received</b> (raw target before <c>?</c>) as the
/// body, so a test sees which destination prefix YARP used and how the path was encoded, plus the query in
/// <c>x-query</c>; or 500 under <c>/bad</c>.
/// </summary>
public sealed class TestUpstream : IAsyncLifetime
{
    private IHost? _host;

    public int Port { get; private set; }

    /// <summary>A loopback port nothing listens on.</summary>
    public int DeadPort { get; } = FreePort();

    /// <summary>Destination address with a path prefix, e.g. <c>http://127.0.0.1:1234/a/</c>.</summary>
    public string Address(string prefix) => string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{Port}/{prefix}/");

    public async ValueTask InitializeAsync()
    {
        _host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0))
                .Configure(static app => app.Run(static context =>
                {
                    if (context.Request.Path.Value!.Contains("/bad", StringComparison.Ordinal))
                    {
                        context.Response.StatusCode = 500;
                        return Task.CompletedTask;
                    }

                    string raw = context.Features.Get<IHttpRequestFeature>()!.RawTarget;
                    int query = raw.IndexOf('?', StringComparison.Ordinal);
                    context.Response.Headers["x-query"] = query < 0 ? string.Empty : raw[query..];
                    return context.Response.WriteAsync(query < 0 ? raw : raw[..query]);
                })))
            .Build();
        await _host.StartAsync();
        string address = Assert.Single(_host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        Port = new Uri(address).Port;
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync(CancellationToken.None);
            _host.Dispose();
        }
    }

    private static int FreePort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
