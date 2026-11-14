using System.Net;
using System.Net.Sockets;
using System.Text;

using Paduraksa.Proxy.Yarp;
using Paduraksa.Tests.Proxy.Testing;

namespace Paduraksa.Tests.Proxy.Yarp;

public sealed class UpstreamConnectsTests
{
    [Fact]
    public async Task TheRequestThatOpenedAConnectionReportsItsConnectAndOneOnThePooledConnectionNone()
    {
        var ct = TestContext.Current.CancellationToken;
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serving = ServeAsync(listener, answers: 2, ct);
        using HttpMessageInvoker client = new(new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = UpstreamConnects.Timed(null),
            PlaintextStreamFilter = UpstreamConnects.CompletedAsync,
        });

        using HttpRequestMessage first = new(HttpMethod.Get, $"http://127.0.0.1:{port}/a");
        using (var response = await client.SendAsync(first, ct))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using HttpRequestMessage second = new(HttpMethod.Get, $"http://127.0.0.1:{port}/b");
        using (var response = await client.SendAsync(second, ct))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        await serving;
        Assert.InRange(UpstreamConnects.TicksFor(first, long.MaxValue, answered: true), 1, long.MaxValue);
        Assert.Equal(0, UpstreamConnects.TicksFor(second, long.MaxValue, answered: true));
        // Never more than the forward it is part of.
        Assert.Equal(1, UpstreamConnects.TicksFor(first, 1, answered: true));
    }

    [Fact]
    public async Task AConnectThatNeverFinishedIsTheWholeForward()
    {
        var ct = TestContext.Current.CancellationToken;
        using HttpMessageInvoker client = new(new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = UpstreamConnects.Timed(static (_, _) => ValueTask.FromException<Stream>(new SocketException((int)SocketError.ConnectionRefused))),
            PlaintextStreamFilter = UpstreamConnects.CompletedAsync,
        });

        using HttpRequestMessage refused = new(HttpMethod.Get, "http://127.0.0.1:9/");
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(refused, ct));

        Assert.Equal(12_345, UpstreamConnects.TicksFor(refused, 12_345, answered: false));

        // The same unfinished connect on a forward that was answered anyway: the pool served it on a connection that freed up
        // while its own was still opening, so it paid no connect.
        Assert.Equal(0, UpstreamConnects.TicksFor(refused, 12_345, answered: true));
    }

    [Fact]
    public void LookingARequestUpAllocatesNothing()
    {
        using HttpRequestMessage message = new();
        AllocAssert.Zero(message, static m => _ = UpstreamConnects.TicksFor(m, 1_000, answered: true), "UpstreamConnects.TicksFor");
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
