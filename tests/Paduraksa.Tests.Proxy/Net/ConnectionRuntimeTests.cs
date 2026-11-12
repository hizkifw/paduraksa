using Microsoft.Extensions.Logging;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.Net;

/// <summary>The connection stage's decisions on a compiled snapshot: the deny list, the cap and who is exempt from it, a full table
/// failing open, the QUIC read that takes no place, the counters, the rate-limited log, and 0 B on every path.</summary>
public sealed class ConnectionRuntimeTests
{
    private static readonly AccessListSpec Scanners = new()
    {
        Id = TestIds.AccessList("scanners"),
        Name = "scanners",
        Realm = "scanners",
        Deny = ["203.0.113.0/24", "2001:db8:dead::/48"],
        Allow = ["10.0.0.0/8"],
    };

    private static RuntimeSnapshot Snapshot(ConnectionsSpec? connections = null, TrustedProxySpec? proxies = null) =>
        Compile(new ProxyConfigModel
        {
            Upstreams = [Upstream("web", "http://127.0.0.1:1/")],
            AccessLists = [Scanners],
            Defaults = new DefaultsSpec { Connections = connections ?? ConnectionsSpec.Default, TrustedProxies = proxies ?? TrustedProxySpec.Off },
            Hosts = [ProxyHost("app", "app.test", Route("root", "web"))],
        });

    private static IpKey Ip(string address) => IpKey.Parse(address);

    [Fact]
    public void ACompiledConfigCarriesTheBuiltInCapAndNoList()
    {
        var policy = Snapshot().Connections;

        Assert.Equal(256, policy.MaxPerIp);
        Assert.True(policy.Deny.IsEmpty);
        Assert.False(policy.IsInert);
        Assert.True(RuntimeSnapshot.Bootstrap.Connections.IsInert);
    }

    [Fact]
    public void TheDenyListIsTheNamedAccessListsDenyAddressesAndNothingElse()
    {
        var policy = Snapshot(new ConnectionsSpec { MaxPerIp = 0, DenyList = Scanners.Id }).Connections;

        Assert.Equal(Scanners.Id, policy.DenyListId);
        Assert.Equal("scanners", policy.DenyListName);
        Assert.True(policy.Deny.Contains(Ip("203.0.113.9")));
        Assert.True(policy.Deny.Contains(Ip("2001:db8:dead:1::1")));
        Assert.False(policy.Deny.Contains(Ip("10.1.2.3"))); // the list's allow entry is a request rule
        Assert.False(policy.IsInert);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(ConnectionsSpec.MaxMaxPerIp + 1)]
    public void AnOutOfRangeCapOrAMissingListIsACompileError(int cap)
    {
        var model = new ProxyConfigModel { Defaults = new DefaultsSpec { Connections = new ConnectionsSpec { MaxPerIp = cap } } };
        Assert.Contains(SnapshotCompiler.Compile(model).Errors, e => e.Message.Contains("per-IP connection limit", StringComparison.Ordinal));

        var missing = new ProxyConfigModel { Defaults = new DefaultsSpec { Connections = new ConnectionsSpec { DenyList = TestIds.AccessList("gone") } } };
        Assert.Contains(SnapshotCompiler.Compile(missing).Errors, e => e.Message.Contains("deny list", StringComparison.Ordinal));
    }

    [Fact]
    public void ADeniedAddressIsRefusedWhoeverItIsAndNothingIsCounted()
    {
        ConnectionRuntime runtime = new(new ConnectionTable(64));
        // The listed address is also a trusted proxy: the deny list is the operator's explicit word, so it still refuses.
        var snapshot = Snapshot(new ConnectionsSpec { MaxPerIp = 1, DenyList = Scanners.Id }, new TrustedProxySpec { Mode = TrustedProxyMode.Generic, Proxies = ["203.0.113.5"] });

        Assert.Equal(ConnectionDecision.Denied, runtime.Admit(snapshot, Ip("203.0.113.5"), ConnectionTransport.Tcp, out _));
        Assert.Equal(ConnectionDecision.Denied, runtime.Admit(snapshot, Ip("2001:db8:dead:ffff::7"), ConnectionTransport.Quic, out _));
        Assert.Equal(ConnectionDecision.Counted, runtime.Admit(snapshot, Ip("198.51.100.1"), ConnectionTransport.Tcp, out _));
        Assert.Equal(1, runtime.Stats.DeniedTcp);
        Assert.Equal(1, runtime.Stats.DeniedQuic);
        Assert.Equal(1, runtime.Table.TotalCount());
    }

