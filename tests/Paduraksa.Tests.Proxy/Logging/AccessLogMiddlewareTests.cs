using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Http;
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
        context.Features.Get<IGateFeature>()!.State.CountBody(context);
        context.Response.StatusCode = (int)HttpStatusCode.Created;
        // The declared length is not what is counted: the body written is.
        context.Response.ContentLength = 512;
        context.Response.Body.Write(new byte[300]);
        _ = context.Request.Body.Read(new byte[16]);

        AccessLogMiddleware.Record(context, ring, Stopwatch.GetTimestamp() - Stopwatch.Frequency / 1000);

        Assert.True(ring.TryRead(out var entry));
        Assert.Equal("127.0.0.1", entry.ClientAddress);
        Assert.Equal("app.test", entry.Host);
        Assert.Equal("GET", entry.Method);
        Assert.Equal("/a/b", entry.Path);
        Assert.Equal("?x=1", entry.Query);
        Assert.Equal("HTTP/1.1", entry.Protocol);
        Assert.Equal(201, entry.StatusCode);
        Assert.Equal(300, entry.ResponseBytes);
        Assert.Equal(0, entry.RequestBytes);
        Assert.Equal("Mozilla/5.0", entry.UserAgent);
        Assert.True(entry.TotalTicks >= TimeSpan.FromMilliseconds(1).Ticks, $"total was {entry.TotalTicks} ticks");
        Assert.Equal(AccessLogEntry.Unknown, entry.UpstreamTicks);
        Assert.Null(entry.UpstreamAddress);
        Assert.False(string.IsNullOrEmpty(entry.RequestId));
        Assert.Null(entry.CloudflareRay);
        Assert.InRange(DateTime.UtcNow.Ticks - entry.TimestampUtcTicks, 0, TimeSpan.FromMinutes(1).Ticks);
    }

    [Fact]
    public void BytesThatCouldNotBeCountedAreUnknownNotZero()
    {
        AccessLogRing ring = new(8);
        var context = Request("app.test", "/a", "Mozilla/5.0");
        _gate.Decide(context);
        context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>(null);
        context.Features.Get<IGateFeature>()!.State.CountBody(context);

        AccessLogMiddleware.Record(context, ring, Stopwatch.GetTimestamp());

        Assert.True(ring.TryRead(out var entry));
        Assert.Equal(AccessLogEntry.Unknown, entry.ResponseBytes);
        Assert.Equal(AccessLogEntry.Unknown, entry.RequestBytes);
        var line = new char[8192];
        var length = AccessLogFormatter.Format(entry, AccessLogFormatOptions.Default, line);
        Assert.Contains(" 200 - ", new string(line, 0, length), StringComparison.Ordinal);
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
