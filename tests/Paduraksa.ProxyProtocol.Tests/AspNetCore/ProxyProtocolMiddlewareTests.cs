using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Paduraksa.ProxyProtocol.AspNetCore;
using Paduraksa.Tests.Shared;
using static Paduraksa.Tests.Shared.ProxyProtocolTestData;

namespace Paduraksa.ProxyProtocol.Tests;

/// <summary>The middleware on a real Kestrel, over raw sockets so a test controls exactly what the proxy sends and when.</summary>
public sealed class ProxyProtocolMiddlewareTests : IAsyncLifetime
{
    private const string Get = "GET / HTTP/1.1\r\nHost: test\r\nConnection: close\r\n\r\n";

    private int _throwingCalls;
    private X509Certificate2? _certificate;
    private readonly Dictionary<string, int> _ports = [];
    private readonly Dictionary<string, ConcurrentQueue<ProxyProtocolOutcome>> _outcomes = [];
    private readonly ConcurrentQueue<(string Listener, ProxyProtocolOutcome Outcome, EndPoint? Remote, EndPoint? Proxy)> _reports = [];
    private IHost? _host;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var loopback = IPNetwork.Parse("127.0.0.0/8");
        _certificate = CreateSelfSignedCertificate();
        var certificate = _certificate;
        var listeners = new (string Name, IPAddress Address, Action<ProxyProtocolOptions> Configure, bool Tls)[]
        {
            ("required", IPAddress.Loopback, o => o.TrustedProxies = [loopback], false),
            ("untrusted", IPAddress.Loopback, o => o.TrustedProxies = [IPNetwork.Parse("10.0.0.0/8")], false),
            ("default", IPAddress.Loopback, static _ => { }, false),
            ("fast-timeout", IPAddress.Loopback, o =>
            {
                o.TrustedProxies = [loopback];
                o.HeaderTimeout = TimeSpan.FromMilliseconds(300);
            }, false),
            ("small-v2", IPAddress.Loopback, o =>
            {
                o.TrustedProxies = [loopback];
                o.MaxV2Length = ProxyProtocolParser.V2MaxLengthWithoutTlvs;
            }, false),
            ("optional", IPAddress.Loopback, o =>
            {
                o.TrustedProxies = [loopback];
                o.Required = false;
            }, false),
            ("optional-untrusted", IPAddress.Loopback, o =>
            {
                o.TrustedProxies = [IPNetwork.Parse("10.0.0.0/8")];
                o.Required = false;
            }, false),
            ("dual-mode", IPAddress.IPv6Any, o => o.TrustedProxies = [loopback], false),
            ("optional-tls", IPAddress.Loopback, o =>
            {
                o.TrustedProxies = [loopback];
                o.Required = false;
            }, true),
            ("required-tls", IPAddress.Loopback, o => o.TrustedProxies = [loopback], true),
            ("throwing", IPAddress.Loopback, o => o.TrustedProxies = [loopback], false),
            ("throwing-untrusted", IPAddress.Loopback, o => o.TrustedProxies = [IPNetwork.Parse("10.0.0.0/8")], false),
        };