    [Fact]
    public void TheCapRefusesTheNextConnectionAndReleaseMakesRoom()
    {
        ConnectionRuntime runtime = new(new ConnectionTable(64));
        var snapshot = Snapshot(new ConnectionsSpec { MaxPerIp = 2 });
        var client = Ip("198.51.100.7");

        Assert.Equal(ConnectionDecision.Counted, runtime.Admit(snapshot, client, ConnectionTransport.Tcp, out var first));
        Assert.Equal(ConnectionDecision.Counted, runtime.Admit(snapshot, client, ConnectionTransport.Tcp, out _));
        Assert.Equal(ConnectionDecision.OverCap, runtime.Admit(snapshot, client, ConnectionTransport.Tcp, out _));
        Assert.Equal(ConnectionDecision.Counted, runtime.Admit(snapshot, Ip("198.51.100.8"), ConnectionTransport.Tcp, out _));
        Assert.Equal(3, runtime.Stats.Open);
        Assert.Equal(1, runtime.Stats.OverCapTcp);

        runtime.Release(first);
        Assert.Equal(2, runtime.Stats.Open);
        Assert.Equal(ConnectionDecision.Counted, runtime.Admit(snapshot, client, ConnectionTransport.Tcp, out _));
    }

    [Fact]
    public void AnIpv6Slash64IsOneAddressForTheCap()
    {
        ConnectionRuntime runtime = new(new ConnectionTable(64));
        var snapshot = Snapshot(new ConnectionsSpec { MaxPerIp = 1 });

        Assert.Equal(ConnectionDecision.Counted, runtime.Admit(snapshot, Ip("2001:db8:1:2::1"), ConnectionTransport.Tcp, out _));
        Assert.Equal(ConnectionDecision.OverCap, runtime.Admit(snapshot, Ip("2001:db8:1:2:aaaa::9"), ConnectionTransport.Tcp, out _));
        Assert.Equal(ConnectionDecision.Counted, runtime.Admit(snapshot, Ip("2001:db8:1:3::1"), ConnectionTransport.Tcp, out _));
    }

    [Theory]
    [InlineData(TrustedProxyMode.Generic, "203.0.113.5")]
    [InlineData(TrustedProxyMode.CloudflareTunnel, "203.0.113.5")]
    [InlineData(TrustedProxyMode.CloudflareProxy, "173.245.48.1")]
    public void ATrustedProxyIsNeverCountedBecauseEveryVisitorSharesIt(TrustedProxyMode mode, string proxy)
    {
        ConnectionRuntime runtime = new(new ConnectionTable(64));
        var snapshot = Snapshot(
            new ConnectionsSpec { MaxPerIp = 1 },
            new TrustedProxySpec { Mode = mode, Proxies = mode == TrustedProxyMode.CloudflareProxy ? [] : ["203.0.113.0/24"] });

        for (var i = 0; i < 50; i++)
        {
            Assert.Equal(ConnectionDecision.Admitted, runtime.Admit(snapshot, Ip(proxy), ConnectionTransport.Tcp, out _));
        }

        Assert.Equal(0, runtime.Table.TotalCount());
        Assert.Equal(0, runtime.Stats.OverCap);
        // The same address in Off mode is an ordinary client.
        var direct = Snapshot(new ConnectionsSpec { MaxPerIp = 1 });
        Assert.Equal(ConnectionDecision.Counted, runtime.Admit(direct, Ip(proxy), ConnectionTransport.Tcp, out _));
        Assert.Equal(ConnectionDecision.OverCap, runtime.Admit(direct, Ip(proxy), ConnectionTransport.Tcp, out _));
    }

    [Fact]
    public void ACapOfZeroIsOffAndAFullTableFailsOpenAndCounts()
    {
        ConnectionRuntime off = new(new ConnectionTable(64));
        var snapshot = Snapshot(new ConnectionsSpec { MaxPerIp = 0 });
        Assert.Equal(ConnectionDecision.Admitted, off.Admit(snapshot, Ip("198.51.100.7"), ConnectionTransport.Tcp, out _));
        Assert.Equal(0, off.Table.TotalCount());

        ConnectionRuntime tiny = new(new ConnectionTable(ConnectionTable.ProbeLength));
        var capped = Snapshot(new ConnectionsSpec { MaxPerIp = 1 });
        for (var i = 1; i <= ConnectionTable.ProbeLength; i++)
        {
            Assert.Equal(ConnectionDecision.Counted, tiny.Admit(capped, Ip($"198.51.100.{i}"), ConnectionTransport.Tcp, out _));
        }

        Assert.Equal(ConnectionDecision.Admitted, tiny.Admit(capped, Ip("192.0.2.200"), ConnectionTransport.Tcp, out _));
        Assert.Equal(ConnectionDecision.Admitted, tiny.Admit(capped, Ip("192.0.2.200"), ConnectionTransport.Tcp, out _));
        Assert.Equal(2, tiny.Stats.TableFull);
        Assert.Equal(ConnectionTable.ProbeLength, tiny.Stats.Open);
    }

    [Fact]
    public void AQuicHandshakeReadsTheCapAndTakesNoPlaceSoAFailedHandshakeLeaksNothing()
    {
        ConnectionRuntime runtime = new(new ConnectionTable(64));
        var snapshot = Snapshot(new ConnectionsSpec { MaxPerIp = 1, DenyList = Scanners.Id });
        var client = Ip("198.51.100.7");

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(ConnectionDecision.Admitted, runtime.Check(snapshot, client, ConnectionTransport.Quic));
        }

        Assert.Equal(0, runtime.Table.TotalCount());
        Assert.Equal(ConnectionDecision.Denied, runtime.Check(snapshot, Ip("203.0.113.77"), ConnectionTransport.Quic));

