using System.Buffers;
using System.Text;

using Microsoft.AspNetCore.Http;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Tls;
using Paduraksa.Proxy.Yarp;

using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Health;

namespace Paduraksa.Proxy.Config;

/// <summary>Outcome of <see cref="SnapshotCompiler.Compile"/>: an unbound candidate, or every error found, each with the
/// object it is about.</summary>
public sealed record CompileResult(RuntimeSnapshot? Snapshot, IReadOnlyList<CompileError> Errors)
{
    public bool Succeeded => Snapshot is not null;
}

/// <summary>
/// Validates a <see cref="ProxyConfigModel"/> and compiles it into an unbound <see cref="RuntimeSnapshot"/> candidate
/// plus its YARP config (PLAN §6.1 step 3–4). Cold path: it may allocate freely. It reports every error it finds
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
public static class SnapshotCompiler
{
    /// <summary>Prefix of the internal YARP route paths; nothing ever matches on them.</summary>
    public const string InternalRoutePrefix = "/.paduraksa/r/";

    /// <summary>Cookie name for session affinity.</summary>
    public const string AffinityCookieName = ".Paduraksa.Affinity";

    /// <summary>Compiles <paramref name="model"/> with <paramref name="options"/> (default: <see cref="SnapshotCompilerOptions.Default"/>).</summary>
    public static CompileResult Compile(ProxyConfigModel model, SnapshotCompilerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        options ??= SnapshotCompilerOptions.Default;
        List<CompileError> errors = [];

        DefaultsSpec defaults = model.Defaults ?? new DefaultsSpec();
        if (!Enum.IsDefined(defaults.UnknownHost))
        {
            errors.Add(new(CompileErrorSource.Defaults, $"unknown default-host behaviour {defaults.UnknownHost}"));
        }

        CertificateTableBuilder certificates = new();
        ServerCertificate? defaultCertificate = ResolveCertificate(defaults.Certificate, CompileErrorSource.Defaults, options.Certificates, errors);
        if (defaultCertificate is not null)
        {
            certificates.SetDefault(defaultCertificate, CompileTlsPolicy(defaults.Tls, CompileErrorSource.Defaults, errors));
        }

        TrustedProxyPolicy trustedProxies = CompileTrustedProxies(defaults.TrustedProxies, options, errors);
        Dictionary<UpstreamId, ClusterConfig> clusters = CompileUpstreams(model.Upstreams ?? [], errors);

        HostTable.Builder table = new();
        HashSet<HostId> hostIds = [];
        HashSet<RouteId> routeIds = [];
        Dictionary<string, RouteId> yarpRouteIds = new(StringComparer.OrdinalIgnoreCase);
        List<CompiledHost> hosts = [];
        List<RouteConfig> yarpRoutes = [];
        List<RouteId> routeIdBySlot = [];
        HttpsRedirect? httpsRedirect = null;
        AltSvcHeader altSvc = new(options.PublicHttpsPort, options.AdvertiseHttp3);

        foreach (HostSpec spec in model.Hosts ?? [])
        {
            CompileErrorSource where = CompileErrorSource.Host(spec.Id);
            if (spec.Id.IsEmpty)
            {
                errors.Add(new(where, "the host ID is empty"));
            }
            else if (!hostIds.Add(spec.Id))
            {
                errors.Add(new(where, "duplicate host ID"));
            }

            if (!Enum.IsDefined(spec.Kind))
            {
                errors.Add(new(where, $"unknown host kind {spec.Kind}"));
            }

            IRequestPolicy[] hostPolicies = CompilePolicies(spec.Policies ?? [], where, errors);
            CompiledRoute[] routes = spec.Kind == HostKind.Proxy
                ? CompileRoutes(spec, clusters, routeIds, yarpRouteIds, yarpRoutes, routeIdBySlot, where, errors)
                : RejectRoutes(spec, where, errors);
            PrecomputedResponse? fixedResponse = CompileFixedResponse(spec, where, errors);

            if (spec.ForceHttps)
            {
                httpsRedirect ??= new HttpsRedirect(options.PublicHttpsPort);
            }

            if (!spec.Https && spec.ForceHttps)
            {
                errors.Add(new(where, "a host without HTTPS can't force HTTPS"));
            }

            if (!spec.Https && spec.Certificate is not null)
            {
                errors.Add(new(where, "a host without HTTPS can't name a certificate"));
            }

            ServerCertificate? certificate = !spec.Https ? null
                : spec.Certificate is null ? defaultCertificate
                : ResolveCertificate(spec.Certificate, where, options.Certificates, errors);
            TlsPolicy tls = CompileTlsPolicy(spec.Tls, where, errors);
            List<string> serverNames = spec.Enabled && certificate is not null ? ServerNamesOf(spec.Names ?? []) : [];

            CompiledHost host = new()
            {
                Index = hosts.Count,
                Id = spec.Id,
                Kind = spec.Kind,
                Routes = routes,
                Policies = hostPolicies,
                FixedResponse = fixedResponse,
                HttpsRedirect = spec.ForceHttps ? httpsRedirect : null,
                Tls = tls,
                Https = spec.Https,
                // Only hosts with names in the certificate table can be reached over HTTP/3 (an IP-literal-only
                // host has none: its HTTPS rides on the default certificate's entry and that entry's settings).
                AltSvc = serverNames.Count > 0 ? altSvc.ForHost(tls) : default,
            };

            if (spec.Names is null || spec.Names.Count == 0)
            {
                errors.Add(new(where, "needs at least one name"));
            }

            // A disabled host's names go into a table of their own: validated like any others (duplicates within
            // the host included), but the host serves nothing, so it may share a name with another host.
            HostTable.Builder nameTable = spec.Enabled ? table : new HostTable.Builder();
            bool namesValid = true;
            foreach (string name in spec.Names ?? [])
            {
                if (!nameTable.TryAdd(name, host, out string? nameError))
                {
                    errors.Add(new(where, nameError!));
                    namesValid = false;
                }
            }

            if (namesValid && certificate is not null && serverNames.Count > 0)
            {
                AddServerNames(certificates, serverNames, certificate, tls, where, errors);
            }

            if (spec.Enabled)
            {
                hosts.Add(host);
            }
            else
            {
                // A disabled host takes no names, routes or slots; drop what it registered.
                RemoveSlotsFrom(routes, yarpRoutes, routeIdBySlot);
            }
        }

        if (errors.Count > 0)
        {
            return new CompileResult(null, errors);
        }

        ClusterConfig[] clusterArray = new ClusterConfig[clusters.Count];
        clusters.Values.CopyTo(clusterArray, 0);
        RuntimeSnapshot snapshot = new()
        {
            Generation = 0,
            Revision = model.Revision,
            Hosts = table.Build(),
            HostsByIndex = hosts.ToArray(),
            Yarp = new YarpConfig(yarpRoutes.ToArray(), clusterArray),
            RouteIdBySlot = routeIdBySlot.ToArray(),
            YarpEndpoints = [],
            Default = defaults.UnknownHost switch
            {
                UnknownHostMode.Close => DefaultHostBehaviour.Close,
                UnknownHostMode.MisdirectedRequest => DefaultHostBehaviour.MisdirectedRequest(),
                _ => DefaultHostBehaviour.NotFound(),
            },
            Certificates = certificates.Build(),
            TrustedProxies = trustedProxies,
        };
        return new CompileResult(snapshot, []);
    }

