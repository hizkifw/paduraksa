using System.Net;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.ProxyProtocol;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;
using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;
using static Paduraksa.Tests.Shared.ProxyProtocolTestData;

namespace Paduraksa.Tests.Proxy.Net;

/// <summary>The TCP middleware and the QUIC pieces around <see cref="ConnectionRuntime"/>: which address they judge, that a counted
/// connection is given back however its pipeline ends, and what a connection costs in allocations.</summary>
public sealed class ConnectionLimitMiddlewareTests
{
    private static readonly AccessListSpec Scanners = new()
    {
        Id = TestIds.AccessList("scanners"),
        Name = "scanners",
        Realm = "scanners",
        Deny = ["203.0.113.0/24"],
    };

    private static SnapshotStore Store(ConnectionsSpec connections, TrustedProxySpec? proxies = null)
    {
        SnapshotStore store = new();
        store.Publish(Bound(new ProxyConfigModel
        {
            Upstreams = [Upstream("web", "http://127.0.0.1:1/")],
            AccessLists = [Scanners],
            Defaults = new DefaultsSpec { Connections = connections, TrustedProxies = proxies ?? TrustedProxySpec.Off },
            Hosts = [ProxyHost("app", "app.test", Route("root", "web"))],
        }));
        return store;
    }

    private static TestConnection Connection(string address, string? proxyProtocolClient = null)
    {
        TestConnection connection = new(new IPEndPoint(IPAddress.Parse(address), 40_000));
        if (proxyProtocolClient is not null)
        {
            var client = IpKey.Parse(proxyProtocolClient);
            connection.Features.Set<IClientInfoFeature>(new ClientInfo(client, 50_000, ConnectionAddressSource.ProxyProtocol, IpKey.Parse(address), 40_000));
        }

        return connection;
    }

