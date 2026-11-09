using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Http;
using Paduraksa.Proxy.Logging;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Tls;
using Paduraksa.Proxy.Yarp;

namespace Paduraksa.Proxy.Snapshot;

/// <summary>
/// Everything the hot path needs, compiled once and never mutated (PLAN §6.2). A request reads
/// <see cref="SnapshotStore.Current"/> once and takes host, route, policies and the YARP <see cref="Endpoint"/> from
/// that one instance, so it can't mix two generations.
/// <para>The compiler produces an <i>unbound</i> candidate (<see cref="Generation"/> 0, no endpoints). The publisher
/// applies its YARP config and then <see cref="Bind"/>s it to YARP's new endpoints and the next generation; only a
/// bound snapshot goes into the store.</para>
/// <para><b>Never put mutable runtime state here</b> (PLAN §6.3): snapshots are thrown away on every publish.</para>
/// </summary>
public sealed class RuntimeSnapshot
{
    private static readonly PrecomputedResponse DefaultNoRoute = PrecomputedResponse.Text(StatusCodes.Status404NotFound, "not found\n");
    private static readonly PrecomputedResponse DefaultUnavailable = PrecomputedResponse.Text(StatusCodes.Status503ServiceUnavailable, "service unavailable\n");
    private static readonly PrecomputedResponse DefaultMisdirected = PrecomputedResponse.Text(StatusCodes.Status421MisdirectedRequest, "misdirected request\n");

    /// <summary>0 until bound; then strictly increasing per store.</summary>
    public required long Generation { get; init; }

    /// <summary>
    /// The config revision this was compiled from; <see cref="ConfigRevisionId.Empty"/> when none (the bootstrap
    /// snapshot, the empty startup config).
    /// </summary>
    public required ConfigRevisionId Revision { get; init; }

    /// <summary>For logs: <see cref="Revision"/>'s string form, or <c>(none)</c> when it is empty.</summary>
    public string RevisionLabel => Revision.IsEmpty ? "(none)" : Revision.ToString();

    public required HostTable Hosts { get; init; }

    /// <summary>Dense index; <see cref="CompiledHost.Index"/> points here. Valid for this snapshot only.</summary>
    public required CompiledHost[] HostsByIndex { get; init; }

    /// <summary>The YARP routes and clusters, a pure function of content.</summary>
    public required YarpConfig Yarp { get; init; }

    /// <summary>
    /// Slot → route (whose canonical string is the YARP route ID). <see cref="CompiledRoute.Slot"/> indexes this and
    /// <see cref="YarpEndpoints"/>; the compiler lists <see cref="YarpConfig.Routes"/> in the same order.
    /// </summary>
    public required RouteId[] RouteIdBySlot { get; init; }

    /// <summary>Slot → YARP endpoint, captured at publish step 6c. Empty until <see cref="Bind"/>.</summary>
    public required Endpoint[] YarpEndpoints { get; init; }

    public required DefaultHostBehaviour Default { get; init; }

    /// <summary>
    /// The <c>Server</c> the global defaults' layer resolves to, which an unknown host's answer follows too. Empty for
    /// <see cref="ServerHeaderMode.Upstream"/> and <see cref="ServerHeaderMode.Remove"/>.
    /// </summary>
    public StringValues DefaultServer { get; init; } = new(ServerHeader.Product);

    /// <summary>The global defaults' <c>Server</c> mode.</summary>
    public ServerHeaderMode DefaultServerMode { get; init; } = ServerHeaderMode.Paduraksa;

    /// <summary>A proxy host matched but none of its routes took the path.</summary>
    public PrecomputedResponse NoRoute { get; init; } = DefaultNoRoute;

    /// <summary>A route has no endpoint (only possible for an unbound snapshot).</summary>
    public PrecomputedResponse Unavailable { get; init; } = DefaultUnavailable;

    /// <summary>The request's connection is weaker than its host's TLS settings (421: retry on a connection of its own).</summary>
    public PrecomputedResponse Misdirected { get; init; } = DefaultMisdirected;

