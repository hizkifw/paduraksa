using System.Diagnostics.CodeAnalysis;

using BenchmarkDotNet.Attributes;

using Microsoft.AspNetCore.Http;

using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Snapshot;

namespace Paduraksa.Benchmarks;

/// <summary>
/// The gate's synchronous decision (<see cref="GateMiddleware.Decide"/>): snapshot read, pooled per-request state,
/// client address, host lookup, host policies, then the path being measured. One context per case is reused across
/// iterations, like Kestrel reuses its per-connection state. The response write that follows is Kestrel's work and
/// isn't measured. On <see cref="DefaultHttpContext"/> the feature lookups go through a dictionary; Kestrel's are
/// field-backed, so the real gate is a little faster.
/// </summary>
[MemoryDiagnoser]
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "BenchmarkDotNet disposes it in [GlobalCleanup].")]
public class GateBenchmarks
{
    private SnapshotStore _store = null!;
    private GateMiddleware _gate = null!;
    private DefaultHttpContext _forward = null!;
    private DefaultHttpContext _forwardNonCanonical = null!;
    private DefaultHttpContext _respondHost = null!;
    private DefaultHttpContext _redirectHost = null!;
    private DefaultHttpContext _httpsRedirect = null!;
    private DefaultHttpContext _unknownHost = null!;
    private DefaultHttpContext _forwardWithHsts = null!;
    private DefaultHttpContext _forwardWithHstsExemptName = null!;
    private SnapshotStore _cloudflareStore = null!;
    private GateMiddleware _cloudflareGate = null!;
    private DefaultHttpContext _forwardCloudflare = null!;

    [GlobalSetup]
    public void Setup()
    {
        _store = new SnapshotStore();
        _store.Publish(BenchConfig.Snapshot);
        _gate = new GateMiddleware(_store, new TrustedProxyClientAddressSource());

        _forward = BenchConfig.Context("app.test", "/assets/app.3f9a1c.js");
        _forwardNonCanonical = BenchConfig.Context("app.test", "/api\\v1//items/./42");
        _respondHost = BenchConfig.Context("respond.test", "/");
        _redirectHost = BenchConfig.Context("moved.test", "/old/page");
        _httpsRedirect = BenchConfig.Context("secure.test", "/login");
        _unknownHost = BenchConfig.Context("nope.example.org", "/");
        _forwardWithHsts = BenchConfig.Context("hsts.test", "/assets/app.3f9a1c.js", scheme: "https");
        _forwardWithHstsExemptName = BenchConfig.Context("localhost", "/assets/app.3f9a1c.js", scheme: "https");

        Expect(_forward, GateAction.Forward);
        Expect(_forwardNonCanonical, GateAction.Forward);
        Expect(_respondHost, GateAction.Respond, 200);
        Expect(_redirectHost, GateAction.Respond, 301);
        Expect(_httpsRedirect, GateAction.Respond, 301);
        Expect(_unknownHost, GateAction.Respond, 404);
        Expect(_forwardWithHsts, GateAction.Forward);
        Expect(_forwardWithHstsExemptName, GateAction.Forward);

        _cloudflareStore = new SnapshotStore();
        _cloudflareStore.Publish(BenchConfig.CloudflareSnapshot);
        _cloudflareGate = new GateMiddleware(_cloudflareStore, new TrustedProxyClientAddressSource());
        _forwardCloudflare = BenchConfig.Context("app.test", "/assets/app.3f9a1c.js");
        _forwardCloudflare.Request.Headers[CloudflareHeaders.ConnectingIp] = "2001:db8:85a3::8a2e:370:7334";
        _forwardCloudflare.Request.Headers[CloudflareHeaders.Visitor] = "{\"scheme\":\"https\"}";
        var cloudflare = _cloudflareGate.Decide(_forwardCloudflare);
        if (cloudflare.Action != GateAction.Forward
            || _forwardCloudflare.Features.Get<IGateFeature>()!.State.ClientAddress != IpKey.Parse("2001:db8:85a3::8a2e:370:7334")
            || !_forwardCloudflare.Features.Get<IGateFeature>()!.State.IsHttps)
        {
            throw new InvalidOperationException("The Cloudflare case didn't forward with the CF-Connecting-IP address and the CF-Visitor scheme.");
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _store.Dispose();
        _cloudflareStore.Dispose();
    }

    /// <summary>Proxy host, canonical path, 8th of 9 routes: ends in <c>SetEndpoint</c> for YARP.</summary>
    [Benchmark(Baseline = true)]
    public int Forward() => (int)_gate.Decide(_forward).Action;

    /// <summary>As <see cref="Forward"/>, behind a trusted Cloudflare Tunnel: the client address comes from an IPv6
    /// <c>CF-Connecting-IP</c> and the scheme from <c>CF-Visitor</c>.</summary>
    [Benchmark]
    public int ForwardCloudflare() => (int)_cloudflareGate.Decide(_forwardCloudflare).Action;

    /// <summary>As <see cref="Forward"/>, but the path is rewritten on the stack before matching.</summary>
    [Benchmark]
    public int ForwardNonCanonicalPath() => (int)_gate.Decide(_forwardNonCanonical).Action;

    /// <summary>A Respond host: the precomputed 200 is chosen.</summary>
    [Benchmark]
    public int AnsweredRespondHost() => (int)_gate.Decide(_respondHost).Action;

    /// <summary>A Redirect host: the precomputed 301 is chosen (its <c>Location</c> is built when written).</summary>
    [Benchmark]
    public int AnsweredRedirectHost() => (int)_gate.Decide(_redirectHost).Action;

    /// <summary>A ForceHttps host over plain HTTP: the precomputed HTTP→HTTPS 301.</summary>
    [Benchmark]
    public int HttpsRedirect() => (int)_gate.Decide(_httpsRedirect).Action;

    /// <summary>A proxy host with HSTS on, over HTTPS: the precomputed header is set (PLAN §14 M6).</summary>
    [Benchmark]
    public int ForwardWithHsts() => (int)_gate.Decide(_forwardWithHsts).Action;

    /// <summary>As <see cref="ForwardWithHsts"/>, but the name is a loopback one, so the header is refused.</summary>
    [Benchmark]
    public int ForwardWithHstsExemptName() => (int)_gate.Decide(_forwardWithHstsExemptName).Action;

    /// <summary>Unknown host: the default 404.</summary>
    [Benchmark]
    public int UnknownHost() => (int)_gate.Decide(_unknownHost).Action;

    private void Expect(DefaultHttpContext context, GateAction action, int status = 0)
    {
        var decision = _gate.Decide(context);
        if (decision.Action != action || (status != 0 && decision.Response?.StatusCode != status))
        {
            throw new InvalidOperationException(
                $"{context.Request.Host}{context.Request.Path}: expected {action} {status}, got {decision.Action} {decision.Response?.StatusCode}.");
        }
    }
}
