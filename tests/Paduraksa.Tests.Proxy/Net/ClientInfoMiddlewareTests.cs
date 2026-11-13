using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Connections;
using Paduraksa.Proxy.Net;
using Paduraksa.ProxyProtocol;
using Paduraksa.Tests.Shared;
using static Paduraksa.Tests.Shared.ProxyProtocolTestData;

namespace Paduraksa.Tests.Proxy.Net;

public sealed class ClientInfoMiddlewareTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly IPEndPoint LoadBalancer = new(IPAddress.Parse("::ffff:10.0.0.5"), 40000);
    private static readonly IPEndPoint Stranger = new(IPAddress.Parse("198.51.100.9"), 40001);

    private static ProxyProtocolOptions Options(TimeSpan? timeout = null) =>
        new(CidrSet.Parse(["10.0.0.0/24"])) { HeaderTimeout = timeout ?? TimeSpan.FromSeconds(5) };

    [Fact]
    public async Task WithoutProxyProtocolTakesTheSocketPeer()
    {
        var (middleware, seen) = Build(null);
        var connection = new TestConnection(new IPEndPoint(IPAddress.Parse("::ffff:192.0.2.44"), 5555));

        await middleware.OnConnectionAsync(connection);

        var info = Assert.Single(seen);
        Assert.Equal(IpKey.Parse("192.0.2.44"), info.Address);
        Assert.Equal(5555, info.Port);
        Assert.Equal(ConnectionAddressSource.Socket, info.Source);
        Assert.Equal(info.Address, info.TransportAddress);
        Assert.Null(connection.AbortReason);
    }

    [Fact]
    public async Task NonIpEndPointGivesTheUnspecifiedAddress()
    {
        var (middleware, seen) = Build(null);
        await middleware.OnConnectionAsync(new TestConnection(null));
        Assert.Equal(IpKey.Unspecified, Assert.Single(seen).Address);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task TrustedSourceWithHeaderSetsClientInfoAndLeavesThePayload(int version)
    {
        var options = Options();
        var (middleware, seen, payloads) = BuildReadingPayload(options);
        var connection = new TestConnection(LoadBalancer);
        var header = version == 1 ? V1("PROXY TCP4 203.0.113.7 10.0.0.1 50123 443\r\n") : V2();
        await connection.ClientWriter.WriteAsync(Concat(header, V1("GET / HTTP/1.1\r\n")), Ct);
        await connection.ClientWriter.CompleteAsync();

        await middleware.OnConnectionAsync(connection);

        Assert.Null(connection.AbortReason);
        var info = Assert.Single(seen);
        Assert.Equal(IpKey.Parse("203.0.113.7"), info.Address);
        Assert.Equal(50123, info.Port);
        Assert.Equal(ConnectionAddressSource.ProxyProtocol, info.Source);
        Assert.True(info.IsFromProxyProtocol);
        Assert.Equal(IpKey.Parse("10.0.0.5"), info.TransportAddress);
        Assert.Equal(40000, info.TransportPort);
        Assert.Equal("GET / HTTP/1.1\r\n", Assert.Single(payloads));
        Assert.Equal(1, options.Counters.Accepted);
    }

    [Fact]
    public async Task HeaderArrivingInPiecesIsAssembled()
    {
        var options = Options();
        var (middleware, seen, payloads) = BuildReadingPayload(options);
        var connection = new TestConnection(LoadBalancer);
        var bytes = Concat(V2(family: 2, source: "2001:db8::9", destination: "2001:db8::1", tlvBytes: 100), V1("hello"));

        var run = middleware.OnConnectionAsync(connection);
        foreach (var chunk in bytes.Chunk(7))
        {
            await connection.ClientWriter.WriteAsync(chunk, Ct);
            await Task.Yield();
        }

        await connection.ClientWriter.CompleteAsync();
        await run;

        Assert.Equal(IpKey.Parse("2001:db8::9"), Assert.Single(seen).Address);
        Assert.Equal("hello", Assert.Single(payloads));
    }

    [Theory]
    [InlineData(0, 1)] // LOCAL
    [InlineData(1, 0)] // AF_UNSPEC
    public async Task LocalOrUnspecifiedHeaderUsesTheSocketPeer(int command, int family)
    {
        var options = Options();
        var (middleware, seen, _) = BuildReadingPayload(options);
        var connection = new TestConnection(LoadBalancer);
        await connection.ClientWriter.WriteAsync(V2(command: command, family: family, transport: family == 0 ? 0 : 1), Ct);
        await connection.ClientWriter.CompleteAsync();

        await middleware.OnConnectionAsync(connection);

        var info = Assert.Single(seen);
        Assert.Equal(IpKey.Parse("10.0.0.5"), info.Address);
        Assert.Equal(ConnectionAddressSource.ProxyProtocolUnspecified, info.Source);
        Assert.False(info.IsFromProxyProtocol);
        Assert.Equal(1, options.Counters.AcceptedWithoutAddress);
    }

    [Fact]
    public async Task UntrustedSourceIsAbortedWithoutReading()
    {
        var options = Options();
        var (middleware, seen) = Build(options);
        var connection = new TestConnection(Stranger);
        await connection.ClientWriter.WriteAsync(V2(), Ct);

        await middleware.OnConnectionAsync(connection);

        Assert.Empty(seen);
        Assert.NotNull(connection.AbortReason);
        Assert.Equal(1, options.Counters.UntrustedSource);
        // Nothing was consumed: the header is still sitting in the pipe.
        Assert.True(connection.Transport.Input.TryRead(out var unread));
        Assert.Equal(V2().Length, unread.Buffer.Length);
    }

    [Fact]
    public async Task TrustedSourceWithoutHeaderIsAborted()
    {
        var options = Options();
        var (middleware, seen) = Build(options);
        var connection = new TestConnection(LoadBalancer);
        await connection.ClientWriter.WriteAsync(V1("GET / HTTP/1.1\r\nHost: x\r\n\r\n"), Ct);

        await middleware.OnConnectionAsync(connection);

        Assert.Empty(seen);
        Assert.NotNull(connection.AbortReason);
        Assert.Equal(1, options.Counters.Invalid);
    }

    [Fact]
    public async Task ClosedBeforeTheHeaderIsAborted()
    {
        var options = Options();
        var (middleware, seen) = Build(options);
        var connection = new TestConnection(LoadBalancer);
        await connection.ClientWriter.WriteAsync(V1("PROXY TCP4 1.2.3.4"), Ct);
        await connection.ClientWriter.CompleteAsync();

        await middleware.OnConnectionAsync(connection);

        Assert.Empty(seen);
        Assert.NotNull(connection.AbortReason);
        Assert.Equal(1, options.Counters.Closed);
    }

    [Fact]
    public async Task SlowOrSilentPeerTimesOut()
    {
        var options = Options(TimeSpan.FromMilliseconds(200));
        var (middleware, seen) = Build(options);
        var connection = new TestConnection(LoadBalancer);
        await connection.ClientWriter.WriteAsync(V1("PROXY TCP4 "), Ct);

        var stopwatch = Stopwatch.StartNew();
        var run = middleware.OnConnectionAsync(connection);
        // Keep dripping valid-prefix bytes: the timeout is for the whole header, not per read.
        foreach (var b in V1("1.2.3.4 5.6.7.8 1"))
        {
            if (run.IsCompleted)
            {
                break;
            }

            await connection.ClientWriter.WriteAsync(new[] { b }, Ct);
            // A peer that drips a byte at a time: the timeout is for the whole header, not per read.
            await Task.Delay(40, Ct);
        }

        await run.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(3));
        Assert.Empty(seen);
        Assert.NotNull(connection.AbortReason);
        Assert.Equal(1, options.Counters.TimedOut);
    }

    [Fact]
    public async Task EachAbortGetsItsOwnException()
    {
        var options = Options();
        var (middleware, _) = Build(options);
        var first = new TestConnection(Stranger);
        var second = new TestConnection(Stranger);

        await middleware.OnConnectionAsync(first);
        await middleware.OnConnectionAsync(second);

        Assert.NotNull(first.AbortReason);
        Assert.NotNull(second.AbortReason);
        Assert.NotSame(first.AbortReason, second.AbortReason);
    }

    [Fact]
    public async Task TrustedSourcesCanBeSwapped()
    {
        var options = Options();
        options.TrustedSources = CidrSet.Parse(["198.51.100.0/24"]);
        var (middleware, seen, _) = BuildReadingPayload(options);
        var connection = new TestConnection(Stranger);
        await connection.ClientWriter.WriteAsync(V2(), Ct);
        await connection.ClientWriter.CompleteAsync();

        await middleware.OnConnectionAsync(connection);

        Assert.Single(seen);
        Assert.Throws<ArgumentNullException>(() => options.TrustedSources = null!);
    }

    [Fact]
    public void OptionsValidateTimeoutAndLength()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProxyProtocolOptions(CidrSet.Empty) { HeaderTimeout = TimeSpan.Zero });
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProxyProtocolOptions(CidrSet.Empty) { HeaderTimeout = TimeSpan.FromMinutes(1.5) });
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProxyProtocolOptions(CidrSet.Empty) { HeaderTimeout = TimeSpan.FromDays(50) });
        Assert.Equal(TimeSpan.FromMinutes(1), new ProxyProtocolOptions(CidrSet.Empty) { HeaderTimeout = ProxyProtocolOptions.MaxHeaderTimeout }.HeaderTimeout);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProxyProtocolOptions(CidrSet.Empty) { MaxV2Length = 100 });
    }

    /// <summary>Malformed, truncated and random input never throws and never outlives the timeout.</summary>
    [Fact]
    public async Task FuzzMalformedInputNeverThrowsOrHangs()
    {
        var random = new Random(7239);
        var timeout = TimeSpan.FromMilliseconds(100);
        byte[][] seeds =
        [
            V1("PROXY TCP4 203.0.113.7 10.0.0.1 50123 443\r\n"),
            V1("PROXY TCP6 2001:db8::1 2001:db8::2 1 2\r\n"),
            V1("PROXY UNKNOWN\r\n"),
            V2(),
            V2(family: 2, source: "2001:db8::1", destination: "2001:db8::2", tlvBytes: 50),
            V2(command: 0, family: 0, transport: 0),
        ];

        var tasks = new List<Task<ProxyProtocolReadResult>>();
        var pipes = new List<Pipe>();
        for (var i = 0; i < 400; i++)
        {
            var bytes = (byte[])seeds[random.Next(seeds.Length)].Clone();
            switch (random.Next(4))
            {
                case 0: // flip bytes
                    for (var k = random.Next(1, 4); k > 0; k--)
                    {
                        bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
                    }

                    break;
                case 1: // truncate
                    bytes = bytes[..random.Next(bytes.Length)];
                    break;
                case 2: // random bytes
                    bytes = new byte[random.Next(0, 300)];
                    random.NextBytes(bytes);
                    break;
                default: // corrupt the v2 length field / extend v1 line
                    if (bytes[0] == 0x0D && bytes.Length > 15)
                    {
                        bytes[14] = (byte)random.Next(256);
                        bytes[15] = (byte)random.Next(256);
                    }
                    else
                    {
                        bytes = Concat(bytes[..^2], Encoding.ASCII.GetBytes(new string('9', random.Next(1, 120))), V1("\r\n"));
                    }

                    break;
            }

            var pipe = new Pipe();
            pipes.Add(pipe);
            await pipe.Writer.WriteAsync(bytes, Ct);
            if (random.Next(2) == 0)
            {
                await pipe.Writer.CompleteAsync();
            }

            tasks.Add(ProxyProtocolReader.ReadAsync(pipe.Reader, timeout).AsTask());
        }

        var all = Task.WhenAll(tasks);
        await all.WaitAsync(TimeSpan.FromSeconds(10), Ct); // bounded: each read ends by its 100 ms timeout at worst
        foreach (var task in tasks)
        {
            var result = await task;
            Assert.True(Enum.IsDefined(result.Status));
        }
    }

    [Fact]
    public async Task ReaderReportsAbortAsClosed()
    {
        var pipe = new Pipe();
        var read = ProxyProtocolReader.ReadAsync(pipe.Reader, TimeSpan.FromSeconds(5));
        await pipe.Writer.CompleteAsync(new ConnectionResetException("reset"));
        Assert.Equal(ProxyProtocolReadStatus.Closed, (await read).Status);
    }

    private static (ClientInfoMiddleware, List<ClientInfo>) Build(ProxyProtocolOptions? options)
    {
        var seen = new List<ClientInfo>();
        var middleware = new ClientInfoMiddleware(
            c =>
            {
                seen.Add(c.Features.Get<IClientInfoFeature>()!.ClientInfo);
                return Task.CompletedTask;
            },
            options);
        return (middleware, seen);
    }

    /// <summary>Like <see cref="Build"/>, but the "next" middleware also reads the rest of the input, as TLS/HTTP would.</summary>
    private static (ClientInfoMiddleware, List<ClientInfo>, List<string>) BuildReadingPayload(ProxyProtocolOptions? options)
    {
        var seen = new List<ClientInfo>();
        var payloads = new List<string>();
        var middleware = new ClientInfoMiddleware(
            async c =>
            {
                seen.Add(c.Features.Get<IClientInfoFeature>()!.ClientInfo);
                var input = c.Transport.Input;
                while (true)
                {
                    var result = await input.ReadAsync(Ct);
                    if (result.IsCompleted)
                    {
                        payloads.Add(Encoding.ASCII.GetString(result.Buffer));
                        input.AdvanceTo(result.Buffer.End);
                        return;
                    }

                    input.AdvanceTo(result.Buffer.Start, result.Buffer.End);
                }
            },
            options);
        return (middleware, seen, payloads);
    }
}