        _host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseKestrel(kestrel =>
                {
                    foreach (var (name, address, configure, tls) in listeners)
                    {
                        var queue = _outcomes[name] = new ConcurrentQueue<ProxyProtocolOutcome>();
                        kestrel.Listen(address, 0, l =>
                        {
                            l.Protocols = HttpProtocols.Http1;
                            l.UseProxyProtocol(o =>
                            {
                                configure(o);
                                o.OnOutcome = name.StartsWith("throwing", StringComparison.Ordinal)
                                    ? (outcome, _) =>
                                    {
                                        queue.Enqueue(outcome);
                                        Interlocked.Increment(ref _throwingCalls);
                                        throw new InvalidOperationException("a counter that fails");
                                    }
                                : (outcome, connection) =>
                                    {
                                        _reports.Enqueue((name, outcome, connection.RemoteEndPoint, connection.Features.Get<IProxyProtocolFeature>()?.ProxyEndPoint));
                                        queue.Enqueue(outcome);
                                    };
                            });
                            if (tls)
                            {
                                // The order the README asks for: the header first, the TLS handshake after it.
                                l.UseHttps(certificate);
                            }
                        });
                    }
                })
                .Configure(app => app.Run(static context =>
                {
                    var connection = context.Connection;
                    var feature = context.Features.Get<IProxyProtocolFeature>();
                    var header = feature is null ? "none" : $"v{feature.Header.Version}/{feature.Header.Command}/{feature.Header.HasAddresses}";
                    return context.Response.WriteAsync(
                        $"{connection.RemoteIpAddress}|{connection.RemotePort}|{connection.LocalIpAddress}|{connection.LocalPort}|{header}|{feature?.ProxyEndPoint is IPEndPoint}",
                        context.RequestAborted);
                })))
            .Build();
        await _host.StartAsync(Ct);

        var addresses = _host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.ToArray();
        Assert.Equal(listeners.Length, addresses.Length);
        for (var i = 0; i < listeners.Length; i++)
        {
            _ports[listeners[i].Name] = new Uri(addresses[i]).Port;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync(CancellationToken.None);
            _host.Dispose();
        }

        _certificate?.Dispose();
    }

    /// <summary>A throwaway self-signed certificate for the TLS listeners (the client accepts any).</summary>
    private static X509Certificate2 CreateSelfSignedCertificate()
    {
#pragma warning disable RS0030 // Test-only: an ephemeral key and self-signed cert for loopback TLS listeners; nothing is exported or persisted.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        return X509CertificateLoader.LoadPkcs12(
            request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1)).Export(X509ContentType.Pfx),
            null);
