using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Logging;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Tests.Proxy.Testing;
using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.Logging;

public sealed class AccessLogMiddlewareTests : IDisposable
{
    private readonly SnapshotStore _store = new();
    private readonly GateMiddleware _gate;

    public AccessLogMiddlewareTests()
    {
        ProxyConfigModel model = new()
        {
            Upstreams = [Upstream("web", "http://127.0.0.1:1/")],
            Hosts =
            [
                ProxyHost(
                    "app",
                    "app.test",
                    Route("app-drop", "web", "/drop") with { Policies = [new DropPolicySpec()] },
                    Route("app-root", "web")),
                ProxyHost("quiet", "quiet.test", Route("quiet-root", "web")) with { AccessLog = false },
                ProxyHost("idonly", "idonly.test", Route("idonly-root", "web")) with { RequestIdPropagation = RequestIdPropagation.UpstreamOnly },
            ],
            Defaults = new DefaultsSpec { AccessLog = false },
        };
        _store.Publish(Bound(model));
        _gate = new GateMiddleware(_store, new TrustedProxyClientAddressSource());
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public void AnEntryCarriesWhatTheGateResolvedAndTheResponseSays()
    {
        AccessLogRing ring = new(8);
        var context = Request("app.test", "/a/b?x=1", "Mozilla/5.0");
        _gate.Decide(context);
        context.Response.StatusCode = (int)HttpStatusCode.Created;
        context.Response.ContentLength = 512;

        AccessLogMiddleware.Record(context, ring, Stopwatch.GetTimestamp() - Stopwatch.Frequency / 1000);

        Assert.True(ring.TryRead(out var entry));
        Assert.Equal("127.0.0.1", entry.ClientAddress);
        Assert.Equal("app.test", entry.Host);
        Assert.Equal("GET", entry.Method);
        Assert.Equal("/a/b", entry.Path);
        Assert.Equal("?x=1", entry.Query);
        Assert.Equal("HTTP/1.1", entry.Protocol);
        Assert.Equal(201, entry.StatusCode);
        Assert.Equal(512, entry.ResponseBytes);
        Assert.Equal("Mozilla/5.0", entry.UserAgent);
        Assert.True(entry.TotalTicks >= TimeSpan.FromMilliseconds(1).Ticks, $"total was {entry.TotalTicks} ticks");
        Assert.Equal(AccessLogEntry.Unknown, entry.UpstreamTicks);
        Assert.Null(entry.UpstreamAddress);
        Assert.False(string.IsNullOrEmpty(entry.RequestId));
        Assert.Null(entry.CloudflareRay);
        Assert.InRange(DateTime.UtcNow.Ticks - entry.TimestampUtcTicks, 0, TimeSpan.FromMinutes(1).Ticks);
    }

    [Fact]
    public void AHostWithoutTheAccessLogWritesNothing()
    {
        AccessLogRing ring = new(8);
        var context = Request("quiet.test", "/");
        _gate.Decide(context);

        AccessLogMiddleware.Record(context, ring, Stopwatch.GetTimestamp());

        Assert.False(ring.TryRead(out _));
    }

    [Fact]
    public void AnUnknownHostFollowsTheGlobalDefault()
    {
        AccessLogRing ring = new(8);
        var context = Request("nobody.test", "/");
        _gate.Decide(context);

        // The global default in this snapshot is off.
        AccessLogMiddleware.Record(context, ring, Stopwatch.GetTimestamp());
        Assert.False(ring.TryRead(out _));

        _store.Publish(Bound(new ProxyConfigModel { Defaults = new DefaultsSpec() }, generation: 2));
        var allowed = Request("nobody.test", "/");
        _gate.Decide(allowed);
        AccessLogMiddleware.Record(allowed, ring, Stopwatch.GetTimestamp());
        Assert.True(ring.TryRead(out var entry));
        // The host as received is logged, but no compiled host was matched: the global default decided.
        Assert.Equal("nobody.test", entry.Host);
        Assert.Null(allowed.Features.Get<IGateFeature>()!.State.Host);
    }

    [Fact]
    public void CfRayIsRecordedOnlyFromATrustedCloudflarePeer()
    {
        AccessLogRing ring = new(8);
        var spoofed = Request("app.test", "/", configure: request => request.Headers["CF-Ray"] = "8f00000000000000-AMS");
        _gate.Decide(spoofed);
        AccessLogMiddleware.Record(spoofed, ring, Stopwatch.GetTimestamp());
        Assert.True(ring.TryRead(out var untrusted));
        Assert.Null(untrusted.CloudflareRay);

        _store.Publish(Bound(
            new ProxyConfigModel
            {
                Upstreams = [Upstream("web", "http://127.0.0.1:1/")],
                Hosts = [ProxyHost("app", "app.test", Route("app-root", "web"))],
                Defaults = new DefaultsSpec { TrustedProxies = new TrustedProxySpec { Mode = TrustedProxyMode.CloudflareProxy } },
            },
            generation: 3,
            options: new SnapshotCompilerOptions { CloudflareEdgeRanges = CidrSet.Parse(["127.0.0.0/8"]) }));
        var trusted = Request("app.test", "/", configure: request => request.Headers["CF-Ray"] = "8f00000000000000-AMS");
        _gate.Decide(trusted);
        AccessLogMiddleware.Record(trusted, ring, Stopwatch.GetTimestamp());
        Assert.True(ring.TryRead(out var edge));
        Assert.Equal("8f00000000000000-AMS", edge.CloudflareRay);
    }

    [Fact]
    public void NothingSecretReachesAnEntry()
    {
        AccessLogRing ring = new(8);
        var context = Request(
            "app.test",
            "/login?token=s3cret&password=hunter2",
            "Mozilla/5.0",
            configure: request =>
            {
                request.Headers.Authorization = "Bearer eyJhbGciOiJIUzI1NiJ9.secret.sig";
                request.Headers.Cookie = "__Host-paduraksa-session=live; theme=dark";
            });
        _gate.Decide(context);

        AccessLogMiddleware.Record(context, ring, Stopwatch.GetTimestamp());

        Assert.True(ring.TryRead(out var entry));
        // An entry holds no cookie, no Authorization and no whole-query copy; redaction happens in the formatter.
        Assert.Equal("?token=s3cret&password=hunter2", entry.Query);
        Assert.DoesNotContain("live", entry.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer", entry.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Recording is 0 B: it reads strings the request holds, the cached client address and the request ID the gate has
    /// already materialised. The one string a request accepts is created on the first
    /// <see cref="GateRequestState.RequestIdText"/> call, not here.
    /// </summary>
    [Fact]
    public void RecordingAllocatesNothing()
    {
        AccessLogRing ring = new(8);
        var context = Request("app.test", "/a/b?x=1", "Mozilla/5.0");
        _gate.Decide(context);
        context.Response.ContentLength = 1_024;
        var started = Stopwatch.GetTimestamp();
        _ = context.Features.Get<IGateFeature>()!.State.RequestIdText();
        _ = context.Features.Get<IGateFeature>()!.State.FormattedClientAddress();

        AllocAssert.Zero(
            (ring, context, started),
            static s =>
            {
                AccessLogMiddleware.Record(s.context, s.ring, s.started);
                while (s.ring.TryRead(out _))
                {
                }
            },
            "recording an access-log entry");
    }

    [Fact]
    public void AHostWithTheLogOffAllocatesNothingEither()
    {
        AccessLogRing ring = new(8);
        var context = Request("quiet.test", "/");
        _gate.Decide(context);
        AllocAssert.Zero(
            (ring, context),
            static s => AccessLogMiddleware.Record(s.context, s.ring, Stopwatch.GetTimestamp()),
            "a host without the access log");
    }

    /// <summary>
    /// An exception that no one handled, before anything went out, is the client's 500: the middleware sets it so the line
    /// says 500 rather than the 200 a response object that never sent anything still reports.
    /// </summary>
    [Fact]
    public async Task AnUnhandledExceptionIsLoggedAs500()
    {
        AccessLogRing ring = new(8);
        AccessLogMiddleware middleware = new(ring);
        var context = Request("app.test", "/boom");
        _gate.Decide(context);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => middleware.InvokeAsync(context, static _ => throw new InvalidOperationException("boom")));

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.True(ring.TryRead(out var entry));
        Assert.Equal(500, entry.StatusCode);
    }

    /// <summary>A request the gate dropped sent nothing, so the line has no status at all (written as <c>-</c>).</summary>
    [Fact]
    public async Task AnAbortedRequestIsLoggedWithoutAStatus()
    {
        AccessLogRing ring = new(8);
        AccessLogMiddleware middleware = new(ring);
        var context = Request("app.test", "/drop");
        AbortProbe probe = new();
        context.Features.Set<IHttpRequestLifetimeFeature>(probe);

        // The pipeline as the public app has it: the access log wraps the gate, which carries out its Drop.
        await middleware.InvokeAsync(context, next => _gate.InvokeAsync(next, static _ => Task.CompletedTask));

        Assert.True(probe.Aborted);
        Assert.True(ring.TryRead(out var entry));
        Assert.Equal(AccessLogEntry.NoResponse, entry.StatusCode);
    }

    /// <summary>Answers the gate's own Drop policy, in the model this class publishes.</summary>
    private sealed class AbortProbe : IHttpRequestLifetimeFeature
    {
        public bool Aborted { get; private set; }

        public CancellationToken RequestAborted { get; set; }

        public void Abort() => Aborted = true;
    }

    private static DefaultHttpContext Request(string host, string path, string? userAgent = null, Action<HttpRequest>? configure = null)
    {
        var question = path.IndexOf('?', StringComparison.Ordinal);
        var context = TestContexts.Create(host, question < 0 ? path : path[..question], rawTarget: path);
        if (question >= 0)
        {
            // Kestrel serves the raw query from the target; the test context needs the same shape.
            context.Request.QueryString = new QueryString(path[question..]);
        }

        if (userAgent is not null)
        {
            context.Request.Headers.UserAgent = userAgent;
        }

        configure?.Invoke(context.Request);
        return context;
    }
}
