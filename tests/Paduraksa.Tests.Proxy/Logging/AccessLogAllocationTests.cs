using System.Net;

using Microsoft.AspNetCore.Http;

using Paduraksa.Proxy;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Logging;
using Paduraksa.Tests.Proxy.Testing;
using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.Logging;

public sealed class AccessLogAllocationTests(TestUpstream upstream) : IClassFixture<TestUpstream>
{
    private const int Connections = 4;
    private const int Warmup = 500;
    private const int Measured = 1_500;

    [Theory]
    [InlineData("app.test", "/api/items", HttpStatusCode.OK)]
    [InlineData("app.test", "/blocked", HttpStatusCode.Forbidden)]
    [InlineData("unknown.test", "/", HttpStatusCode.NotFound)]
    public async Task TheGateAllocatesNothingWithTheAccessLogOn(string host, string path, HttpStatusCode expected)
    {
        ProxyConfigModel model = new()
        {
            Upstreams = [Upstream("web", upstream.Address("a"))],
            Hosts =
            [
                ProxyHost(
                    "app",
                    "app.test",
                    Route("app-blocked", "web", "/blocked") with { Policies = [new DenyPolicySpec()] },
                    Route("app-api", "web", "/api"),
                    Route("app-root", "web")),
                ProxyHost("off", "quiet.test", Route("off-root", "web")) with { AccessLog = false },
            ],
        };
        await using var server = await ProxyTestServer.StartAsync(model, new() { MeasureGate = true });
        using var client = ProxyTestServer.CreateClient(maxConnections: Connections);

        await SendAsync(server, client, host, path, expected, Warmup);
        server.GateStats!.Reset();
        await SendAsync(server, client, host, path, expected, Measured);

        Assert.True(server.GateStats.Requests >= Measured, $"only {server.GateStats.Requests} requests measured");
        Assert.True(server.GateStats.Bytes == 0, $"gate allocated on Kestrel with the access log on: {server.GateStats}");
        // A line per request, in order, none dropped: the log really was on for these requests. The lines are written
        // from the response's completion, which can trail the client's last read by a moment.
        await WaitForEntriesAsync(server.AccessLog, Measured);
        Assert.Equal(0, server.AccessLog.Dropped);
        Assert.True(server.AccessLog.TryRead(out var entry));
        Assert.Equal(path, entry.Path);
        Assert.False(string.IsNullOrEmpty(entry.RequestId));
    }

    /// <summary>
    /// The whole access-log path on a real keep-alive connection: the completion registration, the callback's recording
    /// and the reapply hook's header work. Each is measured on the request thread, per request, the same way the gate's
    /// decision is; the callbacks themselves run after the response, so the probe runs the same static entry points the
    /// callbacks call, with the state, ring and headers the request really has. Its registrations come on top of the
    /// pipeline's own, so each measured request writes two identical lines: the test counts no lines on purpose.
    /// </summary>
    [Fact]
    public async Task TheCompletionAndReapplyCallbacksAllocateNothingOnKestrel()
    {
        ProxyConfigModel model = new()
        {
            Upstreams = [Upstream("web", upstream.Address("a"))],
            Hosts = [ProxyHost("app", "app.test", Route("app-root", "web"))],
        };
        CompletionProbe probe = new();
        await using var server = await ProxyTestServer.StartAsync(model, new() { AfterGate = probe.Invoke });
        using var client = ProxyTestServer.CreateClient(maxConnections: Connections);

        await SendAsync(server, client, "app.test", "/api/items", HttpStatusCode.OK, Warmup);
        probe.Reset();
        probe.Enabled = true;
        await SendAsync(server, client, "app.test", "/api/items", HttpStatusCode.OK, Measured);
        probe.Enabled = false;

        Assert.True(probe.Requests >= Measured, $"only {probe.Requests} requests measured");
        Assert.True(probe.RegistrationBytes == 0, $"the OnCompleted and OnStarting registrations allocated {probe.RegistrationBytes} B over {probe.Requests} requests");
        Assert.True(probe.RecordingBytes == 0, $"the access-log callback allocated {probe.RecordingBytes} B over {probe.Requests} requests");
        Assert.True(probe.ReapplyBytes == 0, $"the reapply callback allocated {probe.ReapplyBytes} B over {probe.Requests} requests");
        Assert.True(probe.ResponseStartBytes == 0, $"the response-start hook allocated {probe.ResponseStartBytes} B over {probe.Requests} requests on the successful path");
        await WaitForEntriesAsync(server.AccessLog, Measured);
        Assert.Equal(0, server.AccessLog.Dropped);
    }