    [Fact]
    public async Task ACountedConnectionIsGivenBackWhenThePipelineEndsOrThrows()
    {
        using var store = Store(new ConnectionsSpec { MaxPerIp = 1 });
        ConnectionRuntime runtime = new(new ConnectionTable(64));
        TaskCompletionSource open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var middleware = new ConnectionLimitMiddleware(_ => open.Task, store, runtime);

        var first = middleware.OnConnectionAsync(Connection("192.0.2.1"));
        Assert.False(first.IsCompleted);
        Assert.Equal(1, runtime.Stats.Open);

        var refused = Connection("192.0.2.1");
        await middleware.OnConnectionAsync(refused);
        Assert.NotNull(refused.AbortReason);
        Assert.Equal(1, runtime.Stats.OverCapTcp);

        open.SetResult();
        await first;
        Assert.Equal(0, runtime.Stats.Open);
        Assert.Equal(0, runtime.Table.TotalCount());

        var failing = new ConnectionLimitMiddleware(static _ => Task.FromException(new InvalidOperationException("the pipeline failed")), store, runtime);
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.OnConnectionAsync(Connection("192.0.2.1")));
        Assert.Equal(0, runtime.Table.TotalCount());
        var again = Connection("192.0.2.1");
        await new ConnectionLimitMiddleware(static _ => Task.CompletedTask, store, runtime).OnConnectionAsync(again);
        Assert.Null(again.AbortReason);
    }

    [Fact]
    public async Task TheAddressJudgedIsTheProxyProtocolSourceNotTheLoadBalancer()
    {
        using var store = Store(new ConnectionsSpec { MaxPerIp = 1, DenyList = Scanners.Id });
        ConnectionRuntime runtime = new(new ConnectionTable(64));
        TaskCompletionSource open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var middleware = new ConnectionLimitMiddleware(_ => open.Task, store, runtime);

        // Two visitors behind one load balancer: each is its own address, and neither shares the balancer's count.
        var a = middleware.OnConnectionAsync(Connection("10.0.0.5", proxyProtocolClient: "192.0.2.1"));
        var b = middleware.OnConnectionAsync(Connection("10.0.0.5", proxyProtocolClient: "192.0.2.2"));
        Assert.Equal(2, runtime.Stats.Open);

        // A listed visitor is refused although the load balancer itself isn't listed, and the balancer's own address is judged on its own.
        var listed = Connection("10.0.0.5", proxyProtocolClient: "203.0.113.50");
        await middleware.OnConnectionAsync(listed);
        Assert.NotNull(listed.AbortReason);
        Assert.Equal(1, runtime.Stats.DeniedTcp);
        var sameVisitor = Connection("10.0.0.5", proxyProtocolClient: "192.0.2.1");
        await middleware.OnConnectionAsync(sameVisitor);
        Assert.NotNull(sameVisitor.AbortReason);

        open.SetResult();
        await Task.WhenAll(a, b);
        Assert.Equal(0, runtime.Stats.Open);
    }

    [Fact]
    public async Task AnInertStageAndATrustedProxyGoStraightOnWithoutTouchingTheTable()
    {
        ConnectionRuntime runtime = new(new ConnectionTable(64));
        using var inert = Store(new ConnectionsSpec { MaxPerIp = 0 });
        using var proxied = Store(new ConnectionsSpec { MaxPerIp = 1 }, new TrustedProxySpec { Mode = TrustedProxyMode.Generic, Proxies = ["10.0.0.0/8"] });
        var passed = 0;
        Task Next(ConnectionContext _)
        {
            passed++;
            return Task.CompletedTask;
        }

        for (var i = 0; i < 5; i++)
        {
            await new ConnectionLimitMiddleware(Next, inert, runtime).OnConnectionAsync(Connection("192.0.2.1"));
            await new ConnectionLimitMiddleware(Next, proxied, runtime).OnConnectionAsync(Connection("10.1.2.3"));
        }

        Assert.Equal(10, passed);
        Assert.Equal(0, runtime.Table.TotalCount());
        Assert.Equal(0, runtime.Stats.OverCap);
    }

    [Fact]
    public void WhatAConnectionCostsIsZeroExceptTheContinuationThatGivesACountedOneBack()
    {
        ConnectionRuntime runtime = new(new ConnectionTable(1_024));
        using var inert = Store(new ConnectionsSpec { MaxPerIp = 0 });
        using var proxied = Store(new ConnectionsSpec { MaxPerIp = 1 }, new TrustedProxySpec { Mode = TrustedProxyMode.Generic, Proxies = ["10.0.0.0/8"] });
        using var capped = Store(new ConnectionsSpec { MaxPerIp = 4, DenyList = Scanners.Id });
        var connection = Connection("192.0.2.1");
        var edge = Connection("10.1.2.3");
        ConnectionDelegate done = static _ => Task.CompletedTask;

        var state = (connection, edge, off: new ConnectionLimitMiddleware(done, inert, runtime), exempt: new ConnectionLimitMiddleware(done, proxied, runtime), counted: new ConnectionLimitMiddleware(done, capped, runtime));

        AllocAssert.Zero(state, static s => _ = s.off.OnConnectionAsync(s.connection), "the stage is off");
        AllocAssert.Zero(state, static s => _ = s.exempt.OnConnectionAsync(s.edge), "a trusted proxy (every Cloudflare connection)");
        // A counted connection whose pipeline has already finished: the continuation is only needed when the pipeline is still running.
        AllocAssert.Zero(state, static s => _ = s.counted.OnConnectionAsync(s.connection), "a counted connection whose pipeline completes at once");
    }

    [Fact]
    public async Task ACountedConnectionThatStaysOpenCostsOnlyItsReleaseContinuation()
    {
        ConnectionRuntime runtime = new(new ConnectionTable(1_024));
        using var capped = Store(new ConnectionsSpec { MaxPerIp = 1_000 });
        TaskCompletionSource open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConnectionLimitMiddleware middleware = new(_ => open.Task, capped, runtime);
        var connection = Connection("192.0.2.1");
        List<Task> held = [];
        for (var i = 0; i < 200; i++)
        {
            held.Add(middleware.OnConnectionAsync(connection)); // warm up the state machine's code
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        const int Rounds = 500;
        for (var i = 0; i < Rounds; i++)
        {
            held.Add(middleware.OnConnectionAsync(connection));
        }

        var perConnection = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)Rounds;
        open.SetResult();
        await Task.WhenAll(held);

        // The box of the async method that waits for the rest of the pipeline: the one allocation a counted connection costs here
        // (177 B measured in a Release build, about 200 in Debug, whose state machines are classes). Kestrel's own cost of a
        // connection is thousands of times that.
        Assert.InRange(perConnection, 1, 400);
        Assert.Equal(0, runtime.Table.TotalCount());
    }

    [Fact]
    public async Task TheQuicGateReadsAndTheEstablishedConnectionCounts()
    {
        using var store = Store(new ConnectionsSpec { MaxPerIp = 1, DenyList = Scanners.Id });
        ConnectionRuntime runtime = new(new ConnectionTable(64));
        var gate = QuicConnectionLimits.CreateGate(store, runtime, QuicClientInfoGate.Gate);
        IPEndPoint client = new(IPAddress.Parse("192.0.2.1"), 4433);
        IPEndPoint listed = new(IPAddress.Parse("203.0.113.9"), 4433);

        // Any number of handshakes pass the read: none takes a place, so one that never completes leaks nothing.
        for (var i = 0; i < 5; i++)
        {
            Assert.True(gate(client, new DefaultConnectionContext()));
        }

        Assert.Equal(0, runtime.Table.TotalCount());
        Assert.False(gate(listed, new DefaultConnectionContext()));
        Assert.Equal(1, runtime.Stats.DeniedQuic);

        TaskCompletionSource open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        QuicConnectionLimitMiddleware middleware = new(_ => open.Task, store, runtime);
        FakeMultiplexed established = new(client);
        var serving = middleware.OnConnectionAsync(established);
        Assert.Equal(1, runtime.Stats.Open);

        // Now the address holds its place: the next handshake is refused, and an established second connection is aborted.
        Assert.False(gate(client, new DefaultConnectionContext()));
        FakeMultiplexed second = new(client);
        await middleware.OnConnectionAsync(second);
        Assert.NotNull(second.AbortedWith);
        Assert.Equal(2, runtime.Stats.OverCapQuic);

        open.SetResult();
        await serving;
        Assert.Equal(0, runtime.Stats.Open);
        Assert.True(gate(client, new DefaultConnectionContext()));
    }

    [Fact]
    public async Task TheQuicMiddlewareTakesTheAddressFromTheClientInfoTheHandshakePublished()
    {
        using var store = Store(new ConnectionsSpec { MaxPerIp = 1 });
        ConnectionRuntime runtime = new(new ConnectionTable(64));
        TaskCompletionSource open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        QuicConnectionLimitMiddleware middleware = new(_ => open.Task, store, runtime);
        FakeMultiplexed established = new(new IPEndPoint(IPAddress.Parse("::ffff:192.0.2.1"), 4433));
        established.Features.Set<IClientInfoFeature>(ClientInfo.FromEndPoint(new IPEndPoint(IPAddress.Parse("192.0.2.1"), 4433)));

        var serving = middleware.OnConnectionAsync(established);
        // TCP reports the IPv4-mapped form and QUIC plain IPv4: one address either way.
        var tcp = Connection("192.0.2.1");
        await new ConnectionLimitMiddleware(static _ => Task.CompletedTask, store, runtime).OnConnectionAsync(tcp);
        Assert.NotNull(tcp.AbortReason);

        open.SetResult();
        await serving;
    }

    private sealed class FakeMultiplexed(IPEndPoint remote) : MultiplexedConnectionContext
    {
        private readonly FeatureCollection _features = new();

        public override string ConnectionId { get; set; } = "quic-test";

        public override IFeatureCollection Features => _features;

        public override IDictionary<object, object?> Items { get; set; } = new Dictionary<object, object?>();

        public override EndPoint? RemoteEndPoint { get; set; } = remote;

        public ConnectionAbortedException? AbortedWith { get; private set; }

        public override void Abort(ConnectionAbortedException abortReason) => AbortedWith = abortReason;

        public override void Abort() => AbortedWith = new ConnectionAbortedException("aborted");

        public override ValueTask<ConnectionContext?> AcceptAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<ConnectionContext?>(null);

        public override ValueTask<ConnectionContext> ConnectAsync(IFeatureCollection? features = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
