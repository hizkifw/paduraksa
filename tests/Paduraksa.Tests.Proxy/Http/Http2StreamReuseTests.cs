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
/// <para>In production the writer runs on the thread pool, and the second resume must land in the microseconds before the writer's flush: about
/// one response in a million in the soak. Here Kestrel's scheduler is set to inline (a test-only reach into its internals): a request runs as
/// its HEADERS frame is read, and a parked writer runs on the thread that queues to it. Both resumes happen under the connection-window lock,
/// so the second lands after the first's held flush only if that first runs the writer inline, on the reading thread.</para>
/// <para>Every step waits on an ordering, never on time. Kestrel's writer takes its queue in order, so a marker (a headers-only response, which
/// no window holds back) answered after a step means the writer has finished that step. A marker whose flush runs inside its own request
/// means the writer was parked, and is parked again when the request returns. The client seeing the bytes is not enough: the writer can
/// still be finishing the first 64 KiB, the stream WINDOW_UPDATE then overtakes it, and the race doesn't happen (seen on CI).</para>
/// </remarks>
public sealed class Http2StreamReuseTests
{
    // HTTP/2's initial windows, stream and connection, which this client never raises until the test says.
    private const int DefaultWindow = 65_535;

    // Past both windows: 65 535 bytes go at once, the rest waits for both WINDOW_UPDATEs.
    private const int Rest = 100;

    // A writer that isn't parked yet has a few entries left at most; a marker runs inline long before this.
    private const int InlineAttempts = 20;

