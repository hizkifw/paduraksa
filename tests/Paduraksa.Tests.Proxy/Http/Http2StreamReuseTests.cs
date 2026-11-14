using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Paduraksa.Proxy.Http;

namespace Paduraksa.Tests.Proxy.Http;

/// <summary>
/// Kestrel's lost HTTP/2 response on a reused stream (dotnet/aspnetcore#69690; <see cref="KestrelHttp2StreamReuse"/>), made to happen every time. A
/// response that used up both its stream's and the connection's send window waits twice in Kestrel's connection-window queue (once as the
/// window's last consumer, once queued again after a stream WINDOW_UPDATE found the connection's window still shut). The connection
/// WINDOW_UPDATE then resumes it twice: the first resume writes its last frame, and the second, while that frame's flush is held, queues it
/// once more. The writer drops that entry because the response is complete, and the stream goes back to the pool unable to send.
/// </summary>
/// <remarks>
/// In production the writer runs on the thread pool, and the second resume must land in the microseconds before the writer's flush: about one
/// response in a million in the soak. Here Kestrel's scheduler is set to inline (a test-only reach into its internals), so the first resume runs
/// the writer on the reading thread up to the flush the test holds, and the second always comes after it.
/// </remarks>
public sealed class Http2StreamReuseTests
{
    // HTTP/2's initial windows, stream and connection, which this client never raises until the test says.
    private const int DefaultWindow = 65_535;

    // Past both windows: 65 535 bytes go at once, the rest waits for both WINDOW_UPDATEs.
    private const int Rest = 100;

    // Two streams go back to the pool, /big's and /probe's; that many follow-ups reach /big's whatever the pool's order.
    private const int FollowUps = 3;

    [Fact]
    public async Task PlainKestrelLosesTheResponseOfARequestOnTheReusedStream()
    {
        var unanswered = await RunAsync(heal: false);

        if (KestrelHttp2StreamReuse.Applies)
        {
            Assert.Single(unanswered);
        }
        else
        {
            // The running Kestrel has upstream's fix: KestrelHttp2StreamReuse has turned itself off and can be deleted.
            Assert.Empty(unanswered);
        }
    }

    [Fact]
    public async Task TheWorkaroundLetsTheReusedStreamAnswer()
    {
        var healedBefore = KestrelHttp2StreamReuse.Healed;

        var unanswered = await RunAsync(heal: true);

        Assert.Empty(unanswered);
        if (KestrelHttp2StreamReuse.Applies)
        {
            Assert.True(KestrelHttp2StreamReuse.Healed > healedBefore);
        }
    }

