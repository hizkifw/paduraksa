using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Yarp;
using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Hosting;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Integration.Proxy.PublicProxyTests;

namespace Paduraksa.Tests.Integration.Proxy;

public sealed class PublicServerHeaderTests : IAsyncLifetime
{
    private const string Header = "Server";
    private const string Named = "Paduraksa";
    private const string Custom = "edge/1.0";
    private const string UpstreamServer = EdgeUpstream.UpstreamServer;

    private readonly TestCertificates _certificates = new();
    private EdgeUpstream? _upstream;
    private WebSocketUpstream? _webSockets;
    private AbortingUpstream? _aborting;
    private NoContentUpstream? _noContent;

    public async ValueTask InitializeAsync()
    {
        _upstream = await EdgeUpstream.StartAsync();
        _webSockets = await WebSocketUpstream.StartAsync();
        _aborting = await AbortingUpstream.StartAsync();
        _noContent = await NoContentUpstream.StartAsync();
    }

    private EdgeUpstream Upstream => _upstream!;

    private WebSocketUpstream WebSockets => _webSockets!;

    private AbortingUpstream Aborting => _aborting!;

    private NoContentUpstream NoContent => _noContent!;

    public async ValueTask DisposeAsync()
    {
        if (_upstream is not null)
        {
            await _upstream.DisposeAsync();
        }

        if (_webSockets is not null)
        {
            await _webSockets.DisposeAsync();
        }

        if (_aborting is not null)
        {
            await _aborting.DisposeAsync();
        }

        if (_noContent is not null)
        {
            await _noContent.DisposeAsync();
        }

        _certificates.Dispose();
    }

    private ProxyConfigModel App(DefaultsSpec? defaults = null) => new()
    {
        Defaults = defaults ?? new DefaultsSpec(),
        Upstreams =
        [
            new UpstreamSpec { Id = TestIds.Upstream("echo"), Destinations = [new(TestIds.Destination("echo-1"), Upstream.Address)] },
            new UpstreamSpec { Id = TestIds.Upstream("ws"), Destinations = [new(TestIds.Destination("ws-1"), WebSockets.Address)] },
            new UpstreamSpec { Id = TestIds.Upstream("abort"), Destinations = [new(TestIds.Destination("abort-1"), Aborting.Address)] },
            new UpstreamSpec { Id = TestIds.Upstream("empty"), Destinations = [new(TestIds.Destination("empty-1"), NoContent.Address)] },
            new UpstreamSpec { Id = TestIds.Upstream("down"), Destinations = [new(TestIds.Destination("down-1"), "http://127.0.0.1:1/")] },
        ],
        Hosts =
        [
            Forward("named", "named.test"),
            Forward("custom", "custom.test") with { ServerHeader = CustomHeader(Custom) },
            Forward("upstream", "upstream.test") with { ServerHeader = new HostServerHeaderSpec { Mode = ServerHeaderMode.Upstream } },
            Forward("remove", "remove.test") with { ServerHeader = new HostServerHeaderSpec { Mode = ServerHeaderMode.Remove } },
            Forward("api", "api.test", "/api") with { ServerHeader = CustomHeader("edge/api") },
            new HostSpec
            {
                Id = TestIds.Host("soon"),
                Names = ["soon.test"],
                Kind = HostKind.Respond,
                Respond = new RespondSpec { StatusCode = 503, Body = "soon" },
                ServerHeader = CustomHeader("edge/soon"),
            },
            new HostSpec
            {
                Id = TestIds.Host("old"),
                Names = ["old.test"],
                Kind = HostKind.Redirect,
                Redirect = new RedirectSpec { Target = "https://new.test", StatusCode = 308 },
                ServerHeader = new HostServerHeaderSpec { Mode = ServerHeaderMode.Remove },
            },
            Forward("deny", "deny.test") with
            {
                ServerHeader = CustomHeader("edge/deny"),
                Policies = [new IpAllowListPolicySpec(["10.0.0.0/8"])],
            },
            Forward("abort", "abort.test", upstream: "abort") with { Hsts = new HostHstsSpec { Enabled = true } },
            Forward("empty", "empty.test", upstream: "empty") with { Hsts = new HostHstsSpec { Enabled = true }, ServerHeader = CustomHeader("edge/empty") },
            Forward("down-named", "down-named.test", upstream: "down"),
            Forward("down-custom", "down-custom.test", upstream: "down") with { ServerHeader = CustomHeader("edge/down") },
            Forward("down-upstream", "down-upstream.test", upstream: "down") with { ServerHeader = new HostServerHeaderSpec { Mode = ServerHeaderMode.Upstream } },
            Forward("down-remove", "down-remove.test", upstream: "down") with { ServerHeader = new HostServerHeaderSpec { Mode = ServerHeaderMode.Remove } },
            Forward("ws-named", "ws-named.test", upstream: "ws"),
            Forward("ws-custom", "ws-custom.test", upstream: "ws") with { ServerHeader = CustomHeader("edge/ws") },
            Forward("ws-upstream", "ws-upstream.test", upstream: "ws") with { ServerHeader = new HostServerHeaderSpec { Mode = ServerHeaderMode.Upstream } },
            Forward("ws-remove", "ws-remove.test", upstream: "ws") with { ServerHeader = new HostServerHeaderSpec { Mode = ServerHeaderMode.Remove } },
        ],
    };