    /// <summary>
    /// SNI → prebuilt server options (PLAN §7.2 B). The handshake callbacks read it through
    /// <see cref="SnapshotStore"/> (an <see cref="ICertificateTableSource"/>), so a publish swaps certificates for new
    /// handshakes. Empty (every handshake rejected) until a config with certificates is compiled.
    /// </summary>
    public CertificateTable Certificates { get; init; } = CertificateTable.Empty;

    /// <summary>
    /// Who may tell us the client IP (PLAN §7.6): off, generic proxies, Cloudflare's edge or cloudflared. Global for M1;
    /// the gate resolves every request's client through it.
    /// </summary>
    public TrustedProxyPolicy TrustedProxies { get; init; } = TrustedProxyPolicy.Off;

    /// <summary>
    /// Whether a request whose host the snapshot doesn't know gets an access-log line (PLAN §7.4): the global default,
    /// since an unknown host has no settings of its own. A known host's <see cref="CompiledHost.AccessLog"/> decides.
    /// </summary>
    public bool AccessLog { get; init; } = true;

    /// <summary>Where an unknown host's request ID is sent (PLAN §7.4): the global default.</summary>
    public RequestIdPropagation RequestIdPropagation { get; init; } = RequestIdPropagation.UpstreamAndResponse;

    /// <summary>Whether this host writes an access-log line, else the global default: what the outermost middleware asks.</summary>
    public bool AccessLogFor(CompiledHost? host) => host?.AccessLog ?? AccessLog;

    /// <summary>Where this request's ID is sent: its host's setting, else the global default.</summary>
    public RequestIdPropagation RequestIdPropagationFor(CompiledHost? host) => host?.RequestIdPropagation ?? RequestIdPropagation;

    /// <summary>
    /// Whether every route slot has its YARP endpoint, so the gate can hand any route to YARP. A snapshot with no routes
    /// needs no endpoints, so it is trivially bound, even at generation 0 (<see cref="Bootstrap"/> included, which
    /// keeps the live snapshot bound from the start). That is also why the publisher's empty-YARP fast path can make
    /// a candidate without YARP content live at once, before YARP exists: binding it takes no endpoints.
    /// </summary>
    public bool IsBound => YarpEndpoints.Length == RouteIdBySlot.Length && (Generation > 0 || RouteIdBySlot.Length == 0);

    /// <summary>
    /// Before the first publish: generation 0, no hosts, every request answered with 503. Never seen by a client in
    /// practice, because the core host publishes before any listener opens (PLAN §3).
    /// </summary>
    public static RuntimeSnapshot Bootstrap { get; } = new()
    {
        Generation = 0,
        Revision = ConfigRevisionId.Empty,
        Hosts = HostTable.Empty,
        HostsByIndex = [],
        Yarp = YarpConfig.Empty,
        RouteIdBySlot = [],
        YarpEndpoints = [],
        Default = DefaultHostBehaviour.RespondWith(DefaultUnavailable),
    };

    /// <summary>
    /// A copy carrying <paramref name="generation"/> and YARP's endpoints (one per slot, in slot order). Everything
    /// else is shared: the compiled hosts are immutable.
    /// </summary>
    public RuntimeSnapshot Bind(long generation, Endpoint[] endpoints)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        ArgumentNullException.ThrowIfNull(endpoints);
        if (endpoints.Length != RouteIdBySlot.Length)
        {
            throw new ArgumentException($"Expected {RouteIdBySlot.Length} endpoints (one per slot), got {endpoints.Length}.", nameof(endpoints));
        }

        foreach (Endpoint endpoint in endpoints)
        {
            ArgumentNullException.ThrowIfNull(endpoint, nameof(endpoints));
        }

        return new RuntimeSnapshot
        {
            Generation = generation,
            Revision = Revision,
            Hosts = Hosts,
            HostsByIndex = HostsByIndex,
            Yarp = Yarp,
            RouteIdBySlot = RouteIdBySlot,
            YarpEndpoints = endpoints,
            Default = Default,
            DefaultServer = DefaultServer,
            DefaultServerMode = DefaultServerMode,
            NoRoute = NoRoute,
            Unavailable = Unavailable,
            Misdirected = Misdirected,
            Certificates = Certificates,
            TrustedProxies = TrustedProxies,
            AccessLog = AccessLog,
            RequestIdPropagation = RequestIdPropagation,
        };
    }
}