    /// <returns>The follow-up streams that got no response.</returns>
    private static async Task<IReadOnlyList<int>> RunAsync(bool heal)
    {
        var ct = TestContext.Current.CancellationToken;
        using FlushGate gate = new();
        TaskCompletionSource probeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen =>
                {
                    listen.Protocols = HttpProtocols.Http2;
                    listen.Use(next => connection =>
                    {
                        connection.Transport = new GatedTransport(connection.Transport, gate);
                        return next(connection);
                    });
                }))
                .Configure(app => app.Run(context =>
                {
                    if (heal)
                    {
                        KestrelHttp2StreamReuse.BeforeRequest(context);
                    }

                    switch (context.Request.Path.Value)
                    {
                        case "/big":
                            // Advanced without a flush, so the whole body and the end of the response reach the writer together.
                            var body = context.Response.BodyWriter;
                            for (var left = DefaultWindow + Rest; left > 0;)
                            {
                                var chunk = Math.Min(left, body.GetMemory(left).Length);
                                body.Advance(chunk);
                                left -= chunk;
                            }

                            return Task.CompletedTask;
                        case "/probe":
                            probeStarted.TrySetResult();
                            break;
                    }

                    return context.Response.WriteAsync("ok", context.RequestAborted);
                })))
            .Build();
        RunInline(host.Services.GetRequiredService<IServer>());
        await host.StartAsync(ct);
        try
        {
            var port = new Uri(Assert.Single(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses)).Port;
            await using var client = await RawHttp2Client.ConnectAsync(port, ct);

            // Both windows are used up and the rest waits for the connection's: /big's producer is the connection window's last consumer.
            client.Get(1, "/big");
            await WaitUntilAsync(() => client.DataBytes(1) == DefaultWindow);
            // The stream's window opens and the connection's is still shut, so the producer queues for it a second time.
            client.WindowUpdate(1, Rest);
            await client.PingAsync().WaitAsync(TimeSpan.FromSeconds(10), ct);

            gate.Close();
            // Resumed twice: the first writes the last frame (its flush held here), the second queues the producer again.
            client.WindowUpdate(0, DefaultWindow);
            // Frames are handled in order, and with the inline scheduler a request runs as its frame is read: once /probe runs, the
            // WINDOW_UPDATE's resumes have both happened.
            client.Get(3, "/probe");
            await probeStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            Assert.Equal(1, gate.Held);

            gate.Open();
            await client.Completed(1).WaitAsync(TimeSpan.FromSeconds(10), ct);
            await client.Completed(3).WaitAsync(TimeSpan.FromSeconds(10), ct);
            // The ping's answer means the connection's loop has run since both ended, and it pools ended streams on every pass.
            await client.PingAsync().WaitAsync(TimeSpan.FromSeconds(10), ct);

            int[] followUps = [.. Enumerable.Range(0, FollowUps).Select(i => 5 + (2 * i))];
            foreach (var stream in followUps)
            {
                client.Get(stream, "/small");
            }

            var all = Task.WhenAll(followUps.Select(client.Completed));
            if (!heal)
            {
                // At most one of them can have landed on /big's stream: the others answer, however slow the machine.
                await WaitUntilAsync(() => followUps.Count(stream => client.Completed(stream).IsCompleted) >= FollowUps - 1);
            }

            // An absence window for the last: on loopback a response that is coming arrives in milliseconds, one left in a stream that can't send
            // never does.
            await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(heal ? 10 : 1), ct));
            return [.. followUps.Where(stream => !client.Completed(stream).IsCompleted)];
        }
        finally
        {
            gate.Open();
            // A lost response's stream never completes, so its connection never drains: the stop gives up on it instead of waiting out
            // the host's 30 s (in production, Kestrel's "application never completed").
            using CancellationTokenSource stop = new(TimeSpan.FromSeconds(1));
            await host.StopAsync(stop.Token);
        }
    }

    /// <summary>Kestrel's own tests run its connections on the inline scheduler; production has the thread pool, and no option for this.</summary>
    private static void RunInline(IServer server)
    {
        var context = server.GetType().GetProperty("ServiceContext", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
        context.GetType().GetProperty("Scheduler")!.SetValue(context, PipeScheduler.Inline);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for the server");
            // A short poll step inside a bounded wait: the byte and response counts have no signal of their own.
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Holds the connection's output flushes while closed; open, it lets every flush through.</summary>
    private sealed class FlushGate : IDisposable
    {
        private readonly SemaphoreSlim _permits = new(0);
        private volatile bool _closed;
        private int _held;

        public int Held => Volatile.Read(ref _held);

        public void Close() => _closed = true;

        public void Open()
        {
            _closed = false;
            _permits.Release(Math.Max(1, Held));
        }

        public void Dispose() => _permits.Dispose();

        public async ValueTask WaitAsync(CancellationToken ct)
        {
            if (!_closed)
            {
                return;
            }

            Interlocked.Increment(ref _held);
            await _permits.WaitAsync(ct);
        }
    }

    private sealed class GatedTransport(IDuplexPipe inner, FlushGate gate) : IDuplexPipe
    {
        public PipeReader Input => inner.Input;

        public PipeWriter Output { get; } = new GatedWriter(inner.Output, gate);
    }

    private sealed class GatedWriter(PipeWriter inner, FlushGate gate) : PipeWriter
    {
        public override void Advance(int bytes) => inner.Advance(bytes);

        public override Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);

        public override Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);

        public override void CancelPendingFlush() => inner.CancelPendingFlush();

        public override void Complete(Exception? exception = null) => inner.Complete(exception);

        public override async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            await gate.WaitAsync(cancellationToken);
            return await inner.FlushAsync(cancellationToken);
        }
    }

    /// <summary>Just enough HTTP/2 to send GETs, WINDOW_UPDATEs and PINGs when the test says, and to see what each stream got.</summary>
    private sealed class RawHttp2Client : IAsyncDisposable
    {
        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;
        private readonly ConcurrentDictionary<int, TaskCompletionSource> _ended = new();
        private readonly ConcurrentDictionary<int, long> _dataBytes = new();
        private readonly TaskCompletionSource _settingsAcked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _writeLock = new();
        private TaskCompletionSource _pong = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task _reader = Task.CompletedTask;

        private RawHttp2Client(TcpClient tcp)
        {
            _tcp = tcp;
            _stream = tcp.GetStream();
        }

        public static async Task<RawHttp2Client> ConnectAsync(int port, CancellationToken ct)
        {
            TcpClient tcp = new() { NoDelay = true };
            await tcp.ConnectAsync(IPAddress.Loopback, port, ct);
            RawHttp2Client client = new(tcp);
            List<byte> hello = [.. "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"u8];
            Frame(hello, 0x4, 0x0, 0, []); // SETTINGS, all defaults
            client.Write([.. hello]);
            client._reader = Task.Run(client.ReadAsync, CancellationToken.None);
            await client._settingsAcked.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            return client;
        }

        public Task Completed(int stream) => Ended(stream).Task;

        public long DataBytes(int stream) => _dataBytes.GetValueOrDefault(stream);

        public void Get(int stream, string path)
        {
            // :method GET, :scheme http (static table), then :path and :authority as literals with indexed names.
            List<byte> block = [0x82, 0x86];
            Literal(block, 0x04, path);
            Literal(block, 0x01, "localhost");
            List<byte> frame = [];
            Frame(frame, 0x1, 0x5, stream, [.. block]); // HEADERS, END_HEADERS | END_STREAM
            Ended(stream);
            Write([.. frame]);
        }

        /// <param name="stream">0 for the connection's window.</param>
        /// <param name="increment">The bytes the window grows by.</param>
        public void WindowUpdate(int stream, int increment)
        {
            List<byte> frame = [];
            Frame(frame, 0x8, 0x0, stream, [(byte)(increment >> 24), (byte)(increment >> 16), (byte)(increment >> 8), (byte)increment]);
            Write([.. frame]);
        }

        public Task PingAsync()
        {
            _pong = new(TaskCreationOptions.RunContinuationsAsynchronously);
            List<byte> frame = [];
            Frame(frame, 0x6, 0x0, 0, new byte[8]);
            Write([.. frame]);
            return _pong.Task;
        }

        public async ValueTask DisposeAsync()
        {
            _tcp.Dispose();
            try
            {
                await _reader;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or EndOfStreamException)
            {
            }
        }

        private TaskCompletionSource Ended(int stream) => _ended.GetOrAdd(stream, static _ => new(TaskCreationOptions.RunContinuationsAsynchronously));

        private void Write(byte[] bytes)
        {
            lock (_writeLock)
            {
                _stream.Write(bytes);
            }
        }

        private async Task ReadAsync()
        {
            var header = new byte[9];
            while (true)
            {
                await _stream.ReadExactlyAsync(header);
                var payload = new byte[(header[0] << 16) | (header[1] << 8) | header[2]];
                await _stream.ReadExactlyAsync(payload);
                var (type, flags) = (header[3], header[4]);
                var stream = ((header[5] & 0x7f) << 24) | (header[6] << 16) | (header[7] << 8) | header[8];
                if (type == 0x0)
                {
                    _dataBytes.AddOrUpdate(stream, payload.Length, (_, total) => total + payload.Length);
                }

                switch (type)
                {
                    case 0x4 when (flags & 0x1) != 0:
                        _settingsAcked.TrySetResult();
                        break;
                    case 0x4:
                        List<byte> ack = [];
                        Frame(ack, 0x4, 0x1, 0, []);
                        Write([.. ack]);
                        break;
                    case 0x6 when (flags & 0x1) != 0:
                        _pong.TrySetResult();
                        break;
                    case 0x0 or 0x1 when (flags & 0x1) != 0: // DATA or HEADERS with END_STREAM
                        Ended(stream).TrySetResult();
                        break;
                }
            }
        }

        private static void Literal(List<byte> block, byte nameIndex, string value)
        {
            var bytes = Encoding.ASCII.GetBytes(value);
            block.Add(nameIndex);
            block.Add((byte)bytes.Length);
            block.AddRange(bytes);
        }

        private static void Frame(List<byte> to, byte type, byte flags, int stream, byte[] payload)
        {
            to.AddRange([(byte)(payload.Length >> 16), (byte)(payload.Length >> 8), (byte)payload.Length, type, flags]);
            to.AddRange([(byte)(stream >> 24), (byte)(stream >> 16), (byte)(stream >> 8), (byte)stream]);
            to.AddRange(payload);
        }
    }
}