    private static HostServerHeaderSpec CustomHeader(string value) =>
        new() { Mode = ServerHeaderMode.Custom, Value = value };

    private static HostSpec Forward(string label, string name, string path = "/", string upstream = "echo") =>
        new()
        {
            Id = TestIds.Host(label),
            Names = [name],
            Routes = [new RouteSpec { Id = TestIds.Route($"{label}-root"), Path = path, Upstream = TestIds.Upstream(upstream) }],
        };

    private async Task<PaduraksaHost> StartedAsync(ProxyConfigModel model)
    {
        var host = TestHost.Create();
        await host.StartAsync(TestContext.Current.CancellationToken);
        host.Shared.Certificates.Set(
            TestIds.Certificate("all"),
            _certificates.Create(
                "named.test",
                "named.test", "custom.test", "upstream.test", "remove.test", "api.test", "soon.test", "old.test", "deny.test", "abort.test",
                "empty.test", "down-named.test", "down-custom.test", "down-upstream.test", "down-remove.test",
                "ws-named.test", "ws-custom.test", "ws-upstream.test", "ws-remove.test"));
        var result = await host.Shared.SnapshotPublisher.PublishAsync(Compile(host, model), TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        return host;
    }

    private static async Task<(HttpResponseMessage Response, string[] Servers)> SendAsync(
        PaduraksaHost host, string hostHeader, string path = "/")
    {
        using var client = EdgeClient.Create(out _);
        using var request = EdgeClient.Request(host, "http", hostHeader, path);
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        string[] servers = response.Headers.TryGetValues(Header, out var values) ? [.. values] : [];
        return (response, servers);
    }

    [Fact]
    public async Task EachModeHoldsOnForwardedResponses()
    {
        await using var host = await StartedAsync(App());

        foreach (var (name, expected) in new (string Name, string Expected)[]
        {
            ("named.test", Named), // built-in default, with no version
            ("custom.test", Custom),
            ("upstream.test", UpstreamServer), // the upstream's own, untouched
            ("remove.test", string.Empty), // no header at all
        })
        {
            var (response, servers) = await SendAsync(host, name);
            using (response)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal(expected.Length == 0 ? [] : [expected], servers);
                Assert.DoesNotContain(servers, value => value.Contains("Kestrel", StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    [Fact]
    public async Task EachModeHoldsOverHttpsToo()
    {
        await using var host = await StartedAsync(App());

        foreach (var (name, expected) in new (string Name, string Expected)[]
        {
            ("named.test", Named),
            ("custom.test", Custom),
            ("upstream.test", UpstreamServer),
            ("remove.test", string.Empty),
        })
        {
            using var client = EdgeClient.Create(out _);
            using var request = EdgeClient.Request(host, "https", name);
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            string[] servers = response.Headers.TryGetValues(Header, out var values) ? [.. values] : [];
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(expected.Length == 0 ? [] : [expected], servers);
            Assert.DoesNotContain(servers, value => value.Contains("Kestrel", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task TheHeaderIsSetOnHttp2Responses()
    {
        await using var host = await StartedAsync(App());
        using var client = EdgeClient.Create(out _);
        using var request = EdgeClient.Request(host, "https", "named.test", version: HttpVersion.Version20);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpVersion.Version20, response.Version);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["Paduraksa"], response.Headers.GetValues(Header));
    }

    [Fact]
    public async Task KestrelsOwnHeaderNeverAppears()
    {
        // Both listeners turn Kestrel's header off; no mode may bring the name back.
        await using var host = await StartedAsync(App());

        foreach (var name in (string[])["named.test", "custom.test", "upstream.test", "remove.test", "nobody.test"])
        {
            var (response, servers) = await SendAsync(host, name);
            using (response)
            {
                Assert.DoesNotContain(servers, value => value.Contains("Kestrel", StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    [Fact]
    public async Task EachModeHoldsOnGateAnswers()
    {
        await using var host = await StartedAsync(App());

        foreach (var (name, path, status, expected) in new (string Name, string Path, HttpStatusCode Status, string Expected)[]
        {
            ("soon.test", "/", HttpStatusCode.ServiceUnavailable, "edge/soon"), // Respond
            ("old.test", "/deep/path", HttpStatusCode.PermanentRedirect, string.Empty), // Redirect, removed
            ("deny.test", "/", HttpStatusCode.Forbidden, "edge/deny"), // a policy's refusal
            ("api.test", "/nothing", HttpStatusCode.NotFound, "edge/api"), // the gate's own 404
        })
        {
            var (response, servers) = await SendAsync(host, name, path);
            using (response)
            {
                Assert.Equal(status, response.StatusCode);
                Assert.Equal(expected.Length == 0 ? [] : [expected], servers);
                Assert.DoesNotContain(servers, value => value.Contains("Kestrel", StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    [Fact]
    public async Task AnUnknownHostFollowsTheGlobalDefaults()
    {
        await using var host = await StartedAsync(App(new DefaultsSpec
        {
            ServerHeader = CustomHeader("edge/global"),
        }));

        var (response, servers) = await SendAsync(host, "nobody.test");
        using (response)
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(["edge/global"], servers);
        }
    }

    [Fact]
    public async Task EachModeHoldsOnAFailedForward()
    {
        await using var host = await StartedAsync(App());

        foreach (var (name, expected) in new (string Name, string Expected)[]
        {
            ("down-named.test", Named),
            ("down-custom.test", "edge/down"),
            ("down-upstream.test", string.Empty),
            ("down-remove.test", string.Empty),
        })
        {
            var (response, servers) = await SendAsync(host, name);
            using (response)
            {
                Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
                Assert.Equal(expected.Length == 0 ? [] : [expected], servers);
            }
        }
    }

    [Fact]
    public async Task AFailedBodyKeepsTheGatesHeadersOnThe502()
    {
        // The upstream sends status and headers, then dies before the first body byte. YARP clears the response and
        // answers 502 without running the response transforms again; the proxy's post-forward step puts the host's
        // headers back.
        await using var host = await StartedAsync(App());
        using var client = EdgeClient.Create(out _);
        using var request = EdgeClient.Request(host, "https", "abort.test");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(["Paduraksa"], response.Headers.GetValues(Header));
        Assert.Equal(["max-age=31536000"], response.Headers.GetValues("Strict-Transport-Security"));
        Assert.Single(response.Headers.GetValues("X-Request-Id"));
    }

    [Fact]
    public async Task ABodylessUpstreamResponseCarriesEachGateHeaderExactlyOnce()
    {
        // A 204 never starts a response body, so Kestrel fires the response-start hooks at the end of the request, after
        // the response transforms already ran. The reapply hook must not run there a second time, and the upstream's own
        // HSTS and Server values must not survive next to ours.
        await using var host = await StartedAsync(App());
        using var client = EdgeClient.Create(out _);
        using var request = EdgeClient.Request(host, "https", "empty.test");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(["edge/empty"], response.Headers.GetValues(Header));
        Assert.Equal(["max-age=31536000"], response.Headers.GetValues("Strict-Transport-Security"));
        Assert.Single(response.Headers.GetValues("X-Request-Id"));
    }

    [Fact]
    public async Task EachModeHoldsOnAWebSocketUpgrade()
    {
        await using var host = await StartedAsync(App());

        foreach (var (name, expected) in new (string Name, string Expected)[]
        {
            ("ws-named.test", Named),
            ("ws-custom.test", "edge/ws"),
            ("ws-upstream.test", UpstreamServer),
            ("ws-remove.test", string.Empty),
        })
        {
            var head = await UpgradeAsync(host, name);
            Assert.StartsWith("HTTP/1.1 101", head, StringComparison.Ordinal);
            var servers = ServersIn(head);
            Assert.Equal(expected.Length == 0 ? [] : [expected], servers);
        }
    }

    private static async Task<string> UpgradeAsync(PaduraksaHost host, string hostHeader)
    {
        var ct = TestContext.Current.CancellationToken;
        var endpoint = TestHost.Endpoint(host, HostRole.Public, "http");
        using TcpClient tcp = new();
        await tcp.ConnectAsync(IPAddress.Loopback, endpoint.Port, ct);
        var stream = tcp.GetStream();
        var key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        var request = $"GET / HTTP/1.1\r\nHost: {hostHeader}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                      + $"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), ct);
        using StreamReader reader = new(stream, Encoding.ASCII);
        StringBuilder head = new();
        while (true)
        {
            var line = await reader.ReadLineAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct);
            if (line is null || line.Length == 0)
            {
                break;
            }

            head.Append(line).Append("\r\n");
        }

        return head.ToString();
    }

    private static string[] ServersIn(string head) =>
        [.. head.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith(Header + ":", StringComparison.OrdinalIgnoreCase))
            .Select(line => line[(Header.Length + 1)..].Trim())];

    /// <summary>A loopback upstream that sends status and headers, then aborts before the first body byte.</summary>
    private sealed class AbortingUpstream : IAsyncDisposable
    {
        private readonly IHost _host;

        private AbortingUpstream(IHost host, string address)
        {
            _host = host;
            Address = address;
        }

        public string Address { get; }

        public static async Task<AbortingUpstream> StartAsync()
        {
            var host = new HostBuilder()
                .ConfigureLogging(static logging => logging.ClearProviders())
                .ConfigureWebHost(web => web
                    .UseKestrel(static kestrel =>
                    {
                        kestrel.Listen(IPAddress.Loopback, 0);
                        kestrel.AddServerHeader = false;
                    })
                    .Configure(static app => app.Run(static async context =>
                    {
                        context.Response.StatusCode = StatusCodes.Status200OK;
                        context.Response.ContentLength = 64;
                        await context.Response.StartAsync();
                        context.Abort();
                    })))
                .Build();
            await host.StartAsync(TestContext.Current.CancellationToken);
            var address = Assert.Single(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
            return new AbortingUpstream(host, address + "/");
        }

        public async ValueTask DisposeAsync()
        {
            await _host.StopAsync(CancellationToken.None);
            _host.Dispose();
        }
    }

    /// <summary>A loopback upstream that answers 204 with its own <c>Server</c>, HSTS and <c>Alt-Svc</c> headers.</summary>
    private sealed class NoContentUpstream : IAsyncDisposable
    {
        private readonly IHost _host;

        private NoContentUpstream(IHost host, string address)
        {
            _host = host;
            Address = address;
        }

        public string Address { get; }

        public static async Task<NoContentUpstream> StartAsync()
        {
            var host = new HostBuilder()
                .ConfigureLogging(static logging => logging.ClearProviders())
                .ConfigureWebHost(web => web
                    .UseKestrel(static kestrel =>
                    {
                        kestrel.Listen(IPAddress.Loopback, 0);
                        kestrel.AddServerHeader = false;
                    })
                    .Configure(static app => app.Run(static context =>
                    {
                        context.Response.StatusCode = StatusCodes.Status204NoContent;
                        context.Response.Headers["Server"] = EdgeUpstream.UpstreamServer;
                        context.Response.Headers.StrictTransportSecurity = EdgeUpstream.UpstreamHsts;
                        context.Response.Headers.AltSvc = EdgeUpstream.UpstreamAltSvc;
                        return Task.CompletedTask;
                    })))
                .Build();
            await host.StartAsync(TestContext.Current.CancellationToken);
            var address = Assert.Single(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
            return new NoContentUpstream(host, address + "/");
        }

        public async ValueTask DisposeAsync()
        {
            await _host.StopAsync(CancellationToken.None);
            _host.Dispose();
        }
    }

    /// <summary>A loopback upstream that accepts a WebSocket upgrade and sets its own <c>Server</c> header.</summary>
    private sealed class WebSocketUpstream : IAsyncDisposable
    {
        private readonly IHost _host;

        private WebSocketUpstream(IHost host, string address)
        {
            _host = host;
            Address = address;
        }

        public string Address { get; }

        public static async Task<WebSocketUpstream> StartAsync()
        {
            var host = new HostBuilder()
                .ConfigureLogging(static logging => logging.ClearProviders())
                .ConfigureWebHost(web => web
                    .UseKestrel(static kestrel =>
                    {
                        kestrel.Listen(IPAddress.Loopback, 0);
                        kestrel.AddServerHeader = false;
                    })
                    .Configure(static app =>
                    {
                        app.UseWebSockets();
                        app.Run(static async context =>
                        {
                            context.Response.Headers["Server"] = UpstreamServer;
                            if (!context.WebSockets.IsWebSocketRequest)
                            {
                                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                                return;
                            }

                            using var socket = await context.WebSockets.AcceptWebSocketAsync();
                            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ok", context.RequestAborted);
                        });
                    }))
                .Build();
            await host.StartAsync(TestContext.Current.CancellationToken);
            var address = Assert.Single(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
            return new WebSocketUpstream(host, address + "/");
        }

        public async ValueTask DisposeAsync()
        {
            await _host.StopAsync(CancellationToken.None);
            _host.Dispose();
        }
    }
}