    [ThreadStatic]
    private static bool s_inMarker;

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
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FollowUps followUps = new();
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
                        case "/marker":
                            // Not awaited inside the flag: were it to go async, the flag would stay set on this thread.
                            s_inMarker = true;
                            var completed = context.Response.CompleteAsync();
                            s_inMarker = false;
                            return completed;
                        case "/held":
                            return AnswerAsync(context, releaseHeld.Task);
                        case "/probe":
                            probeStarted.TrySetResult();
                            return context.Response.WriteAsync("ok", context.RequestAborted);
                        default:
                            return followUps.AnswerAsync(context, release.Task);
                    }
                })))
            .Build();
        RunInline(host.Services.GetRequiredService<IServer>());
        await host.StartAsync(ct);
        try
        {
            var port = new Uri(Assert.Single(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses)).Port;
            await using var client = await RawHttp2Client.ConnectAsync(port, ct);
            var held = client.Get("/held");

            // Both windows are used up and the rest waits for the connection's: /big's producer is the connection window's last consumer.
            var big = client.Get("/big");
            await MarkerAsync(client, gate, ct);
            // The stream's window opens and the connection's is still shut, so the producer queues for it a second time.
            client.WindowUpdate(big, Rest);
            await InlineMarkerAsync(client, gate, ct);

            gate.Close();
            // Resumed twice: the first writes the last frame (its flush held here), the second queues the producer again.
            client.WindowUpdate(0, DefaultWindow);
            // Frames are handled in order, and a request runs as its frame is read: once /probe runs, both resumes have happened.
            var probe = client.Get("/probe");
            await probeStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            Assert.Equal(1, gate.Held);

            gate.Open();
            await client.Completed(big).WaitAsync(TimeSpan.FromSeconds(10), ct);
            await client.Completed(probe).WaitAsync(TimeSpan.FromSeconds(10), ct);
            // /big's stream ended in the writer before it took this response, and the connection pools ended streams as it reads the next
            // frames. A marker here would be read first, and could take /big's stream.
            releaseHeld.TrySetResult();
            await client.Completed(held).WaitAsync(TimeSpan.FromSeconds(10), ct);

            // Held open, each follow-up takes a pooled stream or a new one, and there are no more pooled streams than streams so far: one of
            // them is on /big's.
            followUps.Expect(client.Opened);
            int[] streams = [.. Enumerable.Range(0, client.Opened).Select(_ => client.Get("/follow-up"))];
            await followUps.Started.WaitAsync(TimeSpan.FromSeconds(10), ct);
            release.TrySetResult();
            await followUps.Answered.WaitAsync(TimeSpan.FromSeconds(10), ct);
            // Every follow-up's response is queued to the writer or lost, and the writer takes this marker after all of them.
            await MarkerAsync(client, gate, ct);
            return [.. streams.Where(stream => !client.Completed(stream).IsCompleted)];
        }
        finally
        {
            gate.Open();
            release.TrySetResult();
            releaseHeld.TrySetResult();
            // A lost response's stream never completes, so its connection never drains: the stop gives up on it instead of waiting out
            // the host's 30 s (in production, Kestrel's "application never completed").
            using CancellationTokenSource stop = new(TimeSpan.FromSeconds(1));
            await host.StopAsync(stop.Token);
        }
    }

    private static async Task AnswerAsync(HttpContext context, Task release)
    {
        await release;
        await context.Response.CompleteAsync();
    }

    private static async Task MarkerAsync(RawHttp2Client client, FlushGate gate, CancellationToken ct) =>
        _ = await TryMarkerAsync(client, gate, ct);

    /// <summary>Until a marker runs the writer inline: then every earlier step is done, and the writer is parked for the next frame.</summary>
    private static async Task InlineMarkerAsync(RawHttp2Client client, FlushGate gate, CancellationToken ct)
    {
        for (var attempt = 0; attempt < InlineAttempts; attempt++)
        {
            if (await TryMarkerAsync(client, gate, ct))
            {
                return;
            }
        }

        Assert.Fail($"Kestrel's HTTP/2 writer didn't run inline in {InlineAttempts} markers: the reproduction depends on its inline scheduler.");
    }

    /// <returns>Whether the marker's flush ran inside its own request.</returns>
    private static async Task<bool> TryMarkerAsync(RawHttp2Client client, FlushGate gate, CancellationToken ct)
    {
        var before = gate.InlineMarkerFlushes;
        await client.Completed(client.Get("/marker")).WaitAsync(TimeSpan.FromSeconds(10), ct);
        return gate.InlineMarkerFlushes > before;
    }

    /// <summary>Kestrel's own tests run its connections on the inline scheduler; production has the thread pool, and no option for this.</summary>
    private static void RunInline(IServer server)
    {
        var context = server.GetType().GetProperty("ServiceContext", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
        context.GetType().GetProperty("Scheduler")!.SetValue(context, PipeScheduler.Inline);
    }

    /// <summary>The follow-up requests: each holds its stream until the test releases them all, then answers with headers only.</summary>
    private sealed class FollowUps
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _answered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _expected = int.MaxValue;
        private int _startedCount;
        private int _answeredCount;

        public Task Started => _started.Task;

        public Task Answered => _answered.Task;

        /// <summary>Before the first follow-up is sent.</summary>
        public void Expect(int count) => _expected = count;

        public async Task AnswerAsync(HttpContext context, Task release)
        {
            if (Interlocked.Increment(ref _startedCount) == _expected)
            {
                _started.TrySetResult();
            }

            // Returns once the response is queued to the writer, or, on the stream that can't send, as soon as its queueing was skipped.
            await Http2StreamReuseTests.AnswerAsync(context, release);
            if (Interlocked.Increment(ref _answeredCount) == _expected)
            {
                _answered.TrySetResult();
            }
        }
    }

    /// <summary>Holds the connection's output flushes while closed; open, it lets every flush through.</summary>
    private sealed class FlushGate : IDisposable
    {
        private readonly SemaphoreSlim _permits = new(0);
        private volatile bool _closed;
        private int _held;
        private int _inlineMarkerFlushes;

        public int Held => Volatile.Read(ref _held);

        /// <summary>Flushes that ran inside a marker request: the writer ran on the request's own thread.</summary>
        public int InlineMarkerFlushes => Volatile.Read(ref _inlineMarkerFlushes);

        public void Close() => _closed = true;

        public void Open()
        {
            _closed = false;
            _permits.Release(Math.Max(1, Held));
        }

        public void Dispose() => _permits.Dispose();

        public async ValueTask WaitAsync(CancellationToken ct)
        {
            if (s_inMarker)
            {
                Interlocked.Increment(ref _inlineMarkerFlushes);
            }

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

    /// <summary>Just enough HTTP/2 to send GETs and WINDOW_UPDATEs when the test says, and to see what each stream got.</summary>
    private sealed class RawHttp2Client : IAsyncDisposable
    {
        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;
        private readonly ConcurrentDictionary<int, TaskCompletionSource> _ended = new();
        private readonly TaskCompletionSource _settingsAcked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _writeLock = new();
        private Task _reader = Task.CompletedTask;
        private int _nextStream = 1;

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

        /// <summary>The streams opened so far.</summary>
        public int Opened => _nextStream / 2;

        /// <returns>The new stream's ID.</returns>
        public int Get(string path)
        {
            var stream = _nextStream;
            _nextStream += 2;
            // :method GET, :scheme http (static table), then :path and :authority as literals with indexed names.
            List<byte> block = [0x82, 0x86];
            Literal(block, 0x04, path);
            Literal(block, 0x01, "localhost");
            List<byte> frame = [];
            Frame(frame, 0x1, 0x5, stream, [.. block]); // HEADERS, END_HEADERS | END_STREAM
            Ended(stream);
            Write([.. frame]);
            return stream;
        }

        /// <param name="stream">0 for the connection's window.</param>
        /// <param name="increment">The bytes the window grows by.</param>
        public void WindowUpdate(int stream, int increment)
        {
            List<byte> frame = [];
            Frame(frame, 0x8, 0x0, stream, [(byte)(increment >> 24), (byte)(increment >> 16), (byte)(increment >> 8), (byte)increment]);
            Write([.. frame]);
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
