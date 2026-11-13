using System.Net.Sockets;
using System.Text;

using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Net;
using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Hosting;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Integration.Proxy.PublicProxyTests;

namespace Paduraksa.Tests.Integration.Proxy;

/// <summary>
/// The connection stage on the real public listeners of a real host: a denied address is closed before TLS and the cap closes the
/// next connection from one address, with trusted proxies exempt, and the limits the options state are the ones Kestrel runs with.
/// </summary>
public sealed class PublicConnectionLimitsTests : IAsyncLifetime
{
    private static readonly AccessListSpec Loopback = new()
    {
        Id = TestIds.AccessList("loopback"),
        Name = "loopback",
        Realm = "loopback",
        Deny = ["127.0.0.0/8", "::1"],
    };

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private EdgeUpstream? _upstream;

    public async ValueTask InitializeAsync() => _upstream = await EdgeUpstream.StartAsync();

    public async ValueTask DisposeAsync()
    {
        if (_upstream is not null)
        {
            await _upstream.DisposeAsync();
        }
    }

    private ProxyConfigModel App(ConnectionsSpec connections, TrustedProxySpec? proxies = null) => new()
    {
        Upstreams = [new UpstreamSpec { Id = TestIds.Upstream("echo"), Destinations = [new(TestIds.Destination("echo-1"), _upstream!.Address)] }],
        AccessLists = [Loopback],
        Defaults = new DefaultsSpec { Connections = connections, TrustedProxies = proxies ?? TrustedProxySpec.Off },
        Hosts = [new HostSpec { Id = TestIds.Host("app"), Names = ["app.test"], Routes = [new RouteSpec { Id = TestIds.Route("app-root"), Upstream = TestIds.Upstream("echo") }] }],
    };

    private static async Task<PaduraksaHost> StartAsync(Dictionary<string, string?>? settings = null)
    {
        var host = TestHost.Create(settings: settings);
        await host.StartAsync(TestContext.Current.CancellationToken);
        return host;
    }

