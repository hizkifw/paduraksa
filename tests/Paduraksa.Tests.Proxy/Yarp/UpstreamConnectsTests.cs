using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

using Paduraksa.Proxy.Yarp;
using Paduraksa.Tests.Proxy.Testing;

namespace Paduraksa.Tests.Proxy.Yarp;

public sealed class UpstreamConnectsTests
{
    [Fact]
    public async Task TheRequestThatOpenedAConnectionReportsItsConnectFromTheForwardsStartAndOneOnThePooledConnectionNone()
    {
        var ct = TestContext.Current.CancellationToken;
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serving = ServeAsync(listener, answers: 2, ct);
        using HttpMessageInvoker client = new(new SocketsHttpHandler { UseProxy = false, PlaintextStreamFilter = UpstreamConnects.CompletedAsync });

        using HttpRequestMessage first = new(HttpMethod.Get, $"http://127.0.0.1:{port}/a");
        var firstStarted = Stopwatch.GetTimestamp();
        using (var response = await client.SendAsync(first, ct))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using HttpRequestMessage second = new(HttpMethod.Get, $"http://127.0.0.1:{port}/b");
        var secondStarted = Stopwatch.GetTimestamp();
        using (var response = await client.SendAsync(second, ct))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        await serving;
        Assert.InRange(UpstreamConnects.TicksFor(first, firstStarted, long.MaxValue, null, out var started), 1, long.MaxValue);
        Assert.Equal(firstStarted, started);
        Assert.Equal(0, UpstreamConnects.TicksFor(second, secondStarted, long.MaxValue, null, out started));
        Assert.Equal(0, started);
        // Never more than the forward it is part of.
        Assert.Equal(1, UpstreamConnects.TicksFor(first, firstStarted, 1, null, out _));
        // A connect that ended before the forward's start can't be the triggering request's; it is no negative figure.
        Assert.Equal(0, UpstreamConnects.TicksFor(first, Stopwatch.GetTimestamp() + Stopwatch.Frequency, long.MaxValue, null, out started));
        Assert.Equal(0, started);
    }

    [Fact]
    public async Task ARefusedConnectIsTheWholeForward()
    {
        var ct = TestContext.Current.CancellationToken;
        // A port nothing listens on: bind one, then close it.
        int port;
        using (TcpListener closed = new(IPAddress.Loopback, 0))
        {
            closed.Start();
            port = ((IPEndPoint)closed.LocalEndpoint).Port;
        }

        using HttpMessageInvoker client = new(new SocketsHttpHandler { UseProxy = false, PlaintextStreamFilter = UpstreamConnects.CompletedAsync });
        using HttpRequestMessage refused = new(HttpMethod.Get, $"http://127.0.0.1:{port}/");
        var refusal = await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(refused, ct));

        var started = Stopwatch.GetTimestamp();
        Assert.Equal(12_345, UpstreamConnects.TicksFor(refused, started, 12_345, refusal, out var reported));
        Assert.Equal(started, reported);
    }

    [Fact]
    public async Task AConnectTimeoutIsTheWholeForward()
    {
        var ct = TestContext.Current.CancellationToken;
        using HttpMessageInvoker client = new(new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromMilliseconds(100),
            ConnectCallback = static async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("the connect timeout never fired");
            },
            PlaintextStreamFilter = UpstreamConnects.CompletedAsync,
        });

        using HttpRequestMessage timedOut = new(HttpMethod.Get, "http://127.0.0.1:9/");
        var timeout = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(timedOut, ct));

        Assert.IsType<TimeoutException>(timeout.InnerException);
        Assert.Equal(777, UpstreamConnects.TicksFor(timedOut, 1, 777, timeout, out _));
    }

    [Fact]
    public void AForwardThatFailedOnAPooledConnectionOrWasAnsweredReportsNoConnect()
    {
        using HttpRequestMessage message = new();
        // A reset after connecting is no connect failure, nor is an answered forward (no error at all): the connect never finished
        // for the request that was handed another connection.
        Assert.Equal(0, UpstreamConnects.TicksFor(message, 1, 500, new HttpRequestException(HttpRequestError.Unknown), out var started));
        Assert.Equal(0, started);
        Assert.Equal(0, UpstreamConnects.TicksFor(message, 1, 500, new IOException("reset"), out _));
        Assert.Equal(0, UpstreamConnects.TicksFor(message, 1, 500, new OperationCanceledException(), out _));
        Assert.Equal(0, UpstreamConnects.TicksFor(message, 1, 500, failure: null, out _));
    }

    [Theory]
    [InlineData(HttpRequestError.ConnectionError)]
    [InlineData(HttpRequestError.NameResolutionError)]
    [InlineData(HttpRequestError.SecureConnectionError)]
    public void AFailureWhileConnectingIsTheWholeForward(HttpRequestError error)
    {
        using HttpRequestMessage message = new();
        Assert.Equal(500, UpstreamConnects.TicksFor(message, 1, 500, new HttpRequestException(error), out var started));
        Assert.Equal(1, started);
    }

    [Fact]
    public void LookingARequestUpAllocatesNothing()
    {
        using HttpRequestMessage message = new();
        AllocAssert.Zero(message, static m => _ = UpstreamConnects.TicksFor(m, 1, 1_000, null, out _), "UpstreamConnects.TicksFor");
    }

    // Answers each request on one kept-alive connection with an empty 200.
    private static async Task ServeAsync(TcpListener listener, int answers, CancellationToken ct)
    {
        using var socket = await listener.AcceptSocketAsync(ct);
        await using NetworkStream stream = new(socket);
        var buffer = new byte[4096];
        StringBuilder received = new();
        for (var answered = 0; answered < answers;)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0)
            {
                return;
            }

            received.Append(Encoding.ASCII.GetString(buffer, 0, read));
            while (received.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal) is var end and >= 0)
            {
                received.Remove(0, end + 4);
                await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n"u8.ToArray(), ct);
                answered++;
            }
        }
    }
}
