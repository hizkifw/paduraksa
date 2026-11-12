using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Simulation;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.EndToEnd;

/// <summary>What an upstream receives as <c>Host</c>, and on HTTPS as SNI, in each mode, for a client on HTTP/1.1 and one on HTTP/2.</summary>
public sealed class UpstreamHostHeaderTests : IAsyncLifetime
{
    private X509Certificate2? _certificate;
    private IHost? _plain;
    private IHost? _secure;
    private string _plainAddress = string.Empty;
    private string _secureAddress = string.Empty;

    public async ValueTask InitializeAsync()
    {
#pragma warning disable RS0030 // Test-only: an ephemeral key and self-signed cert for a loopback upstream; nothing is exported or persisted.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new("CN=nas.lan", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        _certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
#pragma warning restore RS0030
        (_plain, _plainAddress) = await HeaderEchoUpstreamHost.StartAsync(onResponse: null, TestContext.Current.CancellationToken);
        (_secure, _secureAddress) = await HeaderEchoUpstreamHost.StartAsync(onResponse: null, TestContext.Current.CancellationToken, _certificate);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in new[] { _plain, _secure })
        {
            if (host is not null)
            {
                await host.StopAsync(TestContext.Current.CancellationToken);
                host.Dispose();
            }
        }

        _certificate?.Dispose();
    }

    private string PlainAuthority => new Uri(_plainAddress).Authority;

    [Theory]
    [InlineData(HttpProtocols.Http1)]
    [InlineData(HttpProtocols.Http2)]
    public async Task OriginalSendsTheClientsHost(HttpProtocols client)
    {
        var seen = await SendAsync(client, _plainAddress, UpstreamHostHeaderMode.Original, null);

        Assert.Equal("app.test", seen["Host"]);
    }

    [Theory]
    [InlineData(HttpProtocols.Http1)]
    [InlineData(HttpProtocols.Http2)]
    public async Task DestinationSendsTheDestinationsOwnAddress(HttpProtocols client)
    {
        var seen = await SendAsync(client, _plainAddress, UpstreamHostHeaderMode.Destination, null);

        Assert.Equal(PlainAuthority, seen["Host"]);
    }

    [Theory]
    [InlineData(HttpProtocols.Http1)]
    [InlineData(HttpProtocols.Http2)]
    public async Task CustomSendsItsNameWhateverTheClientAsked(HttpProtocols client)
    {
        var seen = await SendAsync(client, _plainAddress, UpstreamHostHeaderMode.Custom, "api.internal:8443");

        Assert.Equal("api.internal:8443", seen["Host"]);
    }

    [Theory]
    [InlineData(HttpProtocols.Http1)]
    [InlineData(HttpProtocols.Http2)]
    public async Task SniFollowsTheClientsHostInOriginalMode(HttpProtocols client)
    {
        var seen = await SendAsync(client, _secureAddress, UpstreamHostHeaderMode.Original, null);

        Assert.Equal("app.test", seen["Host"]);
        Assert.Equal("app.test", seen["sni"]);
    }

    [Theory]
    [InlineData(HttpProtocols.Http1)]
    [InlineData(HttpProtocols.Http2)]
    public async Task SniFollowsTheDestinationsNameInDestinationMode(HttpProtocols client)
    {
        var address = _secureAddress.Replace("127.0.0.1", "localhost", StringComparison.Ordinal);
        var seen = await SendAsync(client, address, UpstreamHostHeaderMode.Destination, null);

        Assert.Equal(new Uri(address).Authority, seen["Host"]);
        Assert.Equal("localhost", seen["sni"]);
    }

    [Theory]
    [InlineData(HttpProtocols.Http1)]
    [InlineData(HttpProtocols.Http2)]
    public async Task SniFollowsTheCustomNameWithoutItsPort(HttpProtocols client)
    {
        var seen = await SendAsync(client, _secureAddress, UpstreamHostHeaderMode.Custom, "api.internal:8443");

        Assert.Equal("api.internal:8443", seen["Host"]);
        Assert.Equal("api.internal", seen["sni"]);
    }

    [Fact]
    public async Task TwoHostNamesToOneHttpsDestinationOpenTheirOwnTlsConnectionsOverH2()
    {
        // A connection is for one server name: a request for another Host must not ride a connection whose handshake named the first.
        var handshakes = new List<string?>();
        var (upstream, address) = await HeaderEchoUpstreamHost.StartAsync(onResponse: null, TestContext.Current.CancellationToken, _certificate, name => { lock (handshakes) { handshakes.Add(name); } });
        await using var server = await ProxyTestServer.StartAsync(
            new ProxyConfigModel
            {
                Upstreams = [Upstream("up", address) with { TlsTrust = UpstreamTlsTrust.Any }],
                Hosts = [ProxyHost("a", "a.test", Route("a-root", "up")), ProxyHost("b", "b.test", Route("b-root", "up"))],
            },
            new ProxyTestServerOptions { Protocols = HttpProtocols.Http2 });
        using var client = ProxyTestServer.CreateClient();
        try
        {
            foreach (var host in new[] { "a.test", "b.test", "a.test", "b.test" })
            {
                using var request = server.Request(host, "/");
                using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
                var seen = HeaderEchoUpstreamHost.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
                Assert.Equal(host, seen["Host"]);
                Assert.Equal(host, seen["sni"]);
            }

            string?[] names;
            lock (handshakes)
            {
                names = [.. handshakes];
            }

            Assert.Equal(["a.test", "b.test"], names.Order(StringComparer.Ordinal));
        }
        finally
        {
            await upstream.StopAsync(TestContext.Current.CancellationToken);
            upstream.Dispose();
        }
    }

    [Fact]
    public async Task DestinationModeToAnIpDestinationOverTlsSendsNoSni()
    {
        var seen = await SendAsync(HttpProtocols.Http1, _secureAddress, UpstreamHostHeaderMode.Destination, null);

        Assert.Equal(new Uri(_secureAddress).Authority, seen["Host"]); // 127.0.0.1:port
        Assert.DoesNotContain("sni", seen.Keys);
    }

    [Theory]
    [InlineData("destination-authority")]
    [InlineData("destination-host")]
    [InlineData("clients-host")]
    public async Task ACustomHostThatEqualsAnotherHostInPlayIsStillSentAsConfigured(string which)
    {
        var authority = new Uri(_plainAddress).Authority;
        var custom = which switch
        {
            "destination-authority" => authority,
            "destination-host" => new Uri(_plainAddress).Host,
            _ => "app.test",
        };

        var seen = await SendAsync(HttpProtocols.Http1, _plainAddress, UpstreamHostHeaderMode.Custom, custom);

        Assert.Equal(custom, seen["Host"]);
    }

    [Fact]
    public async Task ARouteChangesItsHostWhenTheUpstreamIsEdited()
    {
        await using var server = await ProxyTestServer.StartAsync(Model(_plainAddress, UpstreamHostHeaderMode.Original, null));
        using var client = ProxyTestServer.CreateClient();
        Assert.Equal("app.test", await HostSeenAsync(server, client));

        var published = await server.PublishAsync(Model(_plainAddress, UpstreamHostHeaderMode.Custom, "one.internal"));

        Assert.True(published.Succeeded, string.Join("; ", published.Errors));
        Assert.Equal("one.internal", await HostSeenAsync(server, client));
    }

    [Theory]
    [InlineData(UpstreamHostHeaderMode.Original, null)]
    [InlineData(UpstreamHostHeaderMode.Destination, null)]
    [InlineData(UpstreamHostHeaderMode.Custom, "api.internal:8443")]
    public async Task TheSimulatorReportsTheHostTheUpstreamReceived(UpstreamHostHeaderMode mode, string? name)
    {
        var model = Model(_plainAddress, mode, name);
        var seen = await SendAsync(HttpProtocols.Http1, _plainAddress, mode, name);

        var run = GateSimulator.Run(Bound(model), new GateProbe { Host = "app.test", ClientAddress = IPAddress.Loopback });

        var step = Assert.Single(run.Steps, s => s.Kind == GateStepKind.Upstream);
        Assert.Equal(seen["Host"], Assert.Single(step.Facts, f => f.Name == "hostSent").Value);
        Assert.Equal(mode.ToString(), Assert.Single(step.Facts, f => f.Name == "hostHeaderMode").Value);
        Assert.Contains($"Host: {seen["Host"]}", step.Summary, StringComparison.Ordinal);
    }

    private static async Task<Dictionary<string, string>> SendAsync(HttpProtocols client, string address, UpstreamHostHeaderMode mode, string? name)
    {
        await using var server = await ProxyTestServer.StartAsync(
            Model(address, mode, name), new ProxyTestServerOptions { Protocols = client });
        using var http = ProxyTestServer.CreateClient();
        using var request = server.Request("app.test", "/");
        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, body);
        return HeaderEchoUpstreamHost.Parse(body);
    }

    private static async Task<string> HostSeenAsync(ProxyTestServer server, HttpClient client)
    {
        using var request = server.Request("app.test", "/");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return HeaderEchoUpstreamHost.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))["Host"];
    }

    private static ProxyConfigModel Model(string address, UpstreamHostHeaderMode mode, string? name) => new()
    {
        Upstreams =
        [
            Upstream("up", address) with
            {
                HostHeader = mode,
                HostHeaderValue = name,
                TlsTrust = address.StartsWith("https:", StringComparison.Ordinal) ? UpstreamTlsTrust.Any : UpstreamTlsTrust.System,
            },
        ],
        Hosts = [ProxyHost("app", "app.test", Route("app-root", "up"))],
    };
}