    private static async Task PublishAsync(PaduraksaHost host, ProxyConfigModel model)
    {
        var result = await host.Shared.SnapshotPublisher.PublishAsync(Compile(host, model), TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
    }

    private static async Task<TcpClient> ConnectAsync(PaduraksaHost host, string scheme = "http")
    {
        TcpClient tcp = new();
        await tcp.ConnectAsync(TestHost.Endpoint(host, HostRole.Public, scheme).Host, TestHost.Endpoint(host, HostRole.Public, scheme).Port, TestContext.Current.CancellationToken);
        return tcp;
    }

    private static async Task<string> GetAsync(TcpClient tcp)
    {
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: app.test\r\n\r\n"), TestContext.Current.CancellationToken);
        var buffer = new byte[2048];
        var read = await stream.ReadAsync(buffer, TestContext.Current.CancellationToken).AsTask().WaitAsync(Wait, TestContext.Current.CancellationToken);
        return Encoding.ASCII.GetString(buffer, 0, read);
    }

    /// <summary>True when the server closes the connection without a byte: it read nothing, so it was refused before it spoke.</summary>
    private static async Task<bool> ClosedUnansweredAsync(TcpClient tcp)
    {
        var buffer = new byte[16];
        try
        {
            return await tcp.GetStream().ReadAsync(buffer, TestContext.Current.CancellationToken).AsTask().WaitAsync(Wait, TestContext.Current.CancellationToken) == 0;
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            return true; // reset
        }
    }

    /// <summary>Connects and reports whether the server refused the connection without a byte; on loopback the refusal may reset it
    /// before the connect call returns, which is a refusal too.</summary>
    private static async Task<bool> RefusedAsync(PaduraksaHost host, string scheme = "http")
    {
        try
        {
            using var tcp = await ConnectAsync(host, scheme);
            return await ClosedUnansweredAsync(tcp);
        }
        catch (SocketException)
        {
            return true;
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            // Poll step of a bounded wait.
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }

    [Fact]
    public async Task ADeniedAddressIsClosedBeforeTlsOnBothListenersAndServedAgainOnceTheListIsGone()
    {
        await using var host = await StartAsync();
        var stats = host.Shared.PublicListeners.Connections.Stats;
        await PublishAsync(host, App(new ConnectionsSpec { MaxPerIp = 0, DenyList = Loopback.Id }));

        // A TLS server waits for a ClientHello and a plain one for a request: the one that closes first, unasked, refused the peer.
        Assert.True(await RefusedAsync(host));
        Assert.True(await RefusedAsync(host, "https"));

        Assert.Equal(2, stats.DeniedTcp);
        Assert.Equal(0, host.Shared.PublicListeners.Selector.Counters.UnknownServerNameTcp);

        await PublishAsync(host, App(new ConnectionsSpec { MaxPerIp = 0 }));
        using var allowed = await ConnectAsync(host);
        Assert.StartsWith("HTTP/1.1 200", await GetAsync(allowed), StringComparison.Ordinal);
        Assert.Equal(2, stats.DeniedTcp);
    }

    [Fact]
    public async Task TheCapClosesTheNextConnectionFromOneAddressAndAClosedOneMakesRoom()
    {
        await using var host = await StartAsync();
        var stats = host.Shared.PublicListeners.Connections.Stats;
        await PublishAsync(host, App(new ConnectionsSpec { MaxPerIp = 2 }));

        using var first = await ConnectAsync(host);
        using var second = await ConnectAsync(host);
        Assert.StartsWith("HTTP/1.1 200", await GetAsync(first), StringComparison.Ordinal);
        Assert.StartsWith("HTTP/1.1 200", await GetAsync(second), StringComparison.Ordinal);
        Assert.Equal(2, stats.Open);

        Assert.True(await RefusedAsync(host));
        Assert.True(await RefusedAsync(host, "https")); // the cap is the table's: both listeners count into it

        Assert.Equal(2, stats.OverCapTcp);

        first.Close();
        await WaitForAsync(() => stats.Open == 1);
        using var fourth = await ConnectAsync(host);
        Assert.StartsWith("HTTP/1.1 200", await GetAsync(fourth), StringComparison.Ordinal);
        Assert.Equal(2, stats.Open);
    }

    [Fact]
    public async Task ATrustedProxyIsNotCountedSoItsVisitorsAreNeverRefusedForSharingIt()
    {
        await using var host = await StartAsync();
        var stats = host.Shared.PublicListeners.Connections.Stats;
        await PublishAsync(host, App(
            new ConnectionsSpec { MaxPerIp = 1 },
            new TrustedProxySpec { Mode = TrustedProxyMode.Generic, Proxies = ["127.0.0.0/8", "::1"] }));

        List<TcpClient> held = [];
        try
        {
            for (var i = 0; i < 5; i++)
            {
                var tcp = await ConnectAsync(host);
                held.Add(tcp);
                Assert.StartsWith("HTTP/1.1 200", await GetAsync(tcp), StringComparison.Ordinal);
            }
        }
        finally
        {
            held.ForEach(static t => t.Dispose());
        }

        Assert.Equal(0, stats.OverCap);

        // The server sees the clients' closes after they are sent; counting before it has is a race (the count reads 1 on a loaded machine).
        await WaitForAsync(() => stats.Open == 0);
    }

    [Fact]
    public async Task TheOptionsAreTheLimitsKestrelRunsWith()
    {
        await using var host = await StartAsync(new()
        {
            ["Paduraksa:Limits:RequestHeadersTimeout"] = "00:00:07",
            ["Paduraksa:Limits:KeepAliveTimeout"] = "00:03:00",
            ["Paduraksa:Limits:MinRequestBodyRate"] = "0",
            ["Paduraksa:Limits:MinResponseRate"] = "500",
            ["Paduraksa:Limits:MinResponseGrace"] = "00:00:20",
            ["Paduraksa:Limits:MaxConnections"] = "123",
            ["Paduraksa:Limits:MaxUpgradedConnections"] = "0",
            ["Paduraksa:Limits:Http2MaxStreamsPerConnection"] = "17",
        });

        var limits = host.Public.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.Limits;

        Assert.Equal(TimeSpan.FromSeconds(7), limits.RequestHeadersTimeout);
        Assert.Equal(TimeSpan.FromMinutes(3), limits.KeepAliveTimeout);
        Assert.Null(limits.MinRequestBodyDataRate);
        Assert.Equal(500, limits.MinResponseDataRate!.BytesPerSecond);
        Assert.Equal(TimeSpan.FromSeconds(20), limits.MinResponseDataRate.GracePeriod);
        Assert.Equal(123, limits.MaxConcurrentConnections);
        Assert.Null(limits.MaxConcurrentUpgradedConnections);
        Assert.Equal(17, limits.Http2.MaxStreamsPerConnection);
    }

    [Fact]
    public async Task WithoutSettingsTheDefaultsAreTheLimitsKestrelRunsWith()
    {
        await using var host = await StartAsync();
        var limits = host.Public.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.Limits;
        var expected = ServerLimits.Default;

        Assert.Equal(expected.RequestHeadersTimeout, limits.RequestHeadersTimeout);
        Assert.Equal(expected.KeepAliveTimeout, limits.KeepAliveTimeout);
        Assert.Equal(expected.MinRequestBodyRate, limits.MinRequestBodyDataRate!.BytesPerSecond);
        Assert.Equal(expected.MinRequestBodyGrace, limits.MinRequestBodyDataRate.GracePeriod);
        Assert.Equal(expected.MinResponseRate, limits.MinResponseDataRate!.BytesPerSecond);
        Assert.Equal(expected.MaxConnections, limits.MaxConcurrentConnections);
        Assert.Equal(expected.MaxUpgradedConnections, limits.MaxConcurrentUpgradedConnections);
        Assert.Equal(expected.Http2MaxStreamsPerConnection, limits.Http2.MaxStreamsPerConnection);
    }

    [Theory]
    [InlineData("Paduraksa:Limits:RequestHeadersTimeout", "00:00:00", "RequestHeadersTimeout must be positive")]
    [InlineData("Paduraksa:Limits:KeepAliveTimeout", "-00:00:01", "KeepAliveTimeout must be positive")]
    [InlineData("Paduraksa:Limits:TlsHandshakeTimeout", "00:00:00", "TlsHandshakeTimeout must be positive")]
    [InlineData("Paduraksa:Limits:MinRequestBodyRate", "-1", "can't be negative")]
    [InlineData("Paduraksa:Limits:MinResponseGrace", "00:00:01", "MinResponseGrace must be longer than 1 second")]
    [InlineData("Paduraksa:Limits:MaxConnections", "-5", "can't be negative")]
    [InlineData("Paduraksa:Limits:Http2MaxStreamsPerConnection", "0", "Http2MaxStreamsPerConnection must be at least 1")]
    public void InvalidLimitsFailValidation(string key, string value, string expected)
    {
        var ex = Assert.Throws<OptionsValidationException>(() => TestHost.Create(settings: new Dictionary<string, string?> { [key] = value }));
        Assert.Contains(ex.Failures, f => f.Contains(expected, StringComparison.Ordinal) && f.Contains("Paduraksa:Limits", StringComparison.Ordinal));
    }
}