    private static Dictionary<UpstreamId, ClusterConfig> CompileUpstreams(IReadOnlyList<UpstreamSpec> upstreams, List<CompileError> errors)
    {
        // Insertion-ordered by construction.
        Dictionary<UpstreamId, ClusterConfig> clusters = [];
        Dictionary<string, UpstreamId> clusterIds = new(StringComparer.OrdinalIgnoreCase);
        foreach (UpstreamSpec spec in upstreams)
        {
            CompileErrorSource where = CompileErrorSource.Upstream(spec.Id);
            if (spec.Id.IsEmpty)
            {
                errors.Add(new(where, "the upstream ID is empty"));
                continue;
            }

            if (clusters.ContainsKey(spec.Id))
            {
                errors.Add(new(where, "duplicate upstream ID"));
                continue;
            }

            string clusterId = spec.Id.ToString();
            if (!clusterIds.TryAdd(clusterId, spec.Id))
            {
                errors.Add(new(where, CaseCollision("upstream", clusterId, clusterIds[clusterId].ToString())));
                continue;
            }

            // Keyed like YARP keys destinations (ignoring case).
            Dictionary<string, DestinationConfig> destinations = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, DestinationId> destinationIds = new(StringComparer.OrdinalIgnoreCase);
            if (spec.Destinations is null || spec.Destinations.Count == 0)
            {
                errors.Add(new(where, "needs at least one destination"));
            }

            foreach (DestinationSpec destination in spec.Destinations ?? [])
            {
                string destinationId = destination.Id.ToString();
                CompileErrorSource destinationWhere = CompileErrorSource.Destination(spec.Id, destination.Id);
                if (destination.Id.IsEmpty)
                {
                    errors.Add(new(destinationWhere, "the destination ID is empty"));
                }
                else if (!Uri.TryCreate(destination.Address, UriKind.Absolute, out Uri? address)
                    || (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps))
                {
                    errors.Add(new(destinationWhere, $"needs an absolute http or https address, got '{destination.Address}'"));
                }
                else if (!destinationIds.TryAdd(destinationId, destination.Id))
                {
                    DestinationId existing = destinationIds[destinationId];
                    errors.Add(new(destinationWhere, existing == destination.Id
                        ? "duplicate destination ID"
                        : CaseCollision("destination", destinationId, existing.ToString())));
                }
                else
                {
                    destinations.Add(destinationId, new DestinationConfig { Address = destination.Address });
                }
            }

            if (spec.MaxConnectionsPerServer is < 1)
            {
                errors.Add(new(where, "MaxConnectionsPerServer must be positive"));
            }

            if (spec.ActiveHealth is { } active
                && (active.Path is not ['/', ..] || active.Path.Contains('?', StringComparison.Ordinal) || active.Interval <= TimeSpan.Zero || active.Timeout <= TimeSpan.Zero))
            {
                errors.Add(new(where, "an active health check needs a path starting with '/' (no query) and a positive interval and timeout"));
            }

            clusters[spec.Id] = new ClusterConfig
            {
                ClusterId = clusterId,
                Destinations = destinations,
                LoadBalancingPolicy = spec.LoadBalancing,
                SessionAffinity = spec.SessionAffinity
                    ? new SessionAffinityConfig
                    {
                        Enabled = true,
                        Policy = "Cookie",
                        FailurePolicy = "Redistribute",
                        AffinityKeyName = AffinityCookieName,
                    }
                    : null,
                HealthCheck = spec.PassiveHealth || spec.ActiveHealth is not null
                    ? new HealthCheckConfig
                    {
                        Passive = spec.PassiveHealth
                            ? new PassiveHealthCheckConfig
                            {
                                Enabled = true,
                                Policy = HealthCheckConstants.PassivePolicy.TransportFailureRate,
                                ReactivationPeriod = spec.ReactivationPeriod,
                            }
                            : null,
                        Active = spec.ActiveHealth is { } probe
                            ? new ActiveHealthCheckConfig
                            {
                                Enabled = true,
                                Interval = probe.Interval,
                                Timeout = probe.Timeout,
                                Policy = HealthCheckConstants.ActivePolicy.ConsecutiveFailures,
                                Path = probe.Path,
                            }
                            : null,
                    }
                    : null,
                HttpClient = spec.MaxConnectionsPerServer is { } max ? new HttpClientConfig { MaxConnectionsPerServer = max } : null,
            };
        }

        return clusters;
    }

