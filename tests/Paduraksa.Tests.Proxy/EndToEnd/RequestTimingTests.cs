using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
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
using Microsoft.Extensions.Logging;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Logging;
using Paduraksa.Tests.Proxy.Testing;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.EndToEnd;

/// <summary>
/// Each stage of a request's time (docs/design/observability.md "Request timing") against a known delay on a real listener: a slow client handshake,
/// a trickled body, a slow upstream connect and first byte, a slow reader. The delays are hundreds of milliseconds, so a stage is checked with a
/// floor a little under its delay, and never with a ceiling a busy machine could break, except where it must stay near zero by construction.
/// </summary>
public sealed class RequestTimingTests : IAsyncLifetime
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan Floor = TimeSpan.FromMilliseconds(250);

    private readonly X509Certificate2 _certificate = TestCertificates.SelfSigned();
    private TimingUpstream? _upstream;

    private TimingUpstream Upstream => _upstream!;

    public async ValueTask InitializeAsync() => _upstream = await TimingUpstream.StartAsync(certificate: null);

    public async ValueTask DisposeAsync()
    {
        if (_upstream is not null)
        {
            await _upstream.DisposeAsync();
        }

        _certificate.Dispose();
    }

    [Theory]
    [InlineData(HttpProtocols.Http1)]
    [InlineData(HttpProtocols.Http2)]
    public async Task ASlowHandshakeIsReportedOnTheFirstRequestAndReusedAfter(HttpProtocols protocols)
    {
        await using var server = await ProxyTestServer.StartAsync(Model(Upstream.Address), new() { Certificate = _certificate, Protocols = protocols });
        // The client waits after the TCP connect before it starts TLS, so the server waits that long for the ClientHello.
        using HttpClient client = new(new SocketsHttpHandler
        {
            UseProxy = false,
            MaxConnectionsPerServer = 1,
            SslOptions = { RemoteCertificateValidationCallback = static (_, certificate, _, _) => TestCertificates.IsTestCertificate(certificate) },
            ConnectCallback = static async (context, ct) =>
            {
                Socket socket = new(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                await socket.ConnectAsync(context.DnsEndPoint, ct);
                await Task.Delay(Delay, ct);
                return new NetworkStream(socket, ownsSocket: true);
            },
        });

        await SendAsync(client, server, "/tls/1");
        await SendAsync(client, server, "/tls/2");

        var entries = await EntriesAsync(server, "/tls/1", "/tls/2");
        Assert.True(entries[0].TlsHandshakeTicks >= Floor.Ticks, $"the handshake was {Ms(entries[0].TlsHandshakeTicks)}");
        Assert.Equal(1, entries[0].ConnectionRequest);
        Assert.Equal(AccessLogEntry.HandshakeReused, entries[1].TlsHandshakeTicks);
        Assert.Equal(2, entries[1].ConnectionRequest);
    }

    [Fact]
    public async Task RequestsOnAPlainConnectionAreCountedInOrderWithNoHandshake()
    {
        await using var server = await ProxyTestServer.StartAsync(Model(Upstream.Address), new() { Protocols = HttpProtocols.Http2 });
        using var client = ProxyTestServer.CreateClient(maxConnections: 1);

        for (var i = 1; i <= 3; i++)
        {
            await SendAsync(client, server, "/plain/" + i.ToString(CultureInfo.InvariantCulture));
        }

        var entries = await EntriesAsync(server, "/plain/1", "/plain/2", "/plain/3");
        Assert.Equal([1, 2, 3], entries.Select(static entry => entry.ConnectionRequest));
        Assert.All(entries, static entry => Assert.Equal(AccessLogEntry.Unknown, entry.TlsHandshakeTicks));
    }

    [Fact]
    public async Task ATrickledBodyIsTimedFromItsFirstByteToItsLastAndNoBodyIsZero()
    {
        await using var server = await ProxyTestServer.StartAsync(Model(Upstream.Address));
        var ct = TestContext.Current.CancellationToken;
        using TcpClient tcp = new();
        await tcp.ConnectAsync(IPAddress.Loopback, server.BaseAddress.Port, ct);
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("POST /upload/trickled HTTP/1.1\r\nHost: app.test\r\nContent-Length: 3\r\n\r\n"), ct);
        for (var i = 0; i < 3; i++)
        {
            if (i > 0)
            {
                await Task.Delay(Delay / 2, ct);
            }

            await stream.WriteAsync("x"u8.ToArray(), ct);
        }

        Assert.Contains("200", await ReadHeadAsync(stream, ct), StringComparison.Ordinal);
        using var client = ProxyTestServer.CreateClient();
        await SendAsync(client, server, "/nobody");

        var entries = await EntriesAsync(server, "/upload/trickled", "/nobody");
        Assert.True(entries[0].RequestBodyTicks >= Floor.Ticks, $"the body read took {Ms(entries[0].RequestBodyTicks)}");
        Assert.Equal(3, entries[0].RequestBytes);
        Assert.False(entries[0].RequestBodyUnfinished);
        Assert.Equal(0, entries[1].RequestBodyTicks);
        Assert.Equal(AccessLogEntry.UnknownOffset, entries[1].RequestBodyStartMicros);
        AssertPlaced(entries[0]);
        AssertPlaced(entries[1]);
        // The upstream answers once it has the body, so the wait starts at the body's last byte and is not the trickle.
        Assert.True(entries[0].UpstreamWaitTicks < entries[0].RequestBodyTicks, $"the wait was {Ms(entries[0].UpstreamWaitTicks)}");
        Assert.True(Micros(entries[0].RequestBodyStartMicros) + entries[0].RequestBodyTicks <= WaitStart(entries[0]) + TimeSpan.TicksPerMillisecond / 1000);
    }

    [Fact]
    public async Task AnUpstreamThatAnswersBeforeTheBodyIsInWaitsForNothing()
    {
        // Over TLS the forward is HTTP/2, which hands YARP an early refusal at once; the proxy then answers and drops the rest of the body.
        // The upstream refuses only once the proxy has read the body's first byte: the upstream can't wait to receive that byte itself,
        // since the forwarder holds a lone byte of a body in its HTTP/2 stream until more of the body comes or it ends.
        await using var tlsUpstream = await TimingUpstream.StartAsync(_certificate);
        TaskCompletionSource firstByteRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await ProxyTestServer.StartAsync(
            Model(tlsUpstream.Address, UpstreamTlsTrust.Any),
            new()
            {
                AfterGate = (context, next) =>
                {
                    // Over the counted body, so the signal comes after the proxy's own read has been counted and timed.
                    context.Request.Body = new FirstReadSignal(context.Request.Body, firstByteRead);
                    return next(context);
                },
            });
        var ct = TestContext.Current.CancellationToken;
        using TcpClient tcp = new();
        await tcp.ConnectAsync(IPAddress.Loopback, server.BaseAddress.Port, ct);
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("POST /early/answer HTTP/1.1\r\nHost: app.test\r\nContent-Length: 3\r\n\r\nx"), ct);

        await firstByteRead.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        tlsUpstream.ReleaseEarlyAnswer();
        Assert.Contains("413", await ReadHeadAsync(stream, ct), StringComparison.Ordinal);

        var entry = Assert.Single(await EntriesAsync(server, "/early/answer"));
        Assert.Equal(1, entry.RequestBytes);
        Assert.True(entry.RequestBodyUnfinished, "the body was cut off by the answer");
        Assert.Equal(0, entry.UpstreamWaitTicks);
        AssertPlaced(entry);
        // The zero-length wait sits at the answer, after the body's one read.
        var answered = Micros(entry.ForwardStartMicros) + entry.UpstreamTicks;
        Assert.Equal(answered, WaitStart(entry));
        Assert.True(Micros(entry.RequestBodyStartMicros) <= answered, "the body was read after the answer");
    }

    [Fact]
    public async Task ASlowUpstreamConnectIsTheConnectPartAndAPooledConnectionPaysNone()
    {
        await using var tlsUpstream = await TimingUpstream.StartAsync(_certificate);
        // The relay holds the upstream's first bytes (its ServerHello), so the TLS part of the connect waits for it.
        await using var relay = DelayingRelay.Start(tlsUpstream.Port, Delay);
        var model = Model($"https://127.0.0.1:{relay.Port}/", UpstreamTlsTrust.Any);
        await using var server = await ProxyTestServer.StartAsync(model);
        using var client = ProxyTestServer.CreateClient(maxConnections: 1);

        await SendAsync(client, server, "/connect/1");
        await SendAsync(client, server, "/connect/2");

        var entries = await EntriesAsync(server, "/connect/1", "/connect/2");
        Assert.True(entries[0].UpstreamConnectTicks >= Floor.Ticks, $"the connect took {Ms(entries[0].UpstreamConnectTicks)}");
        Assert.InRange(entries[0].UpstreamConnectTicks, 0, entries[0].UpstreamTicks);
        Assert.Equal(0, entries[1].UpstreamConnectTicks);
        Assert.True(entries[1].UpstreamTicks >= 0);
        AssertPlaced(entries[0]);
        AssertPlaced(entries[1]);
        // The wait starts once the connection is up, and the reused connection has no connect to place.
        Assert.True(WaitStart(entries[0]) >= Micros(entries[0].UpstreamConnectStartMicros) + entries[0].UpstreamConnectTicks - TimeSpan.TicksPerMillisecond / 1000);
        Assert.True(entries[0].UpstreamWaitTicks < entries[0].UpstreamConnectTicks, $"the wait was {Ms(entries[0].UpstreamWaitTicks)}");
        Assert.Equal(AccessLogEntry.UnknownOffset, entries[1].UpstreamConnectStartMicros);
    }

    [Fact]
    public async Task ASlowFirstByteIsTheWaitNotTheConnect()
    {
        await using var server = await ProxyTestServer.StartAsync(Model(Upstream.Address));
        using var client = ProxyTestServer.CreateClient(maxConnections: 1);

        await SendAsync(client, server, "/slow/first-byte");

        var entry = Assert.Single(await EntriesAsync(server, "/slow/first-byte"));
        Assert.True(entry.UpstreamTicks >= Floor.Ticks, $"the forward took {Ms(entry.UpstreamTicks)}");
        // A loopback connect, whatever the machine is doing, is far from the upstream's own wait.
        Assert.True(entry.UpstreamWaitTicks >= Floor.Ticks, $"the wait was {Ms(entry.UpstreamWaitTicks)}");
        Assert.InRange(entry.UpstreamWaitTicks, 0, entry.UpstreamTicks);
        AssertPlaced(entry);
    }

    [Fact]
    public async Task ASlowReaderIsTheResponseSend()
    {
        await using var server = await ProxyTestServer.StartAsync(Model(Upstream.Address));
        var ct = TestContext.Current.CancellationToken;
        // A small fixed receive buffer turns off the kernel's autotuning, so what the client hasn't read can't all wait in buffers.
        using Socket socket = new(SocketType.Stream, ProtocolType.Tcp) { ReceiveBufferSize = 64 * 1024 };
        await socket.ConnectAsync(IPAddress.Loopback, server.BaseAddress.Port, ct);
        await using NetworkStream stream = new(socket, ownsSocket: false);
        await stream.WriteAsync(Encoding.ASCII.GetBytes("GET /big/slow-reader HTTP/1.1\r\nHost: app.test\r\nConnection: close\r\n\r\n"), ct);

        await ReadHeadAsync(stream, ct);
        await Task.Delay(Delay * 2, ct);
        var buffer = new byte[64 * 1024];
        while (await stream.ReadAsync(buffer, ct) > 0)
        {
        }

        var entry = Assert.Single(await EntriesAsync(server, "/big/slow-reader"));
        Assert.Equal(TimingUpstream.BigBody, entry.ResponseBytes);
        Assert.True(entry.ResponseSendTicks >= Floor.Ticks, $"the send took {Ms(entry.ResponseSendTicks)}");
        Assert.InRange(entry.ResponseSendTicks, 0, entry.TotalTicks);
    }

    [Fact]
    public async Task TheGateIsTimedOnAForwardAndOnItsOwnAnswer()
    {
        await using var server = await ProxyTestServer.StartAsync(Model(Upstream.Address));
        using var client = ProxyTestServer.CreateClient();

        await SendAsync(client, server, "/forwarded");
        using (var request = server.Request("nobody.test", "/answered"))
        {
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        var entries = await EntriesAsync(server, "/forwarded", "/answered");
        Assert.All(entries, static entry => Assert.InRange(entry.GateTicks, 0, entry.TotalTicks));
        Assert.All(entries, static entry => Assert.InRange(entry.ResponseSendTicks, 0, entry.TotalTicks));
        Assert.All(entries, AssertPlaced);
        Assert.True(entries[0].UpstreamConnectTicks >= 0, "the forward opened a connection or reused one");
        Assert.Equal(AccessLogEntry.Unknown, entries[1].UpstreamConnectTicks);
        Assert.Equal(AccessLogEntry.Unknown, entries[1].UpstreamTicks);
        Assert.Equal(AccessLogEntry.Unknown, entries[1].UpstreamWaitTicks);
        Assert.Equal(AccessLogEntry.UnknownOffset, entries[1].ForwardStartMicros);
    }

    // Every stage starts and ends inside the request, in the pipeline's order: the gate hands the request on before the forward starts,
    // the connect runs inside the forward, the wait ends at the upstream's answer and the response goes out after it. An offset is
    // whole microseconds and a duration is ticks, so a comparison across the two allows a microsecond.
    private static void AssertPlaced(AccessLogEntry entry)
    {
        const long Slack = TimeSpan.TicksPerMillisecond / 1000;
        var total = entry.TotalTicks;
        var sendStart = entry.ResponseSendTicks >= 0 ? total - entry.ResponseSendTicks : total;
        if (entry.RequestBodyStartMicros >= 0)
        {
            Assert.InRange(Micros(entry.RequestBodyStartMicros) + entry.RequestBodyTicks, 0, total + Slack);
        }

        if (entry.ForwardStartMicros < 0)
        {
            Assert.Equal(AccessLogEntry.UnknownOffset, entry.UpstreamConnectStartMicros);
            return;
        }

        var forwardStart = Micros(entry.ForwardStartMicros);
        var answered = forwardStart + entry.UpstreamTicks;
        Assert.InRange(entry.GateTicks, 0, forwardStart + Slack);
        Assert.InRange(answered, forwardStart, sendStart + Slack);
        if (entry.UpstreamConnectStartMicros >= 0)
        {
            var connectStart = Micros(entry.UpstreamConnectStartMicros);
            Assert.InRange(connectStart, forwardStart - Slack, answered + Slack);
            Assert.InRange(connectStart + entry.UpstreamConnectTicks, forwardStart, answered + Slack);
        }

        Assert.InRange(entry.UpstreamWaitTicks, 0, entry.UpstreamTicks);
        Assert.InRange(WaitStart(entry), forwardStart, answered);
    }

    private static long WaitStart(AccessLogEntry entry) => RequestStages.WaitStartTicks(entry.ForwardStartMicros, entry.UpstreamTicks, entry.UpstreamWaitTicks);

    private static long Micros(int micros) => micros * (TimeSpan.TicksPerMillisecond / 1000);

    private static ProxyConfigModel Model(string address, UpstreamTlsTrust trust = UpstreamTlsTrust.System) => new()
    {
        Upstreams = [Upstream("web", address) with { TlsTrust = trust }],
        Hosts = [ProxyHost("app", "app.test", Route("app-root", "web"))],
    };

    private static async Task SendAsync(HttpClient client, ProxyTestServer server, string path)
    {
        using var request = server.Request("app.test", path);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await response.Content.LoadIntoBufferAsync(TestContext.Current.CancellationToken);
    }

    // The entries of these paths, in their order; a machine's port prober may add its own "GET /" lines, which no test path is.
    private static async Task<AccessLogEntry[]> EntriesAsync(ProxyTestServer server, params string[] paths)
    {
        Dictionary<string, AccessLogEntry> found = [];
        for (var attempt = 0; attempt < 500 && found.Count < paths.Length; attempt++)
        {
            while (server.AccessLog.TryRead(out var entry))
            {
                if (entry.Path is { } path && paths.Contains(path))
                {
                    found[path] = entry;
                }
            }

            if (found.Count < paths.Length)
            {
                // Poll step of a wait bounded to five seconds: the lines are written from the response's completion.
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
        }

        return [.. paths.Select(path => found.TryGetValue(path, out var entry) ? entry : throw new Xunit.Sdk.XunitException($"no line for {path}"))];
    }

    private static async Task<string> ReadHeadAsync(Stream stream, CancellationToken ct)
    {
        StringBuilder head = new();
        var one = new byte[1];
        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(one, ct) == 0)
            {
                break;
            }

            head.Append((char)one[0]);
        }

        return head.ToString();
    }

    private static string Ms(long ticks) => TimeSpan.FromTicks(ticks).TotalMilliseconds.ToString("0.000", CultureInfo.InvariantCulture) + " ms";

    /// <summary>
    /// An upstream with known delays: <c>/slow/...</c> waits before its first byte, <c>/big/...</c> answers <see cref="BigBody"/> bytes,
    /// <c>/upload/...</c> reads the whole body first, <c>/early/...</c> refuses the body with a 413 without reading it, once <see cref="ReleaseEarlyAnswer"/> lets it; anything else answers at once.
    /// </summary>
    private sealed class TimingUpstream : IAsyncDisposable
    {
        public const int BigBody = 16 * 1024 * 1024;

        private readonly IHost _host;

        private readonly TaskCompletionSource _earlyAnswer;

        private TimingUpstream(IHost host, string address, TaskCompletionSource earlyAnswer)
        {
            _host = host;
            Address = address;
            Port = new Uri(address).Port;
            _earlyAnswer = earlyAnswer;
        }

        /// <summary>Lets <c>/early/...</c> answer.</summary>
        public void ReleaseEarlyAnswer() => _earlyAnswer.TrySetResult();

        public string Address { get; }

        public int Port { get; }

        public static async Task<TimingUpstream> StartAsync(X509Certificate2? certificate)
        {
            TaskCompletionSource earlyAnswer = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var host = new HostBuilder()
                .ConfigureLogging(static logging => logging.ClearProviders())
                .ConfigureWebHost(web => web
                    .UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen =>
                    {
                        if (certificate is not null)
                        {
                            listen.UseHttps(certificate);
                        }
                    }))
                    .Configure(app => app.Run(async context =>
                    {
                        var path = context.Request.Path.Value ?? string.Empty;
                        var ct = context.RequestAborted;
                        if (path.StartsWith("/slow/", StringComparison.Ordinal))
                        {
                            await Task.Delay(Delay, ct);
                        }
                        else if (path.StartsWith("/upload/", StringComparison.Ordinal))
                        {
                            await context.Request.Body.CopyToAsync(Stream.Null, ct);
                        }
                        else if (path.StartsWith("/early/", StringComparison.Ordinal))
                        {
                            await earlyAnswer.Task.WaitAsync(ct);
                            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                            return;
                        }
                        else if (path.StartsWith("/big/", StringComparison.Ordinal))
                        {
                            context.Response.ContentLength = BigBody;
                            var chunk = new byte[64 * 1024];
                            for (var sent = 0; sent < BigBody; sent += chunk.Length)
                            {
                                await context.Response.Body.WriteAsync(chunk, ct);
                            }

                            return;
                        }

                        await context.Response.WriteAsync("ok", ct);
                    })))
                .Build();
            await host.StartAsync(TestContext.Current.CancellationToken);
            var address = Assert.Single(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
            return new TimingUpstream(host, address + "/", earlyAnswer);
        }

        public async ValueTask DisposeAsync()
        {
            await _host.StopAsync(CancellationToken.None);
            _host.Dispose();
        }
    }

    /// <summary>The request body as the proxy reads it, completing <paramref name="signal"/> once a read has returned bytes.</summary>
    private sealed class FirstReadSignal(Stream inner, TaskCompletionSource signal) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Signal(await inner.ReadAsync(buffer, cancellationToken));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => Signal(inner.Read(buffer, offset, count));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Signal(int read)
        {
            if (read > 0)
            {
                signal.TrySetResult();
            }

            return read;
        }
    }

    /// <summary>A TCP relay to a loopback port that holds back the target's first bytes for a while, then passes everything through.</summary>
    private sealed class DelayingRelay : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _accepting;

        private DelayingRelay(TcpListener listener, int target, TimeSpan delay)
        {
            _listener = listener;
            _accepting = AcceptAsync(target, delay, _stop.Token);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public static DelayingRelay Start(int target, TimeSpan delay)
        {
            TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            return new DelayingRelay(listener, target, delay);
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            try
            {
                await _accepting;
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // Stopped.
            }

            _stop.Dispose();
        }

        private async Task AcceptAsync(int target, TimeSpan delay, CancellationToken ct)
        {
            List<Task> relays = [];
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(ct);
                    relays.Add(RelayAsync(client, target, delay, ct));
                }
            }
            finally
            {
                await Task.WhenAll(relays.Select(static relay => relay.ContinueWith(static _ => { }, TaskScheduler.Default)));
            }
        }

        private static async Task RelayAsync(TcpClient client, int target, TimeSpan delay, CancellationToken ct)
        {
            using (client)
            using (TcpClient upstream = new())
            {
                await upstream.ConnectAsync(IPAddress.Loopback, target, ct);
                var toUpstream = client.GetStream().CopyToAsync(upstream.GetStream(), ct);
                var toClient = DelayedCopyAsync(upstream.GetStream(), client.GetStream(), delay, ct);
                await Task.WhenAny(toUpstream, toClient);
            }
        }

        private static async Task DelayedCopyAsync(Stream from, Stream to, TimeSpan delay, CancellationToken ct)
        {
            var buffer = new byte[16 * 1024];
            var first = true;
            int read;
            while ((read = await from.ReadAsync(buffer, ct)) > 0)
            {
                if (first)
                {
                    await Task.Delay(delay, ct);
                    first = false;
                }

                await to.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
    }
}
