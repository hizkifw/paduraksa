using System.Net;

using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Snapshot;

namespace Paduraksa.Benchmarks;

/// <summary>
/// The shared benchmark config: ~500 hosts like spike S3's table (480 exact, 20 single-label wildcards), plus a few
/// hosts exercising each gate path. Compiled by the real <see cref="SnapshotCompiler"/> and bound to stand-in
/// endpoints, so no YARP is involved. IDs are new per run: nothing on the request path reads them.
/// </summary>
internal static class BenchConfig
{
    public const int ExactHosts = 480;
    public const int WildcardHosts = 20;

    public static RuntimeSnapshot Snapshot { get; } = Bind(Compile(Model()));

    /// <summary>The same hosts behind a Cloudflare Tunnel whose cloudflared connects from 203.0.113.0/24 (the
    /// contexts' peer), so <c>CF-Connecting-IP</c> is honoured.</summary>
    public static RuntimeSnapshot CloudflareSnapshot { get; } = Bind(Compile(Model() with
    {
        Defaults = new DefaultsSpec
        {
            TrustedProxies = new TrustedProxySpec { Mode = TrustedProxyMode.CloudflareTunnel, Proxies = ["203.0.113.0/24"] },
        },
    }));

    /// <summary>The host with a realistic route list (8 prefixes and one exact route; the catch-all last).</summary>
    public static CompiledHost App => Snapshot.Hosts.Find("app.test") ?? throw new InvalidOperationException("app.test missing");

    public static ProxyConfigModel Model()
    {
        UpstreamId upstream = UpstreamId.New();
        List<HostSpec> hosts = [];
        for (int i = 0; i < ExactHosts; i++)
        {
            hosts.Add(Proxy($"h{i}.test"));
        }

        for (int i = 0; i < WildcardHosts; i++)
        {
            hosts.Add(Proxy($"*.w{i}.test"));
        }

        hosts.Add(new HostSpec
        {
            Id = HostId.New(),
            Names = ["app.test"],
            Routes =
            [
                Route("/api/v1"),
                Route("/api/v2"),
                Route("/auth"),
                Route("/ws"),
                Route("/healthz") with { Match = RouteMatchKind.Exact },
                Route("/static"),
                Route("/img"),
                Route("/assets"),
                Route("/"),
            ],
        });
        hosts.Add(Proxy("secure.test") with { ForceHttps = true });
        hosts.Add(new HostSpec { Id = HostId.New(), Names = ["respond.test"], Kind = HostKind.Respond, Respond = new RespondSpec { Body = "ok\n" } });
        hosts.Add(new HostSpec { Id = HostId.New(), Names = ["moved.test"], Kind = HostKind.Redirect, Redirect = new RedirectSpec { Target = "https://new.test" } });

        return new ProxyConfigModel
        {
            Revision = ConfigRevisionId.New(),
            Upstreams = [new UpstreamSpec { Id = upstream, Destinations = [new DestinationSpec(DestinationId.New(), "http://127.0.0.1:1/")] }],
            Hosts = hosts,
        };

        HostSpec Proxy(string name) => new() { Id = HostId.New(), Names = [name], Routes = [Route("/")] };

        RouteSpec Route(string path) => new() { Id = RouteId.New(), Path = path, Upstream = upstream };
    }

    /// <summary>A <see cref="DefaultHttpContext"/> set up like Kestrel's for the gate (persistent state, a peer address).</summary>
    public static DefaultHttpContext Context(string host, string path, string scheme = "http")
    {
        FeatureCollection features = new();
        HttpRequestFeature request = new() { Method = "GET", Scheme = scheme, Path = path, RawTarget = path, Protocol = "HTTP/1.1" };
        request.Headers.Host = host;
        features.Set<IHttpRequestFeature>(request);
        features.Set<IHttpResponseFeature>(new HttpResponseFeature());
        features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(Stream.Null));
        features.Set<IPersistentStateFeature>(new PersistentState());
        features.Set<IHttpConnectionFeature>(new HttpConnectionFeature { RemoteIpAddress = IPAddress.Parse("203.0.113.7") });
        return new DefaultHttpContext(features);
    }

    private static RuntimeSnapshot Compile(ProxyConfigModel model)
    {
        CompileResult result = SnapshotCompiler.Compile(model);
        return result.Snapshot ?? throw new InvalidOperationException(string.Join(Environment.NewLine, result.Errors));
    }

    private static RuntimeSnapshot Bind(RuntimeSnapshot candidate)
    {
        Endpoint[] endpoints = new Endpoint[candidate.RouteIdBySlot.Length];
        for (int i = 0; i < endpoints.Length; i++)
        {
            endpoints[i] = new Endpoint(static _ => Task.CompletedTask, EndpointMetadataCollection.Empty, candidate.RouteIdBySlot[i].ToString());
        }

        return candidate.Bind(1, endpoints);
    }

    /// <summary>Kestrel keeps this per H1 connection or pooled H2 stream.</summary>
    private sealed class PersistentState : IPersistentStateFeature
    {
        public IDictionary<object, object?> State { get; } = new Dictionary<object, object?>();
    }
}