    private static CompiledRoute[] CompileRoutes(
        HostSpec host,
        Dictionary<UpstreamId, ClusterConfig> clusters,
        HashSet<RouteId> routeIds,
        Dictionary<string, RouteId> yarpRouteIds,
        List<RouteConfig> yarpRoutes,
        List<RouteId> routeIdBySlot,
        CompileErrorSource where,
        List<CompileError> errors)
    {
        IReadOnlyList<RouteSpec> specs = host.Routes ?? [];
        if (specs.Count == 0)
        {
            errors.Add(new(where, "a proxy host needs at least one route"));
        }

        if (host.Redirect is not null || host.Respond is not null)
        {
            errors.Add(new(where, "a proxy host can't have a redirect or respond setting"));
        }

        // Stable sort by Order: equal orders keep the listed order.
        int[] order = new int[specs.Count];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (a, b) => specs[a].Order != specs[b].Order ? specs[a].Order.CompareTo(specs[b].Order) : a.CompareTo(b));

        CompiledRoute[] routes = new CompiledRoute[specs.Count];
        for (int i = 0; i < order.Length; i++)
        {
            RouteSpec spec = specs[order[i]];
            string routeId = spec.Id.ToString();
            CompileErrorSource routeWhere = CompileErrorSource.Route(host.Id, spec.Id);
            if (spec.Id.IsEmpty)
            {
                errors.Add(new(routeWhere, "the route ID is empty"));
            }
            else if (!routeIds.Add(spec.Id))
            {
                errors.Add(new(routeWhere, "duplicate route ID"));
            }
            else if (!yarpRouteIds.TryAdd(routeId, spec.Id))
            {
                errors.Add(new(routeWhere, CaseCollision("route", routeId, yarpRouteIds[routeId].ToString())));
            }

            if (!Enum.IsDefined(spec.Match))
            {
                errors.Add(new(routeWhere, $"unknown match kind {spec.Match}"));
            }

            string path = spec.Path ?? string.Empty;
            if (path.Length == 0 || path[0] != '/' || path.Contains('?', StringComparison.Ordinal) || path.Contains('#', StringComparison.Ordinal))
            {
                errors.Add(new(routeWhere, $"the path must start with '/' and have no query or fragment, got '{path}'"));
            }
            else if (path.Contains('%', StringComparison.Ordinal))
            {
                errors.Add(new(routeWhere, $"write the path decoded, without percent-encoding, got '{path}'"));
            }
            else if (RoutePath.Canonicalize(path) is var canonical && !string.Equals(canonical, path, StringComparison.Ordinal))
            {
                // Requests are matched on the canonical view, so a non-canonical route path could never match.
                errors.Add(new(routeWhere, $"the path isn't canonical ('\\', '//', ';', dot segments, trailing dots or spaces); use '{canonical}'"));
            }

            ClusterConfig? cluster = null;
            if (spec.Upstream.IsEmpty)
            {
                errors.Add(new(routeWhere, "the route's upstream ID is empty"));
            }
            else if (!clusters.TryGetValue(spec.Upstream, out cluster))
            {
                errors.Add(new(routeWhere, $"upstream '{spec.Upstream}' doesn't exist"));
            }

            // Only the route's own chain: the gate runs the host's chain earlier, before redirects and fixed answers.
            IRequestPolicy[] own = CompilePolicies(spec.Policies ?? [], routeWhere, errors);

            int slot = routeIdBySlot.Count;
            routeIdBySlot.Add(spec.Id);
            yarpRoutes.Add(new RouteConfig
            {
                RouteId = routeId,
                ClusterId = cluster?.ClusterId,
                Match = new RouteMatch { Path = InternalRoutePrefix + routeId + "/{**rest}" },
            });
            routes[i] = new CompiledRoute
            {
                Id = spec.Id,
                MatchKind = spec.Match,
                Path = path,
                Slot = slot,
                Policies = own,
            };
        }

