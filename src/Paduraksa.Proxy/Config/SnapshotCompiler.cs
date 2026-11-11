using System.Net;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Http;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.RateLimiting;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Tls;
using Paduraksa.Proxy.Yarp;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

namespace Paduraksa.Proxy.Config;

/// <summary>Outcome of <see cref="SnapshotCompiler.Compile"/>: an unbound candidate, or every error found, each with the
/// object it is about.</summary>
public sealed record CompileResult(RuntimeSnapshot? Snapshot, IReadOnlyList<CompileError> Errors)
{
    public bool Succeeded => Snapshot is not null;
}

/// <summary>
/// Validates a <see cref="ProxyConfigModel"/> and compiles it into an unbound <see cref="RuntimeSnapshot"/> candidate
/// plus its YARP config. Cold path: it may allocate freely. It reports every error it finds
/// rather than stopping at the first, each attributed to the object it is about (<see cref="CompileErrorSource"/>:
/// a host, route, upstream, destination or the defaults, by typed ID). YARP's own validator runs later, in
/// <c>SnapshotPublisher</c>.
/// <para>YARP config is a pure function of content: its route, cluster and destination IDs are the canonical strings
/// of the <see cref="RouteId"/>, <see cref="UpstreamId"/> and <see cref="DestinationId"/> (formatted here, never on the
/// request path), each route path-only (<c>/.paduraksa/r/{routeId}/{**rest}</c>), never requested because the gate
/// picks the endpoint itself. IDs must be non-empty and unique; because YARP compares its IDs ignoring case, route,
/// upstream and destination IDs that differ only in case are rejected too (distinct base62 values, one YARP ID).</para>
/// <para>TLS: every enabled host's DNS names (exact and wildcard) go into the snapshot's <see cref="CertificateTable"/>
/// with the host's certificate (or the default one) and its TLS settings; the default certificate serves unknown or
/// absent SNI. A host without HTTPS (<see cref="HostSpec.Https"/>) takes no entries. Certificate IDs are resolved
/// through <see cref="SnapshotCompilerOptions.Certificates"/>.</para>
/// </summary>
public static partial class SnapshotCompiler
{
    /// <summary>Prefix of the internal YARP route paths; nothing ever matches on them.</summary>
    public const string InternalRoutePrefix = "/.paduraksa/r/";

    public const string AffinityCookieName = ".Paduraksa.Affinity";

    /// <summary>The admin cluster's one destination key; the connect callback ignores its address.</summary>
    private const string AdminClusterDestinationId = "admin";

    /// <summary>Compiles <paramref name="model"/> with <paramref name="options"/> (default: <see cref="SnapshotCompilerOptions.Default"/>).</summary>
    public static CompileResult Compile(ProxyConfigModel model, SnapshotCompilerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        CompileContext context = new(model, options ?? SnapshotCompilerOptions.Default);
        context.CompileDefaultsAndUpstreams();
        foreach (var spec in model.Hosts ?? [])
        {
            context.CompileHost(spec);
        }

        return context.Errors.Count > 0
            ? new CompileResult(null, context.Errors)
            : new CompileResult(context.Build(), []);
    }

