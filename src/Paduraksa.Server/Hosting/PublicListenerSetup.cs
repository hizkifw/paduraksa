using System.Security.Cryptography.X509Certificates;
using Paduraksa.Proxy.AccessLists;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.ForwardAuth;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Metrics;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.RateLimiting;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Tls;
using Paduraksa.Server.Configuration;

namespace Paduraksa.Server.Hosting;

/// <summary>
/// Everything the public listeners and the snapshot compiler must agree on, derived once from
/// <see cref="PaduraksaListenOptions"/>: whether HTTP/3 is really served, the public HTTPS port, the
/// PROXY protocol settings per listener, and the one <see cref="TlsHandshakeSelector"/> of the process. A shared
/// singleton: its counters (TLS rejections, PROXY protocol outcomes) are process-lifetime state.
/// </summary>
internal sealed class PublicListenerSetup
{
    public PublicListenerSetup(
        PaduraksaListenOptions listen,
        SnapshotStore snapshots,
        bool adminInternal,
        ProofOfWorkRuntime? proofOfWork = null,
        RateLimitRuntime? rateLimits = null,
        CloudflareEdgeRangeSource? edgeRanges = null,
        X509Certificate2Collection? originPullCa = null,
        AccessListRuntime? accessLists = null,
        ForwardAuthRuntime? forwardAuth = null,
        MetricsRegistry? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(listen);
        ArgumentNullException.ThrowIfNull(snapshots);

        Http3Requested = listen.Http3;
        Http3 = listen.Http3 && PublicListeners.IsHttp3Supported;
        HttpProxyProtocol = Create(listen.HttpProxyProtocol);
        HttpsProxyProtocol = Create(listen.HttpsProxyProtocol);
        Selector = new TlsHandshakeSelector(snapshots, OriginLockdown.CreateQuicGate(snapshots, OriginLockdownCounters));
        CompilerOptions = new SnapshotCompilerOptions
        {
            PublicHttpsPort = listen.PublicHttpsPort,
            AdvertiseHttp3 = Http3,
            AdminUpstream = adminInternal,
            ProofOfWork = proofOfWork,
            RateLimits = rateLimits,
            CloudflareRanges = edgeRanges,
            OriginPullCa = originPullCa,
            AccessLists = accessLists,
            ForwardAuth = forwardAuth,
            Metrics = metrics,
        };
    }

    /// <summary>HTTP/3 is configured on (<c>Paduraksa:Listen:Http3</c>).</summary>
    public bool Http3Requested { get; }

    /// <summary>HTTP/3 is configured on <b>and</b> libmsquic is available: the UDP listener exists and hosts get
    /// <c>Alt-Svc</c>.</summary>
    public bool Http3 { get; }

    /// <summary>PROXY protocol on the plain-HTTP listener, or null.</summary>
    public ProxyProtocolOptions? HttpProxyProtocol { get; }

    /// <summary>PROXY protocol on the HTTPS TCP listener (before TLS), or null.</summary>
    public ProxyProtocolOptions? HttpsProxyProtocol { get; }

    /// <summary>Connections dropped before TLS because every host is locked down to Cloudflare, per transport.</summary>
    public OriginLockdownCounters OriginLockdownCounters { get; } = new();

    /// <summary>Per-SNI certificate selection on TCP and QUIC, reading the live snapshot's certificate table.</summary>
    public TlsHandshakeSelector Selector { get; }

    /// <summary>What every snapshot is compiled with (certificates, public port, H3 advertising, the proof-of-work runtime).</summary>
    public SnapshotCompilerOptions CompilerOptions { get; }

    private static ProxyProtocolOptions? Create(ProxyProtocolListenOptions options) =>
        options.Enabled
            ? new ProxyProtocolOptions(CidrSet.Parse(options.TrustedSources)) { HeaderTimeout = options.HeaderTimeout }
            : null;
}
