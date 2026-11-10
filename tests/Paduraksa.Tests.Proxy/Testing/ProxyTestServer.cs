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
using Paduraksa.Proxy.Logging;
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
        var addresses = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
        var address = Assert.Single(addresses.Addresses);
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

    /// <summary>The access log the middleware fills; nothing drains it in a test app, so tests read it themselves.</summary>
    public AccessLogRing AccessLog => _host.Services.GetRequiredService<AccessLogRing>();

    /// <summary>
    /// Set when started with <see cref="ProxyTestServerOptions.MeasureGate"/>: bytes allocated by
    /// <see cref="GateMiddleware.Decide"/>.
    /// </summary>
    public GateStats? GateStats { get; }

    /// <summary>
    /// Builds and starts the server. <paramref name="initial"/> is published before YARP exists, like the startup
    /// publish. <paramref name="options"/> defaults to <see cref="ProxyTestServerOptions.Default"/>.
    /// </summary>
    public static async Task<ProxyTestServer> StartAsync(ProxyConfigModel? initial = null, ProxyTestServerOptions? options = null)
    {
        options ??= ProxyTestServerOptions.Default;
        var compilerOptions = options.CompilerOptions;
        var protocols = options.Protocols;
        var publisher = options.Publisher ?? new SnapshotPublisher(new SnapshotStore(), options: options.PublisherOptions);
        var initialPublish = initial is null ? null : publisher.PublishAsync(TestSnapshots.Compile(initial, compilerOptions));
        var stats = options.MeasureGate ? new GateStats() : null;

        var host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseKestrel(kestrel =>
                {
                    kestrel.AddServerHeader = false;
                    kestrel.Listen(IPAddress.Loopback, 0, listen =>
                    {
                        listen.Protocols = protocols;
                        if (options.ClientInfo)
                        {
                            listen.UseClientInfo();
                        }
                    });
                })
                .ConfigureServices(s =>
                {
                    s.AddPaduraksaProxy(publisher, proofOfWork: options.ProofOfWork);
                    s.Configure<TransportFailureRateHealthPolicyOptions>(o =>
                    {
                        o.MinimalTotalCountThreshold = 2;
                        o.DefaultFailureRateLimit = 0.3;
                    });
                    options.Services?.Invoke(s);
                })
                .Configure(app =>
                {
                    // The public pipeline, in its order: the access log wraps everything, the gate decides.
                    app.Use(app.ApplicationServices.GetRequiredService<AccessLogMiddleware>().InvokeAsync);
                    var gate = app.ApplicationServices.GetRequiredService<GateMiddleware>();
                    if (!options.WithoutGate)
                    {
                        if (stats is null)
                        {
                            app.Use(gate.InvokeAsync);
                        }
                        else
                        {
                            app.Use((context, next) =>
                            {
                                // Only the gate uses this dictionary: empty means a new connection (H1) or a new
                                // stream (H2), where the gate creates its pooled state once. Those are counted apart.
                                var fresh = context.Features.Get<IPersistentStateFeature>()?.State.Count == 0;
                                var before = GC.GetAllocatedBytesForCurrentThread();
                                var decision = gate.Decide(context);
                                stats.Record(GC.GetAllocatedBytesForCurrentThread() - before, fresh);
                                return GateMiddleware.CompleteAsync(context, decision, next);
                            });
                        }
                    }

                    if (options.AfterGate is not null)
                    {
                        app.Use(options.AfterGate);
                    }

                    app.UseRouting();
                    app.UseEndpoints(static endpoints => endpoints.MapPaduraksaProxy());
                }))
            .Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        if (initialPublish is not null)
        {
            var result = await initialPublish;
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

/// <summary>How <see cref="ProxyTestServer.StartAsync"/> builds the server. Every property defaults to off.</summary>
internal sealed record ProxyTestServerOptions
{
    public static ProxyTestServerOptions Default { get; } = new();

    /// <summary>Extra service registrations, after the proxy's own.</summary>
    public Action<IServiceCollection>? Services { get; init; }

    /// <summary>A middleware placed between the gate and routing.</summary>
    public Func<HttpContext, RequestDelegate, Task>? AfterGate { get; init; }

    /// <summary>Wraps the gate to record what <see cref="GateMiddleware.Decide"/> allocates (<see cref="ProxyTestServer.GateStats"/>).</summary>
    public bool MeasureGate { get; init; }

    /// <summary>No gate: routing matches YARP's internal routes by itself.</summary>
    public bool WithoutGate { get; init; }

    /// <summary>Installs the stage-A ClientInfo connection middleware, as the public listeners do.</summary>
    public bool ClientInfo { get; init; }

    /// <summary>The listener's protocols; <see cref="HttpProtocols.Http2"/> means cleartext HTTP/2 only.</summary>
    public HttpProtocols Protocols { get; init; } = HttpProtocols.Http1;

    /// <summary>Used for every config the server compiles.</summary>
    public SnapshotCompilerOptions? CompilerOptions { get; init; }

    /// <summary>An existing publisher to attach; otherwise a new one over a new store.</summary>
    public SnapshotPublisher? Publisher { get; init; }

    /// <summary>Options of the new publisher (ignored with <see cref="Publisher"/>).</summary>
    public SnapshotPublisherOptions? PublisherOptions { get; init; }

    /// <summary>The proof-of-work runtime the gate's verify endpoint uses; keep it the same one the compiler options carry.</summary>
    public ProofOfWorkRuntime? ProofOfWork { get; init; }
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

                    var raw = context.Features.Get<IHttpRequestFeature>()!.RawTarget;
                    var query = raw.IndexOf('?', StringComparison.Ordinal);
                    context.Response.Headers["x-query"] = query < 0 ? string.Empty : raw[query..];
                    return context.Response.WriteAsync(query < 0 ? raw : raw[..query]);
                })))
            .Build();
        await _host.StartAsync();
        var address = Assert.Single(_host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
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