#pragma warning restore RS0030
    }

    [Theory]
    [InlineData("PROXY TCP4 203.0.113.7 192.0.2.1 50123 443\r\n", "203.0.113.7|50123|192.0.2.1|443|v1/Proxy/True|True")]
    [InlineData("PROXY TCP6 2001:db8::7 2001:db8::1 50123 443\r\n", "2001:db8::7|50123|2001:db8::1|443|v1/Proxy/True|True")]
    public async Task V1HeaderSetsTheConnectionsEndpoints(string line, string expected)
    {
        var response = await ExchangeAsync("required", V1(line + Get));

        Assert.StartsWith("HTTP/1.1 200 ", response, StringComparison.Ordinal);
        Assert.Contains(expected, response, StringComparison.Ordinal);
        await AssertOutcomeAsync("required", ProxyProtocolOutcome.Accepted);
    }

    [Theory]
    [InlineData(1, 0, "203.0.113.7|50123|192.0.2.1|443|v2/Proxy/True|True")]
    [InlineData(1, 120, "203.0.113.7|50123|192.0.2.1|443|v2/Proxy/True|True")]
    [InlineData(2, 0, "2001:db8::7|50123|2001:db8::1|443|v2/Proxy/True|True")]
    public async Task V2HeaderSetsTheConnectionsEndpoints(int family, int tlvBytes, string expected)
    {
        var header = family == 1
            ? V2(family: 1, source: "203.0.113.7", destination: "192.0.2.1", tlvBytes: tlvBytes)
            : V2(family: 2, source: "2001:db8::7", destination: "2001:db8::1", tlvBytes: tlvBytes);

        var response = await ExchangeAsync("required", Concat(header, V1(Get)));

        Assert.Contains(expected, response, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("v2-local")]
    [InlineData("v1-unknown")]
    public async Task AHeaderWithoutAddressesKeepsTheSocketsEndpoints(string kind)
    {
        var header = kind == "v2-local" ? V2(command: 0, family: 0, transport: 0) : V1("PROXY UNKNOWN\r\n");

        var response = await ExchangeAsync("required", Concat(header, V1(Get)));

        Assert.Contains("|127.0.0.1|", response, StringComparison.Ordinal);
        Assert.StartsWith("HTTP/1.1 200 ", response, StringComparison.Ordinal);
        Assert.Contains(kind == "v2-local" ? "|v2/Local/False|True" : "|v1/Proxy/False|True", response, StringComparison.Ordinal);
        await AssertOutcomeAsync("required", ProxyProtocolOutcome.AcceptedWithoutAddress);
    }

    [Fact]
    public async Task AHeaderSplitAcrossReadsIsStillOneHeader()
    {
        var bytes = Concat(V2(family: 1, tlvBytes: 40), V1(Get));
        using var socket = await ConnectAsync("required");

        // Cuts inside the signature, inside the fixed part, inside the addresses, and inside the TLVs.
        var cuts = new[] { 0, 5, 14, 20, 40, bytes.Length };
        for (var i = 1; i < cuts.Length; i++)
        {
            await socket.SendAsync(bytes.AsMemory(cuts[i - 1], cuts[i] - cuts[i - 1]), SocketFlags.None, Ct);
            await Task.Delay(20, Ct);
        }

        var response = await ReadToEndAsync(socket);
        Assert.StartsWith("HTTP/1.1 200 ", response, StringComparison.Ordinal);
        Assert.Contains("203.0.113.7|50123|", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARequestSentWithTheHeaderInOneWriteIsNotLost()
    {
        var response = await ExchangeAsync("required", Concat(V2(family: 1), V1(Get)));

        Assert.StartsWith("HTTP/1.1 200 ", response, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("garbage\r\n")]
    [InlineData("PROXY TCP4 203.0.113.7 192.0.2.1 50123\r\n")]
    [InlineData("GET / HTTP/1.1\r\nHost: test\r\n\r\n")]
    public async Task AMissingOrBrokenHeaderAbortsTheConnection(string bytes)
    {
        var response = await ExchangeAsync("required", V1(bytes));

        Assert.Equal(string.Empty, response);
        await AssertOutcomeAsync("required", ProxyProtocolOutcome.Invalid);
    }

    [Fact]
    public async Task AV2HeaderOverTheConfiguredMaximumAbortsTheConnection()
    {
        var response = await ExchangeAsync("small-v2", Concat(V2(family: 1, tlvBytes: 300), V1(Get)));

        Assert.Equal(string.Empty, response);
        await AssertOutcomeAsync("small-v2", ProxyProtocolOutcome.Invalid);
    }

    [Fact]
    public async Task ASourceOutsideTheTrustedProxiesIsAbortedUnread()
    {
        var response = await ExchangeAsync("untrusted", Concat(V2(family: 1), V1(Get)));

        Assert.Equal(string.Empty, response);
        await AssertOutcomeAsync("untrusted", ProxyProtocolOutcome.UntrustedSource);
    }

    [Fact]
    public async Task WithNoTrustedProxiesEveryConnectionIsRefused()
    {
        var response = await ExchangeAsync("default", Concat(V2(family: 1), V1(Get)));

        Assert.Equal(string.Empty, response);
        await AssertOutcomeAsync("default", ProxyProtocolOutcome.UntrustedSource);
    }

    [Fact]
    public async Task ASilentTrustedPeerIsAbortedWhenTheTimeoutPasses()
    {
        using var socket = await ConnectAsync("fast-timeout");

        // No bytes sent: the server must close on its own.
        var response = await ReadToEndAsync(socket);

        Assert.Equal(string.Empty, response);
        await AssertOutcomeAsync("fast-timeout", ProxyProtocolOutcome.TimedOut);
    }

    [Fact]
    public async Task APartialHeaderThenSilenceIsAbortedWhenTheTimeoutPasses()
    {
        using var socket = await ConnectAsync("fast-timeout");
        await socket.SendAsync(V2Signature.AsMemory(0, 8), SocketFlags.None, Ct);

        var response = await ReadToEndAsync(socket);

        Assert.Equal(string.Empty, response);
        await AssertOutcomeAsync("fast-timeout", ProxyProtocolOutcome.TimedOut);
    }

    [Fact]
    public async Task APeerThatClosesBeforeTheHeaderEndsIsCountedAsClosed()
    {
        using (var socket = await ConnectAsync("required"))
        {
            await socket.SendAsync(V1("PROXY TCP4 203."), SocketFlags.None, Ct);
        }

        await AssertOutcomeAsync("required", ProxyProtocolOutcome.Closed);
    }

    [Fact]
    public async Task AnOptionalHeaderIsHonouredWhenItIsThere()
    {
        var response = await ExchangeAsync("optional", Concat(V2(family: 1), V1(Get)));

        Assert.Contains("203.0.113.7|50123|192.0.2.1|443|v2/Proxy/True|True", response, StringComparison.Ordinal);
        await AssertOutcomeAsync("optional", ProxyProtocolOutcome.Accepted);
    }

    [Fact]
    public async Task WithoutARequiredHeaderAPlainRequestGoesThroughOnTheSocketsEndpoints()
    {
        var response = await ExchangeAsync("optional", V1(Get));

        Assert.StartsWith("HTTP/1.1 200 ", response, StringComparison.Ordinal);
        Assert.Contains("|127.0.0.1|", response, StringComparison.Ordinal);
        Assert.Contains("|none|False", response, StringComparison.Ordinal);
        await AssertOutcomeAsync("optional", ProxyProtocolOutcome.NoHeader);
    }

    /// <summary>An HTTP/2 prior-knowledge preface starts with <c>PRI</c>: it begins like <c>PROXY</c> and must not be mistaken for a broken header.</summary>
    [Theory]
    [InlineData("POST / HTTP/1.1\r\nHost: test\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")]
    [InlineData("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n")]
    public async Task AnOptionalHeaderDoesNotMistakeRequestsThatStartWithPForAHeader(string request)
    {
        await ExchangeAsync("optional", V1(request));

        await AssertOutcomeAsync("optional", ProxyProtocolOutcome.NoHeader);
    }

    [Theory]
    [InlineData("PROXY TCP4 203.0.113.7 192.0.2.1 50123\r\n")]
    [InlineData("PROXY \r\n")]
    public async Task AHeaderThatBeginsRightAndThenBreaksAbortsEvenWhenOptional(string bytes)
    {
        var response = await ExchangeAsync("optional", V1(bytes));

        Assert.Equal(string.Empty, response);
        await AssertOutcomeAsync("optional", ProxyProtocolOutcome.Invalid);
    }

    [Fact]
    public async Task AnUntrustedPeerThatSendsAHeaderToAnOptionalListenerGetsNoClientAddress()
    {
        var response = await ExchangeAsync("optional-untrusted", Concat(V1("PROXY TCP4 203.0.113.7 192.0.2.1 50123 443\r\n"), V1(Get)));

        // The header isn't honoured: Kestrel reads it as a broken request line.
        Assert.StartsWith("HTTP/1.1 400 ", response, StringComparison.Ordinal);
        Assert.DoesNotContain("203.0.113.7", response, StringComparison.Ordinal);
        await AssertOutcomeAsync("optional-untrusted", ProxyProtocolOutcome.UntrustedSource);
    }

    [Fact]
    public async Task AnUntrustedPeerWithoutAHeaderGoesThroughAnOptionalListener()
    {
        var response = await ExchangeAsync("optional-untrusted", V1(Get));

        Assert.StartsWith("HTTP/1.1 200 ", response, StringComparison.Ordinal);
        Assert.Contains("|none|False", response, StringComparison.Ordinal);
    }

    /// <summary>A dual-mode listener reports a loopback IPv4 peer as <c>::ffff:127.0.0.1</c>, which must match an IPv4 trusted network.</summary>
    [Fact]
    public async Task AnIPv4PeerOnADualModeListenerMatchesAnIPv4Network()
    {
        Assert.SkipUnless(Socket.OSSupportsIPv6, "no IPv6 on this machine");

        var response = await ExchangeAsync("dual-mode", Concat(V2(family: 1), V1(Get)));

        Assert.StartsWith("HTTP/1.1 200 ", response, StringComparison.Ordinal);
        Assert.Contains("203.0.113.7|50123|", response, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("af-unix")]
    [InlineData("af-unspec")]
    public async Task AUnixOrUnspecifiedHeaderKeepsTheSocketsEndpoints(string kind)
    {
        // Both are valid headers that name no client address: the socket's own endpoints stay.
        var header = kind == "af-unix" ? V2(family: 3, transport: 1) : V2(command: 1, family: 0, transport: 0);

        var response = await ExchangeAsync("required", Concat(header, V1(Get)));

        Assert.StartsWith("HTTP/1.1 200 ", response, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1|", response, StringComparison.Ordinal);
        Assert.Contains("|v2/Proxy/False|True", response, StringComparison.Ordinal);
        await AssertOutcomeAsync("required", ProxyProtocolOutcome.AcceptedWithoutAddress);
    }

    /// <summary>The first byte of a TLS ClientHello is 0x16, which can't begin a header: an optional listener must hand the handshake on untouched.</summary>
    [Fact]
    public async Task AnOptionalListenerHandsATlsClientHelloOnUntouched()
    {
        using var socket = await ConnectAsync("optional-tls");
        using var stream = TlsClient(socket);
        await stream.AuthenticateAsClientAsync("localhost");

        await stream.WriteAsync(V1(Get), Ct);
        var response = await ReadToEndAsync(stream);

        Assert.StartsWith("HTTP/1.1 200 ", response, StringComparison.Ordinal);
        Assert.Contains("|127.0.0.1|", response, StringComparison.Ordinal);
        Assert.Contains("|none|False", response, StringComparison.Ordinal);
        await AssertOutcomeAsync("optional-tls", ProxyProtocolOutcome.NoHeader);
    }

    [Fact]
    public async Task AnOptionalListenerReadsTheHeaderThenTheTlsHandshake()
    {
        using var socket = await ConnectAsync("optional-tls");
        await socket.SendAsync(V1("PROXY TCP4 203.0.113.7 192.0.2.1 50123 443\r\n"), SocketFlags.None, Ct);
        using var stream = TlsClient(socket);
        await stream.AuthenticateAsClientAsync("localhost");

        await stream.WriteAsync(V1(Get), Ct);
        var response = await ReadToEndAsync(stream);

        Assert.Contains("203.0.113.7|50123|192.0.2.1|443|v1/Proxy/True|True", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARequiredHeaderIsReadBeforeTheTlsHandshake()
    {
        using var socket = await ConnectAsync("required-tls");
        await socket.SendAsync(V2(family: 2, source: "2001:db8::7", destination: "2001:db8::1", sourcePort: 50123, destinationPort: 443), SocketFlags.None, Ct);
        using var stream = TlsClient(socket);
        await stream.AuthenticateAsClientAsync("localhost");

        await stream.WriteAsync(V1(Get), Ct);
        var response = await ReadToEndAsync(stream);

        Assert.Contains("2001:db8::7|50123|2001:db8::1|443|v2/Proxy/True|True", response, StringComparison.Ordinal);
    }

    /// <summary>The first byte alone can't tell a header from a request (<c>P</c> starts both <c>PROXY</c> and <c>POST</c>): the middleware must wait for the next one.</summary>
    [Theory]
    [InlineData("P", "ROXY TCP4 203.0.113.7 192.0.2.1 50123 443\r\nGET / HTTP/1.1\r\nHost: test\r\nConnection: close\r\n\r\n", "203.0.113.7|50123|", ProxyProtocolOutcome.Accepted)]
    [InlineData("P", "OST / HTTP/1.1\r\nHost: test\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", "|127.0.0.1|", ProxyProtocolOutcome.NoHeader)]
    [InlineData("G", "ET / HTTP/1.1\r\nHost: test\r\nConnection: close\r\n\r\n", "|127.0.0.1|", ProxyProtocolOutcome.NoHeader)]
    public async Task AnOptionalListenerDecidesOnTheFirstBytesWhenTheyArriveOneAtATime(string first, string rest, string expected, ProxyProtocolOutcome outcome)
    {
        using var socket = await ConnectAsync("optional");
        await socket.SendAsync(V1(first), SocketFlags.None, Ct);
        await Task.Delay(50, Ct);
        await socket.SendAsync(V1(rest), SocketFlags.None, Ct);

        var response = await ReadToEndAsync(socket);

        Assert.StartsWith("HTTP/1.1 200 ", response, StringComparison.Ordinal);
        Assert.Contains(expected, response, StringComparison.Ordinal);
        await AssertOutcomeAsync("optional", outcome);
    }

    [Fact]
    public async Task AV2HeaderSentOneByteThenTheRestIsStillRead()
    {
        var bytes = Concat(V2(family: 1, source: "203.0.113.9"), V1(Get));
        using var socket = await ConnectAsync("optional");
        await socket.SendAsync(bytes.AsMemory(0, 1), SocketFlags.None, Ct);
        await Task.Delay(50, Ct);
        await socket.SendAsync(bytes.AsMemory(1), SocketFlags.None, Ct);

        var response = await ReadToEndAsync(socket);

        Assert.Contains("203.0.113.9|50123|", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACounterThatThrowsDoesNotCostTheConnection()
    {
        var accepted = await ExchangeAsync("throwing", Concat(V2(family: 1), V1(Get)));
        var broken = await ExchangeAsync("throwing", V1("garbage\r\n"));

        Assert.StartsWith("HTTP/1.1 200 ", accepted, StringComparison.Ordinal);
        Assert.Contains("203.0.113.7|50123|", accepted, StringComparison.Ordinal);
        Assert.Equal(string.Empty, broken);
        await AssertOutcomeAsync("throwing", ProxyProtocolOutcome.Accepted);
        await AssertOutcomeAsync("throwing", ProxyProtocolOutcome.Invalid);
        Assert.True(Volatile.Read(ref _throwingCalls) >= 2);
    }

    [Fact]
    public async Task ACounterThatThrowsDoesNotCostAnUntrustedRefusalEither()
    {
        // The untrusted path reports before it aborts: the abort must still happen, and the counter must have been called.
        var response = await ExchangeAsync("throwing-untrusted", Concat(V2(family: 1), V1(Get)));

        Assert.Equal(string.Empty, response);
        await AssertOutcomeAsync("throwing-untrusted", ProxyProtocolOutcome.UntrustedSource);
    }

    [Fact]
    public async Task EachConnectionReadsItsOwnHeader()
    {
        var first = ExchangeAsync("required", Concat(V1("PROXY TCP4 203.0.113.1 192.0.2.1 1111 443\r\n"), V1(Get)));
        var second = ExchangeAsync("required", Concat(V1("PROXY TCP4 203.0.113.2 192.0.2.1 2222 443\r\n"), V1(Get)));
        var answers = await Task.WhenAll(first, second);

        Assert.Contains("203.0.113.1|1111|", answers[0], StringComparison.Ordinal);
        Assert.Contains("203.0.113.2|2222|", answers[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheOutcomeCallbackSeesWhoWasRefusedAndWhoWasAccepted()
    {
        await ExchangeAsync("untrusted", V1("PROXY TCP4 203.0.113.9 192.0.2.1 50123 443\r\n" + Get));
        await ExchangeAsync("required", V1("PROXY TCP4 203.0.113.9 192.0.2.1 50123 443\r\n" + Get));
        await AssertOutcomeAsync("untrusted", ProxyProtocolOutcome.UntrustedSource);
        await AssertOutcomeAsync("required", ProxyProtocolOutcome.Accepted);

        // Refused: the socket peer, so a log line can name it. (The enqueue of the report precedes the outcome the waits above saw.)
        var refused = Assert.IsType<IPEndPoint>(_reports.First(r => r is { Listener: "untrusted", Outcome: ProxyProtocolOutcome.UntrustedSource }).Remote);
        Assert.True(IPAddress.IsLoopback(refused.Address));

        // Accepted: the connection as the application sees it, and the proxy on the feature.
        var accepted = _reports.First(r => r is { Listener: "required", Outcome: ProxyProtocolOutcome.Accepted } && r.Remote is IPEndPoint { Port: 50123 });
        Assert.Equal(IPAddress.Parse("203.0.113.9"), ((IPEndPoint)accepted.Remote!).Address);
        Assert.True(IPAddress.IsLoopback(Assert.IsType<IPEndPoint>(accepted.Proxy).Address));
    }

    [Fact]
    public void TheOptionsRejectValuesTheParserCannotHonour()
    {
        var options = new ProxyProtocolOptions();

        Assert.Throws<ArgumentOutOfRangeException>(() => options.HeaderTimeout = TimeSpan.Zero);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.HeaderTimeout = ProxyProtocolOptions.MaxHeaderTimeout + TimeSpan.FromTicks(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => options.MaxV2Length = ProxyProtocolParser.V2MaxLengthWithoutTlvs - 1);
        Assert.Empty(options.TrustedProxies);
        Assert.True(options.Required);
        Assert.False(options.TrustUnixSockets);
        Assert.Equal(TimeSpan.FromSeconds(3), options.HeaderTimeout);
        Assert.Equal(ProxyProtocolParser.DefaultMaxV2Length, options.MaxV2Length);
    }

    [Fact]
    public void UseProxyProtocolChecksItsArguments()
    {
        Assert.Throws<ArgumentNullException>(() => ProxyProtocolListenOptionsExtensions.UseProxyProtocol(null!, _ => { }));
    }

    private async Task<Socket> ConnectAsync(string listener)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(IPAddress.Loopback, _ports[listener], Ct);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Sends <paramref name="bytes"/> and returns what the server answered until it closed (empty when it closed without an answer).</summary>
    private async Task<string> ExchangeAsync(string listener, byte[] bytes)
    {
        try
        {
            using var socket = await ConnectAsync(listener);
            await socket.SendAsync(bytes, SocketFlags.None, Ct);
            return await ReadToEndAsync(socket);
        }
        catch (SocketException)
        {
            // The server aborts an untrusted peer before reading, so the reset can beat the connect or the write.
            return string.Empty;
        }
    }

    /// <summary>A TLS client that pins the listeners' throwaway certificate.</summary>
    private SslStream TlsClient(Socket socket)
    {
        var pinned = _certificate!.GetCertHashString();
        return new SslStream(
            new NetworkStream(socket, ownsSocket: false),
            leaveInnerStreamOpen: false,
            (_, certificate, _, _) => certificate is not null && certificate.GetCertHashString() == pinned);
    }

    private static async Task<string> ReadToEndAsync(Stream stream)
    {
        var received = new List<byte>();
        var buffer = new byte[4096];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0)
            {
                received.AddRange(buffer.AsSpan(0, read));
            }
        }
        catch (IOException)
        {
            // A reset, or TLS closing without close_notify, is a close too.
        }

        return Encoding.ASCII.GetString([.. received]);
    }

    private static async Task<string> ReadToEndAsync(Socket socket)
    {
        var received = new List<byte>();
        var buffer = new byte[4096];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            int read;
            while ((read = await socket.ReceiveAsync(buffer, SocketFlags.None, timeout.Token)) > 0)
            {
                received.AddRange(buffer.AsSpan(0, read));
            }
        }
        catch (SocketException)
        {
            // A reset is a close too.
        }

        return Encoding.ASCII.GetString([.. received]);
    }

    /// <summary>Waits for the outcome: the server reports it on its own thread, possibly after the client saw the close. Other outcomes on the queue
    /// don't matter (a local port prober connects to every new port), but the expected one must show up.</summary>
    private async Task AssertOutcomeAsync(string listener, ProxyProtocolOutcome expected)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var queue = _outcomes[listener];
        while (!queue.Contains(expected))
        {
            try
            {
                await Task.Delay(10, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                Assert.Fail($"No {expected} on listener '{listener}'; saw [{string.Join(", ", queue)}].");
            }
        }
    }
}
