using System.Diagnostics;
using System.Net.Security;
using System.Net.WebSockets;
using System.Text;

namespace Paduraksa.Bench.Soak;

/// <summary>
/// The long-lived and the slow: a few hundred WebSockets that chat slowly (a message every 20 to 60 seconds, well inside the upstream's 120-second idle timeout), a few that
/// stay silent and must be cut by that timeout, and slowloris-style connections that trickle a request header and must be cut by the listener's header timeout.
/// </summary>
internal sealed class LongLived(SoakOptions options, Recorder recorder, Events events)
{
    private const int SilentSockets = 5;
    private const int Slowloris = 20;
    private const int SlowBodies = 4;

    private readonly List<double> _idleCuts = [];
    private readonly List<double> _slowlorisCuts = [];
    private readonly List<double> _slowBodyCuts = [];
    private long _wsOpened;
    private long _wsMessages;
    private long _wsUnexpectedCloses;
    private long _slowlorisSurvived;
    private int _wsOpen;

    public int OpenSockets => Volatile.Read(ref _wsOpen);

    public long Opened => Interlocked.Read(ref _wsOpened);

    public long Messages => Interlocked.Read(ref _wsMessages);

    public long UnexpectedCloses => Interlocked.Read(ref _wsUnexpectedCloses);

    public long SlowlorisSurvived => Interlocked.Read(ref _slowlorisSurvived);

    public (double[] Idle, double[] Slowloris, double[] SlowBody) Cuts
    {
        get
        {
            lock (_idleCuts)
            {
                return ([.. _idleCuts], [.. _slowlorisCuts], [.. _slowBodyCuts]);
            }
        }
    }

    public Task RunAsync(CancellationToken ct)
    {
        List<Task> tasks = [];
        for (var i = 0; i < options.WebSockets; i++)
        {
            var seed = i;
            tasks.Add(Task.Run(() => ChattyAsync(seed, ct), ct));
        }

        for (var i = 0; i < SilentSockets; i++)
        {
            tasks.Add(Task.Run(() => SilentAsync(ct), ct));
        }

        for (var i = 0; i < Slowloris; i++)
        {
            tasks.Add(Task.Run(() => SlowlorisAsync(ct), ct));
        }

        for (var i = 0; i < SlowBodies; i++)
        {
            tasks.Add(Task.Run(() => SlowBodyAsync(ct), ct));
        }

        return Task.WhenAll(tasks);
    }