        return routes;
    }

    private static CompiledRoute[] RejectRoutes(HostSpec host, CompileErrorSource where, List<CompileError> errors)
    {
        if (host.Routes is { Count: > 0 })
        {
            errors.Add(new(where, "only proxy hosts have routes"));
        }

        return [];
    }

    private static PrecomputedResponse? CompileFixedResponse(HostSpec host, CompileErrorSource where, List<CompileError> errors)
    {
        switch (host.Kind)
        {
            case HostKind.Redirect:
                if (host.Respond is not null)
                {
                    errors.Add(new(where, "a redirect host can't have a respond setting"));
                }

                if (host.Redirect is not { } redirect)
                {
                    errors.Add(new(where, "a redirect host needs a redirect target"));
                    return null;
                }

                if (!Uri.TryCreate(redirect.Target, UriKind.Absolute, out Uri? target)
                    || (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
                {
                    errors.Add(new(where, $"the redirect target must be an absolute http or https URL, got '{redirect.Target}'"));
                    return null;
                }

                if (redirect.StatusCode is not (301 or 302 or 303 or 307 or 308))
                {
                    errors.Add(new(where, $"redirect status {redirect.StatusCode} isn't one of 301, 302, 303, 307, 308"));
                    return null;
                }

                if (redirect.PreservePath && (target.Query.Length > 0 || target.Fragment.Length > 0))
                {
                    errors.Add(new(where, "a redirect that preserves the path can't have a query or fragment in its target"));
                    return null;
                }

                return redirect.PreservePath
                    ? PrecomputedResponse.RedirectTo(redirect.StatusCode, new RedirectTarget(redirect.Target.TrimEnd('/'), IncludeRequestHost: false, string.Empty, AppendPathAndQuery: true))
                    : PrecomputedResponse.RedirectTo(redirect.StatusCode, redirect.Target);

            case HostKind.Respond:
                if (host.Redirect is not null)
                {
                    errors.Add(new(where, "a respond host can't have a redirect target"));
                }

                if (host.Respond is not { } respond)
                {
                    errors.Add(new(where, "a respond host needs a status and body"));
                    return null;
                }

                if (respond.StatusCode is < 200 or > 599)
                {
                    errors.Add(new(where, $"respond status {respond.StatusCode} must be 200–599"));
                    return null;
                }

                if (PrecomputedResponse.ForbidsBody(respond.StatusCode) && !string.IsNullOrEmpty(respond.Body))
                {
                    errors.Add(new(where, $"a {respond.StatusCode} response can't have a body"));
                    return null;
                }

                return PrecomputedResponse.Create(respond.StatusCode, Encoding.UTF8.GetBytes(respond.Body ?? string.Empty), respond.ContentType);

            default:
                return null;
        }
    }

    private static IRequestPolicy[] CompilePolicies(IReadOnlyList<PolicySpec> specs, CompileErrorSource where, List<CompileError> errors)
    {
        IRequestPolicy[] policies = new IRequestPolicy[specs.Count];
        for (int i = 0; i < specs.Count; i++)
        {
            policies[i] = CompilePolicy(specs[i], where, errors);
        }

        return policies;
    }

    private static IRequestPolicy CompilePolicy(PolicySpec spec, CompileErrorSource where, List<CompileError> errors)
    {
        switch (spec)
        {
            case DenyPolicySpec deny when deny.StatusCode is < 200 or > 599:
                errors.Add(new(where, $"deny status {deny.StatusCode} must be 200–599"));
                break;
            case DenyPolicySpec deny when PrecomputedResponse.ForbidsBody(deny.StatusCode) && !string.IsNullOrEmpty(deny.Body):
                errors.Add(new(where, $"a {deny.StatusCode} deny response can't have a body"));
                break;
            case DenyPolicySpec deny:
                return new StaticPolicy(PolicyDecision.Respond(PrecomputedResponse.Text(deny.StatusCode, deny.Body ?? string.Empty)));
            case DropPolicySpec:
                return new StaticPolicy(PolicyDecision.Drop);
            case IpAllowListPolicySpec allow:
                CidrSet allowed = CompileCidrs(allow.Addresses ?? [], where, errors);
                PolicyDecision denied = allow.DropDenied
                    ? PolicyDecision.Drop
                    : PolicyDecision.Respond(PrecomputedResponse.Text(StatusCodes.Status403Forbidden, "forbidden\n"));
                return new IpAllowListPolicy(allowed, denied);
            default:
                errors.Add(new(where, $"unsupported policy {spec?.GetType().Name ?? "null"}"));
                break;
        }

        return new StaticPolicy(PolicyDecision.Continue);
    }

    private static TrustedProxyPolicy CompileTrustedProxies(TrustedProxySpec? spec, SnapshotCompilerOptions options, List<CompileError> errors)
    {
        spec ??= TrustedProxySpec.Off;
        CompileErrorSource where = CompileErrorSource.TrustedProxies;
        IReadOnlyList<string> proxies = spec.Proxies ?? [];
        bool cloudflare = spec.Mode is TrustedProxyMode.CloudflareProxy or TrustedProxyMode.CloudflareTunnel;
        if (spec.Header is not null && spec.Mode != TrustedProxyMode.Generic)
        {
            errors.Add(new(where, "a header is only configurable in Generic mode (Cloudflare modes read CF-Connecting-IP)"));
        }

        if (!cloudflare && (spec.UseTrueClientIp || spec.ForwardedForFallback))
        {
            errors.Add(new(where, "True-Client-IP and the X-Forwarded-For fallback only apply to the Cloudflare modes"));
        }

        if (spec.TrustForwardedProto && spec.Mode != TrustedProxyMode.Generic)
        {
            errors.Add(new(where, "TrustForwardedProto only applies to Generic mode (Cloudflare modes read CF-Visitor)"));
        }

        switch (spec.Mode)
        {
            case TrustedProxyMode.Off:
                if (proxies.Count > 0)
                {
                    errors.Add(new(where, "proxies are listed but the mode is Off"));
                }

                return TrustedProxyPolicy.Off;

            case TrustedProxyMode.Generic:
            case TrustedProxyMode.CloudflareTunnel:
                if (proxies.Count == 0)
                {
                    errors.Add(new(where, $"{spec.Mode} mode needs at least one proxy address or CIDR"));
                    return TrustedProxyPolicy.Off;
                }

                CidrSet peers = CompileCidrs(proxies, where, errors);
                if (spec.Mode == TrustedProxyMode.CloudflareTunnel)
                {
                    return TrustedProxyPolicy.CloudflareTunnel(peers, spec.UseTrueClientIp, spec.ForwardedForFallback);
                }

                if (spec.Header is { } header && !IsHeaderName(header))
                {
                    errors.Add(new(where, $"'{header}' isn't a valid header name"));
                    return TrustedProxyPolicy.Off;
                }

                return TrustedProxyPolicy.Generic(peers, spec.Header ?? TrustedProxyPolicy.XForwardedFor, spec.TrustForwardedProto);

            case TrustedProxyMode.CloudflareProxy:
                if (proxies.Count > 0)
                {
                    errors.Add(new(where, "CloudflareProxy mode trusts Cloudflare's published edge ranges; list no proxies"));
                }

                return TrustedProxyPolicy.CloudflareProxy(options.CloudflareEdgeRanges, spec.UseTrueClientIp, spec.ForwardedForFallback);

            default:
                errors.Add(new(where, $"unknown mode {spec.Mode}"));
                return TrustedProxyPolicy.Off;
        }
    }

    /// <summary>Addresses and CIDRs (both families) into one set; each bad entry is an error.</summary>
    private static CidrSet CompileCidrs(IReadOnlyList<string> blocks, CompileErrorSource where, List<CompileError> errors)
    {
        CidrSet.Builder builder = new();
        foreach (string block in blocks)
        {
            if (!IpNetwork.TryParse(block ?? string.Empty, out IpNetwork network, out string? error))
            {
                errors.Add(new(where, $"'{block}' isn't an IP address or CIDR ({error})"));
                continue;
            }

            builder.Add(network);
        }

        return builder.Build();
    }

    /// <summary>An RFC 9110 token.</summary>
    private static bool IsHeaderName(string name) =>
        name.Length is > 0 and <= 256 && !name.AsSpan().ContainsAnyExcept(HeaderNameChars);

    private static readonly SearchValues<char> HeaderNameChars =
        SearchValues.Create("!#$%&'*+-.^_`|~0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ");

    private static ServerCertificate? ResolveCertificate(CertificateId? id, CompileErrorSource where, IServerCertificateResolver? resolver, List<CompileError> errors)
    {
        if (id is not { } certificateId)
        {
            return null;
        }

        if (certificateId.IsEmpty)
        {
            errors.Add(new(where, "the certificate ID can't be empty (leave it unset to use the default)"));
            return null;
        }

        if (resolver is null)
        {
            errors.Add(new(where, $"certificate '{certificateId}' can't be resolved: no certificate source is configured"));
            return null;
        }

        if (!resolver.TryResolve(certificateId, out ServerCertificate? certificate))
        {
            errors.Add(new(where, $"certificate '{certificateId}' doesn't exist"));
            return null;
        }

        return certificate;
    }

    /// <summary>Equal specs give equal policies (records), so hosts with the same settings share one entry per certificate.</summary>
    private static TlsPolicy CompileTlsPolicy(HostTlsSpec? spec, CompileErrorSource where, List<CompileError> errors)
    {
        spec ??= HostTlsSpec.Default;
        if (!Enum.IsDefined(spec.MinimumVersion) || !Enum.IsDefined(spec.TcpAlpn))
        {
            // TlsPolicy would throw; report it like any other config error.
            errors.Add(new(where, $"unknown TLS setting (minimum version {spec.MinimumVersion}, TCP ALPN {spec.TcpAlpn})"));
            return TlsPolicy.Default;
        }

        return new TlsPolicy
        {
            MinimumVersion = spec.MinimumVersion,
            TcpAlpn = spec.TcpAlpn,
            Http3Enabled = spec.Http3,
        };
    }

    /// <summary>
    /// A host's DNS names, normalised: what goes into the certificate table. IP literals are left out: clients never
    /// send them as SNI (an IP-literal URL sends none and gets the default certificate). Invalid names are skipped
    /// here; the host-table pass that follows reports them.
    /// </summary>
    private static List<string> ServerNamesOf(IReadOnlyList<string> names)
    {
        List<string> serverNames = new(names.Count);
        foreach (string name in names)
        {
            if (HostNames.TryNormalize(name, out string normalized, out HostNameKind kind, out _)
                && kind != HostNameKind.IPv6Literal
                && !(kind == HostNameKind.Exact && IpParser.TryParseIPv4(normalized, out _)))
            {
                serverNames.Add(normalized);
            }
        }

        return serverNames;
    }

    /// <summary>
    /// Adds a host's server names (<see cref="ServerNamesOf"/>) to the certificate table. Runs only after the host
    /// table accepted every one of the host's names: both tables apply the same rules (<see cref="DnsNames"/>) and
    /// reject the same duplicates, so nothing is reported twice, and an error here means they disagree (a bug).
    /// </summary>
    private static void AddServerNames(
        CertificateTableBuilder certificates,
        List<string> serverNames,
        ServerCertificate certificate,
        TlsPolicy tls,
        CompileErrorSource where,
        List<CompileError> errors)
    {
        try
        {
            certificates.Add(serverNames, certificate, tls);
        }
        catch (ArgumentException ex)
        {
            errors.Add(new(where, ex.Message));
        }
    }

    /// <summary>Two distinct typed IDs whose canonical strings YARP would take for one ID.</summary>
    private static string CaseCollision(string kind, string id, string existing) =>
        $"{kind} ID '{id}' differs from '{existing}' only in case (YARP ignores case)";

    private static void RemoveSlotsFrom(CompiledRoute[] routes, List<RouteConfig> yarpRoutes, List<RouteId> routeIdBySlot)
    {
        if (routes.Length == 0)
        {
            return;
        }

        // A host's routes take the last slots, so dropping them is a tail truncation.
        int first = routeIdBySlot.Count - routes.Length;
        routeIdBySlot.RemoveRange(first, routes.Length);
        yarpRoutes.RemoveRange(first, routes.Length);
    }
}