    [Fact]
    public async Task AHostWithTheLogOffIsProxiedAndNotLogged()
    {
        ProxyConfigModel model = new()
        {
            Upstreams = [Upstream("web", upstream.Address("a"))],
            Hosts = [ProxyHost("off", "quiet.test", Route("off-root", "web")) with { AccessLog = false }],
        };
        await using var server = await ProxyTestServer.StartAsync(model);
        // One connection: the second request is only served once the first request's completion callback has run, so a
        // line the first shouldn't have written would be in the ring by then.
        using var client = ProxyTestServer.CreateClient(maxConnections: 1);

        using var first = await client.SendAsync(server.Request("quiet.test", "/x"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("/a/x", await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        using var second = await client.SendAsync(server.Request("quiet.test", "/x"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(0, server.AccessLog.EntriesWritten);
    }

    private static async Task WaitForEntriesAsync(AccessLogRing ring, long count)
    {
        for (var attempt = 0; attempt < 2_000 && ring.EntriesWritten < count; attempt++)
        {
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }

        Assert.True(ring.EntriesWritten >= count, $"only {ring.EntriesWritten} lines were queued, expected {count}");
    }

    /// <summary>
    /// Runs the three pieces of the access-log path that a real server runs around a forwarded request, with the state,
    /// ring and headers that request really has, and sums what each allocates on the request thread.
    /// </summary>
    private sealed class CompletionProbe
    {
        private volatile bool _enabled;
        private long _requests;
        private long _registrationBytes;
        private long _recordingBytes;
        private long _reapplyBytes;
        private long _responseStartBytes;

        public bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        public long Requests => Volatile.Read(ref _requests);

        public long RegistrationBytes => Volatile.Read(ref _registrationBytes);

        public long RecordingBytes => Volatile.Read(ref _recordingBytes);

        public long ReapplyBytes => Volatile.Read(ref _reapplyBytes);

        public long ResponseStartBytes => Volatile.Read(ref _responseStartBytes);

        public void Reset()
        {
            Volatile.Write(ref _requests, 0);
            Volatile.Write(ref _registrationBytes, 0);
            Volatile.Write(ref _recordingBytes, 0);
            Volatile.Write(ref _reapplyBytes, 0);
            Volatile.Write(ref _responseStartBytes, 0);
        }

        public Task Invoke(HttpContext context, RequestDelegate next)
        {
            if (!_enabled)
            {
                return next(context);
            }

            var state = context.Features.Get<IGateFeature>()!.State;
            var ring = state.AccessLogRing!;
            var started = state.RequestStartedTimestamp;
            // The gate materialises these before this middleware, so measuring them would only show their cached path.
            _ = state.RequestIdText();
            _ = state.FormattedClientAddress();

            var before = GC.GetAllocatedBytesForCurrentThread();
            AccessLogMiddleware.RegisterCompletion(context);
            var registration = GC.GetAllocatedBytesForCurrentThread() - before;

            before = GC.GetAllocatedBytesForCurrentThread();
            ProxyHostingExtensions.RegisterReapply(context);
            registration += GC.GetAllocatedBytesForCurrentThread() - before;

            before = GC.GetAllocatedBytesForCurrentThread();
            AccessLogMiddleware.Record(context, ring, started);
            var recording = GC.GetAllocatedBytesForCurrentThread() - before;

            before = GC.GetAllocatedBytesForCurrentThread();
            GateResponseHeaders.Reapply(context);
            var reapply = GC.GetAllocatedBytesForCurrentThread() - before;

            // The response-start hook's successful path: the error feature is unset, so it returns a completed task.
            // The real callback runs from Kestrel at response start; this probe calls the same entry point with the
            // request's real context before anything has failed.
            before = GC.GetAllocatedBytesForCurrentThread();
            _ = ProxyHostingExtensions.OnResponseStarting(context);
            var responseStart = GC.GetAllocatedBytesForCurrentThread() - before;

            Interlocked.Increment(ref _requests);
            Interlocked.Add(ref _registrationBytes, registration);
            Interlocked.Add(ref _recordingBytes, recording);
            Interlocked.Add(ref _reapplyBytes, reapply);
            Interlocked.Add(ref _responseStartBytes, responseStart);
            return next(context);
        }
    }

    private static async Task SendAsync(ProxyTestServer server, HttpClient client, string host, string path, HttpStatusCode expected, int count)
    {
        var ct = TestContext.Current.CancellationToken;
        var next = 0;
        var workers = new Task[Connections];
        for (var w = 0; w < workers.Length; w++)
        {
            workers[w] = Task.Run(
                async () =>
                {
                    while (Interlocked.Increment(ref next) <= count)
                    {
                        using var request = server.Request(host, path);
                        using var response = await client.SendAsync(request, ct);
                        Assert.Equal(expected, response.StatusCode);
                        await response.Content.LoadIntoBufferAsync(ct);
                    }
                },
                ct);
        }

        await Task.WhenAll(workers);
    }
}