    private async Task<ClientWebSocket?> ConnectAsync(CancellationToken ct, bool silent = false)
    {
        ClientWebSocket socket = new();
        if (silent)
        {
            socket.Options.KeepAliveInterval = TimeSpan.Zero;
        }

        socket.Options.CollectHttpResponseDetails = false;
        try
        {
            using HttpMessageInvoker invoker = new(new SocketsHttpHandler
            {
                ConnectCallback = async (context, token) => await Loopback.ConnectAsync(context.DnsEndPoint.Port, token),
                SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = static (_, _, _, _) => true },
            });
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await socket.ConnectAsync(new Uri($"wss://{SoakConfig.Name("ws")}:{options.PublicHttps}/ws"), invoker, timeout.Token);
            Interlocked.Increment(ref _wsOpened);
            Interlocked.Increment(ref _wsOpen);
            return socket;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            socket.Dispose();
            recorder.Fail(Recorder.KindOf(ex), SoakConfig.Name("ws"), "/ws", "ws", "connect: " + (ex.InnerException?.Message ?? ex.Message));
            return null;
        }
    }

    private async Task ChattyAsync(int seed, CancellationToken ct)
    {
        Random random = new(options.Seed + seed);
        // Spread the connects over the first half minute, as clients arriving do.
        await Task.Delay(random.Next(0, 30_000), ct).ConfigureAwait(false);
        var counter = 0L;
        while (!ct.IsCancellationRequested)
        {
            var socket = await ConnectAsync(ct);
            if (socket is null)
            {
                await Delay(2000, ct);
                continue;
            }

            // A reader of its own: a socket the server closes is seen at once, not when the next message fails to send half a minute later.
            TaskCompletionSource<byte[]> echo = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var reader = Task.Run(async () =>
            {
                var buffer = new byte[256];
                try
                {
                    while (true)
                    {
                        var result = await socket.ReceiveAsync(buffer, ct);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            return "closed by the server";
                        }

                        echo.TrySetResult(buffer.AsSpan(0, result.Count).ToArray());
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    return ex.InnerException?.Message ?? ex.Message;
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            }, CancellationToken.None);
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var wake = Task.Delay(random.Next(20_000, 60_000), ct);
                    if (await Task.WhenAny(wake, reader) == reader)
                    {
                        var why = await reader;
                        if (why is not null)
                        {
                            Interlocked.Increment(ref _wsUnexpectedCloses);
                            recorder.Fail("ws-closed", SoakConfig.Name("ws"), "/ws", "ws", why);
                        }

                        break;
                    }

                    var message = Encoding.UTF8.GetBytes($"soak-{seed}-{counter++}");
                    echo = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    var sent = Stopwatch.GetTimestamp();
                    await socket.SendAsync(message, WebSocketMessageType.Text, true, ct);
                    var answered = await Task.WhenAny(echo.Task, reader, Task.Delay(TimeSpan.FromSeconds(10), ct));
                    if (answered != echo.Task)
                    {
                        Interlocked.Increment(ref _wsUnexpectedCloses);
                        recorder.Fail(answered == reader ? "ws-closed" : "timeout", SoakConfig.Name("ws"), "/ws", "ws", answered == reader ? (await reader ?? "closed") : "no echo within 10 s");
                        break;
                    }

                    if (!(await echo.Task).AsSpan().SequenceEqual(message))
                    {
                        recorder.Fail("wrong", SoakConfig.Name("ws"), "/ws", "ws", "the echo was not the message sent");
                        continue;
                    }

                    Interlocked.Increment(ref _wsMessages);
                    recorder.Done(Series.Ws, Stopwatch.GetElapsedTime(sent).TotalMicroseconds, 200, false, SoakConfig.Name("ws"));
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Interlocked.Increment(ref _wsUnexpectedCloses);
                recorder.Fail(ex is WebSocketException ? "ws-closed" : Recorder.KindOf(ex), SoakConfig.Name("ws"), "/ws", "ws", ex.InnerException?.Message ?? ex.Message);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                Interlocked.Decrement(ref _wsOpen);
                socket.Dispose();
            }

            await Delay(random.Next(500, 3000), ct);
        }
    }

    /// <summary>A WebSocket that never speaks: the upstream's idle timeout is what ends it, and the time it took is the fact.</summary>
    private async Task SilentAsync(CancellationToken ct)
    {
        // No pings: ClientWebSocket sends one every 30 s by default, and a ping is data in both directions, so the upstream's idle timer would never run out.
        await Delay(Random.Shared.Next(1000, 15_000), ct);
        var buffer = new byte[16];
        while (!ct.IsCancellationRequested)
        {
            var socket = await ConnectAsync(ct, silent: true);
            if (socket is null)
            {
                await Delay(2000, ct);
                continue;
            }

            var opened = Stopwatch.GetTimestamp();
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    var result = await socket.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
            }
            finally
            {
                Interlocked.Decrement(ref _wsOpen);
                socket.Dispose();
            }

            if (ct.IsCancellationRequested)
            {
                return;
            }

            var seconds = Stopwatch.GetElapsedTime(opened).TotalSeconds;
            if (!recorder.Tolerance.Applies(events.ElapsedSeconds, SoakConfig.Name("ws")))
            {
                lock (_idleCuts)
                {
                    _idleCuts.Add(Math.Round(seconds, 1));
                }
            }

            await Delay(1000, ct);
        }
    }

    /// <summary>Sends a request's first header line and then a byte every few seconds, forever: the listener's header timeout is what ends it.</summary>
    private async Task SlowlorisAsync(CancellationToken ct)
    {
        await Delay(Random.Shared.Next(1000, 10_000), ct);
        while (!ct.IsCancellationRequested)
        {
            var opened = Stopwatch.GetTimestamp();
            try
            {
                using var stream = await Loopback.ConnectAsync(options.PublicHttp, ct);
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET /slow HTTP/1.1\r\nHost: {SoakConfig.Name("app01")}\r\nX-Slow: "), ct);
                using CancellationTokenSource watchdog = CancellationTokenSource.CreateLinkedTokenSource(ct);
                watchdog.CancelAfter(TimeSpan.FromSeconds(90));
                var cut = false;
                var reader = Task.Run(async () =>
                {
                    var sink = new byte[512];
                    try
                    {
                        // The server's 408 and then the end of the stream, or a reset: either way the connection is gone.
                        while (await stream.ReadAsync(sink, watchdog.Token) > 0)
                        {
                        }
                    }
                    catch (Exception) when (!ct.IsCancellationRequested)
                    {
                    }

                    cut = true;
                    await watchdog.CancelAsync();
                }, ct);
                try
                {
                    while (!cut)
                    {
                        await Task.Delay(3000, watchdog.Token);
                        await stream.WriteAsync("a"u8.ToArray(), watchdog.Token);
                    }
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                }

                await reader.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
                var seconds = Stopwatch.GetElapsedTime(opened).TotalSeconds;
                if (!cut)
                {
                    Interlocked.Increment(ref _slowlorisSurvived);
                }

                if (!recorder.Tolerance.Applies(events.ElapsedSeconds, SoakConfig.Name("app01")))
                {
                    lock (_idleCuts)
                    {
                        _slowlorisCuts.Add(Math.Round(cut ? seconds : 999, 1));
                    }
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                await Delay(1000, ct);
            }
        }
    }

    /// <summary>A POST that promises a large body and sends it at 10 bytes a second: below the minimum body rate, so the listener ends it after its grace.</summary>
    private async Task SlowBodyAsync(CancellationToken ct)
    {
        await Delay(Random.Shared.Next(1000, 20_000), ct);
        while (!ct.IsCancellationRequested)
        {
            var opened = Stopwatch.GetTimestamp();
            try
            {
                using var stream = await Loopback.ConnectAsync(options.PublicHttp, ct);
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"POST /upload HTTP/1.1\r\nHost: {SoakConfig.Name("upload")}\r\nContent-Length: 1000000\r\n\r\n"), ct);
                using CancellationTokenSource watchdog = CancellationTokenSource.CreateLinkedTokenSource(ct);
                watchdog.CancelAfter(TimeSpan.FromSeconds(120));
                var cut = false;
                var reader = Task.Run(async () =>
                {
                    var sink = new byte[512];
                    try
                    {
                        while (await stream.ReadAsync(sink, watchdog.Token) > 0)
                        {
                        }
                    }
                    catch (Exception) when (!ct.IsCancellationRequested)
                    {
                    }

                    cut = true;
                    await watchdog.CancelAsync();
                }, ct);
                try
                {
                    while (!cut)
                    {
                        await Task.Delay(1000, watchdog.Token);
                        await stream.WriteAsync(new byte[10], watchdog.Token);
                    }
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                }

                await reader.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
                if (!recorder.Tolerance.Applies(events.ElapsedSeconds, SoakConfig.Name("upload")))
                {
                    lock (_idleCuts)
                    {
                        _slowBodyCuts.Add(Math.Round(cut ? Stopwatch.GetElapsedTime(opened).TotalSeconds : 999, 1));
                    }
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                await Delay(1000, ct);
            }
        }
    }

    private static async Task Delay(int milliseconds, CancellationToken ct)
    {
        try
        {
            await Task.Delay(milliseconds, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