        // Once an established connection holds the place, the next handshake from the address is refused.
        Assert.Equal(ConnectionDecision.Counted, runtime.Admit(snapshot, client, ConnectionTransport.Quic, out var hash));
        Assert.Equal(ConnectionDecision.OverCap, runtime.Check(snapshot, client, ConnectionTransport.Quic));
        runtime.Release(hash);
        Assert.Equal(ConnectionDecision.Admitted, runtime.Check(snapshot, client, ConnectionTransport.Quic));
        Assert.Equal(1, runtime.Stats.OverCapQuic);
        Assert.Equal(1, runtime.Stats.DeniedQuic);
    }

    [Fact]
    public void RefusalsAreLoggedOncePerIntervalWithTheirCount()
    {
        List<string> lines = [];
        FakeTime time = new();
        ConnectionRuntime runtime = new(new ConnectionTable(64), new ListLogger(lines), time);
        var snapshot = Snapshot(new ConnectionsSpec { MaxPerIp = 1, DenyList = Scanners.Id });
        Assert.Equal(ConnectionDecision.Counted, runtime.Admit(snapshot, Ip("198.51.100.7"), ConnectionTransport.Tcp, out _));

        for (var i = 0; i < 5; i++)
        {
            _ = runtime.Admit(snapshot, Ip("198.51.100.7"), ConnectionTransport.Tcp, out _);
            _ = runtime.Admit(snapshot, Ip("203.0.113.9"), ConnectionTransport.Tcp, out _);
        }

        Assert.Equal(2, lines.Count);
        Assert.Contains("deny list 'scanners'", lines.Single(l => l.Contains("deny list", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("per-IP limit of 1", lines.Single(l => l.Contains("per-IP", StringComparison.Ordinal)), StringComparison.Ordinal);

        time.Advance(ConnectionRuntime.LogInterval + TimeSpan.FromSeconds(1));
        for (var i = 0; i < 3; i++)
        {
            _ = runtime.Admit(snapshot, Ip("203.0.113.9"), ConnectionTransport.Tcp, out _);
        }

        Assert.Equal(3, lines.Count);
        // The 4 refusals inside the interval and the one that finds it over are what the line says.
        Assert.StartsWith("Refused 5 connection(s)", lines[^1], StringComparison.Ordinal);
        Assert.Equal(8, runtime.Stats.DeniedTcp);
        Assert.Equal(5, runtime.Stats.OverCapTcp);
    }

    [Fact]
    public void EveryDecisionAllocatesNothing()
    {
        ConnectionRuntime runtime = new(new ConnectionTable(1_024));
        var snapshot = Snapshot(new ConnectionsSpec { MaxPerIp = 2, DenyList = Scanners.Id });
        var proxied = Snapshot(new ConnectionsSpec { MaxPerIp = 2 }, new TrustedProxySpec { Mode = TrustedProxyMode.Generic, Proxies = ["198.51.100.0/24"] });
        var inert = Snapshot(new ConnectionsSpec { MaxPerIp = 0 });
        var held = Ip("198.51.100.7");
        Assert.Equal(ConnectionDecision.Counted, runtime.Admit(snapshot, held, ConnectionTransport.Tcp, out _));
        Assert.Equal(ConnectionDecision.Counted, runtime.Admit(snapshot, held, ConnectionTransport.Tcp, out _));
        var state = (runtime, snapshot, proxied, inert, held, other: Ip("192.0.2.9"), denied: Ip("203.0.113.9"), v6: Ip("2001:db8:1:2::1"));

        AllocAssert.Zero(state, static s =>
        {
            // Counted and released in steady state: what a connection costs the table.
            if (s.runtime.Admit(s.snapshot, s.other, ConnectionTransport.Tcp, out var hash) == ConnectionDecision.Counted)
            {
                s.runtime.Release(hash);
            }

            if (s.runtime.Admit(s.snapshot, s.v6, ConnectionTransport.Tcp, out hash) == ConnectionDecision.Counted)
            {
                s.runtime.Release(hash);
            }
        }, "admit and release");
        AllocAssert.Zero(state, static s =>
        {
            _ = s.runtime.Admit(s.snapshot, s.held, ConnectionTransport.Tcp, out _); // over the cap
            _ = s.runtime.Admit(s.snapshot, s.denied, ConnectionTransport.Tcp, out _); // denied
            _ = s.runtime.Admit(s.proxied, s.held, ConnectionTransport.Tcp, out _); // a trusted proxy
            _ = s.runtime.Admit(s.inert, s.held, ConnectionTransport.Tcp, out _); // off
            _ = s.runtime.Check(s.snapshot, s.held, ConnectionTransport.Quic);
        }, "the refused, exempt and off paths");
    }

    private sealed class FakeTime : TimeProvider
    {
        private long _ticks = 1_000;

        public override long TimestampFrequency => 1_000;

        public override long GetTimestamp() => Volatile.Read(ref _ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, (long)by.TotalMilliseconds);
    }

    private sealed class ListLogger(List<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Assert.Equal(LogLevel.Information, logLevel);
            lines.Add(formatter(state, exception));
        }
    }
}