    /// <summary>
    /// The admin cluster: one symbolic destination whose address the internal listener's connect callback
    /// ignores, and HTTP/1.1 fixed because that listener speaks nothing else (a WebSocket upgrade is HTTP/1.1 too).
    /// </summary>
    private static ClusterConfig CompileAdminCluster() => new()
    {
        ClusterId = YarpConfig.AdminClusterId,
        Destinations = new Dictionary<string, DestinationConfig>(StringComparer.OrdinalIgnoreCase)
        {
            [AdminClusterDestinationId] = new DestinationConfig { Address = YarpConfig.AdminUpstreamAddress },
        },
        HttpRequest = new ForwarderRequestConfig
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        },
    };

    /// <summary>Two distinct typed IDs whose canonical strings YARP would take for one ID.</summary>
    private static string CaseCollision(string kind, string id, string existing) =>
        $"{kind} ID '{id}' differs from '{existing}' only in case (YARP ignores case)";

    private static void RemoveSlotsFrom(CompiledRoute[] routes, List<RouteConfig> yarpRoutes,
        List<RouteId> routeIdBySlot)
    {
        if (routes.Length == 0)
        {
            return;
        }

        // A host's routes take the last slots, so dropping them is a tail truncation.
        var first = routeIdBySlot.Count - routes.Length;
        routeIdBySlot.RemoveRange(first, routes.Length);
        yarpRoutes.RemoveRange(first, routes.Length);
    }

    /// <summary>
    /// One compile's accumulators and the steps that fill them. Internal so a test can drive the per-host step on its
    /// own (the admin-host rules, say) instead of building a whole document around it.
    /// </summary>
    internal sealed partial class CompileContext
    {
        public CompileContext(ProxyConfigModel model, SnapshotCompilerOptions options)
        {
            Model = model;
            Options = options;
            Defaults = model.Defaults ?? new DefaultsSpec();
            AltSvc = new AltSvcHeader(options.PublicHttpsPort, options.AdvertiseHttp3);
            RateLimitPolicies = CompileRateLimitPolicies(model.RateLimitPolicies ?? []);
        }

        public ProxyConfigModel Model { get; }

        public SnapshotCompilerOptions Options { get; }

        public DefaultsSpec Defaults { get; }

        public List<CompileError> Errors { get; } = [];

        public CertificateTableBuilder Certificates { get; } = new();

        public ServerCertificate? DefaultCertificate { get; private set; }

        public TrustedProxyPolicy TrustedProxies { get; private set; } = TrustedProxyPolicy.Off;

        public EdgeRangeList? EdgeRanges { get; private set; }

        public ResponseHeaderOperation[] DefaultHeaders { get; private set; } = [];

        public Dictionary<UpstreamId, ClusterConfig> Clusters { get; private set; } = [];

        public HashSet<UpstreamId> WithoutDestinations { get; } = [];

        public HashSet<UpstreamId> Reported { get; } = [];

        public HostTable.Builder Table { get; } = new();

        public HashSet<HostId> HostIds { get; } = [];

        public HashSet<RouteId> RouteIds { get; } = [];

        public Dictionary<string, RouteId> YarpRouteIds { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<CompiledHost> Hosts { get; } = [];

        public List<RouteConfig> YarpRoutes { get; } = [];

        public List<RouteId> RouteIdBySlot { get; } = [];

        public List<RateLimitRegistration> RateLimitRegistrations { get; } = [];

        public RateLimitRuntime RateLimits => Options.RateLimits ?? RateLimitRuntime.Default;

        /// <summary>The named policies by ID (first one wins on a duplicate, which is invalid anyway).</summary>
        public Dictionary<RateLimitPolicyId, RateLimitPolicySpec> RateLimitPolicies { get; }

        public bool AdminHost { get; set; }

        public HttpsRedirect? HttpsRedirect { get; set; }

        public AltSvcHeader AltSvc { get; }

        /// <summary>The client-certificate requirement of every Authenticated Origin Pulls host: one instance per compile, so
        /// those hosts share their TLS options.</summary>
        public ClientCertificatePolicy OriginPullPolicy => field ??= new ClientCertificatePolicy
        {
            Required = true,
            TrustedRoots = Options.OriginPullCa ?? OriginPullCa.Default,
        };

        /// <summary>The defaults and upstreams every host then compiles against, in the order the errors have always come in.</summary>
        public void CompileDefaultsAndUpstreams()
        {
            if (!Enum.IsDefined(Defaults.UnknownHost))
            {
                Errors.Add(new(CompileErrorSource.Defaults, $"unknown default-host behaviour {Defaults.UnknownHost}"));
            }

            DefaultCertificate = ResolveCertificate(Defaults.Certificate, CompileErrorSource.Defaults,
                Options.Certificates, Errors);
            if (DefaultCertificate is not null)
            {
                Certificates.SetDefault(DefaultCertificate,
                    CompileTlsPolicy(Defaults.Tls, CompileErrorSource.Defaults, Errors));
            }

            TrustedProxies = CompileTrustedProxies(Defaults.TrustedProxies, Options, Errors, out var edgeRanges);
            EdgeRanges = edgeRanges;
            DefaultHeaders = CompileResponseHeaders(
                serverHeader: Defaults.ServerHeader,
                hsts: null,
                altSvc: default,
                altSvcReason: "a name no host serves is never invited onto HTTP/3",
                hstsReason: "the proxy doesn't pin a name it doesn't serve",
                propagation: Defaults.RequestIdPropagation,
                CompileErrorSource.Defaults,
                Errors);
            Clusters = CompileUpstreams(Model.Upstreams ?? [], WithoutDestinations, Errors);
        }

        /// <summary>The candidate, once every step has run without an error.</summary>
        public RuntimeSnapshot Build() => new()
        {
            Generation = 0,
            Revision = Model.Revision,
            Hosts = Table.Build(),
            HostsByIndex = Hosts.ToArray(),
            Yarp = new YarpConfig(YarpRoutes.ToArray(), BuildClusters()),
            RouteIdBySlot = RouteIdBySlot.ToArray(),
            RateLimitRegistrations = RateLimitRegistrations.ToArray(),
            YarpEndpoints = [],
            Default = Defaults.UnknownHost switch
            {
                UnknownHostBehaviour.Close => DefaultHostBehaviour.Close,
                UnknownHostBehaviour.MisdirectedRequest => DefaultHostBehaviour.MisdirectedRequest(),
                _ => DefaultHostBehaviour.NotFound(),
            },
            Certificates = Certificates.Build(),
            TrustedProxies = TrustedProxies,
            EdgeRanges = EdgeRanges,
            LockdownAtConnection = LocksDownEveryHost(),
            AccessLog = Defaults.AccessLog,
            RequestIdPropagation = Defaults.RequestIdPropagation,
            DefaultResponseHeaders = DefaultHeaders,
        };

        /// <summary>
        /// An untrusted peer can be dropped before TLS only when no host would have answered it: every host in the snapshot
        /// is locked down. Then even the unknown-host answer is nothing a visitor is meant to see.
        /// </summary>
        private bool LocksDownEveryHost()
        {
            if (Hosts.Count == 0)
            {
                return false;
            }

            foreach (var host in Hosts)
            {
                if (host.OriginLockdown == OriginLockdownMode.Off)
                {
                    return false;
                }
            }

            return true;
        }

        private ClusterConfig[] BuildClusters()
        {
            var clusters = new ClusterConfig[Clusters.Count + (AdminHost && Options.AdminUpstream ? 1 : 0)];
            Clusters.Values.CopyTo(clusters, 0);
            if (AdminHost && Options.AdminUpstream)
            {
                clusters[^1] = CompileAdminCluster();
            }

            return clusters;
        }
    }
}
