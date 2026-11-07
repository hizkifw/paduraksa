using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text;

using Microsoft.AspNetCore.Http;

using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Tls;
using Paduraksa.Proxy.Yarp;

using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Health;

namespace Paduraksa.Proxy.Config;

/// <summary>Outcome of <see cref="SnapshotCompiler.Compile"/>: an unbound candidate, or every error found.</summary>
public sealed record CompileResult(RuntimeSnapshot? Snapshot, IReadOnlyList<string> Errors)
{
    public bool Succeeded => Snapshot is not null;
}

/// <summary>
/// Validates a <see cref="ProxyConfigModel"/> and compiles it into an unbound <see cref="RuntimeSnapshot"/> candidate
/// plus its YARP config (PLAN §6.1 step 3–4). Cold path: it may allocate freely. It reports every error it finds
/// rather than stopping at the first. YARP's own validator runs later, in <c>SnapshotPublisher</c>.
/// <para>YARP config is a pure function of content: route ID = route key, cluster ID = upstream key, destination ID
/// = destination key, each route path-only (<c>/.paduraksa/r/{key}/{**rest}</c>), never requested because the gate
/// picks the endpoint itself.</para>
/// <para>TLS: every enabled host's DNS names (exact and wildcard) go into the snapshot's <see cref="CertificateTable"/>
/// with the host's certificate (or the default one) and its TLS settings; the default certificate serves unknown or
/// absent SNI. References are resolved through <see cref="SnapshotCompilerOptions.Certificates"/>.</para>
/// </summary>
public static class SnapshotCompiler
{
    /// <summary>Prefix of the internal YARP route paths; nothing ever matches on them.</summary>
    public const string InternalRoutePrefix = "/.paduraksa/r/";

    /// <summary>Cookie name for session affinity.</summary>
    public const string AffinityCookieName = ".Paduraksa.Affinity";

    private static readonly SearchValues<char> KeyChars =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_.~-");

    /// <summary>Compiles <paramref name="model"/> with <paramref name="options"/> (default: <see cref="SnapshotCompilerOptions.Default"/>).</summary>
    public static CompileResult Compile(ProxyConfigModel model, SnapshotCompilerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        options ??= SnapshotCompilerOptions.Default;
        List<string> errors = [];

        DefaultsSpec defaults = model.Defaults ?? new DefaultsSpec();
        CertificateTableBuilder certificates = new();
        ServerCertificate? defaultCertificate = ResolveCertificate(defaults.Certificate, "defaults", options.Certificates, errors);
        if (defaultCertificate is not null)
        {
            certificates.SetDefault(defaultCertificate, CompileTlsPolicy(defaults.Tls));
        }

        TrustedProxyPolicy trustedProxies = CompileTrustedProxies(defaults.TrustedProxies, options, errors);
        Dictionary<string, ClusterConfig> clusters = CompileUpstreams(model.Upstreams ?? [], errors);

        HostTable.Builder table = new();
        HashSet<string> hostKeys = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> routeKeys = new(StringComparer.OrdinalIgnoreCase);
        List<CompiledHost> hosts = [];
        List<RouteConfig> yarpRoutes = [];
        List<string> routeIdBySlot = [];
        HttpsRedirect? httpsRedirect = null;
        AltSvcHeader altSvc = new(options.PublicHttpsPort, options.AdvertiseHttp3);

        foreach (HostSpec spec in model.Hosts ?? [])
        {
            string where = $"host '{spec.Key}'";
            if (!IsValidKey(spec.Key))
            {
                errors.Add($"{where}: invalid key (letters, digits and _ . ~ -, up to 128 characters)");
            }
            else if (!hostKeys.Add(spec.Key))
            {
                errors.Add($"{where}: duplicate host key");
            }

            IRequestPolicy[] hostPolicies = CompilePolicies(spec.Policies ?? [], where, errors);
            CompiledRoute[] routes = spec.Kind == HostKind.Proxy
                ? CompileRoutes(spec, clusters, routeKeys, yarpRoutes, routeIdBySlot, where, errors)
                : RejectRoutes(spec, where, errors);
            PrecomputedResponse? fixedResponse = CompileFixedResponse(spec, where, errors);

            if (spec.ForceHttps)
            {
                httpsRedirect ??= new HttpsRedirect(options.PublicHttpsPort);
            }

            ServerCertificate? certificate = spec.Certificate is null
                ? defaultCertificate
                : ResolveCertificate(spec.Certificate, where, options.Certificates, errors);
            TlsPolicy tls = CompileTlsPolicy(spec.Tls);
            List<string> serverNames = spec.Enabled && certificate is not null ? ServerNamesOf(spec.Names ?? []) : [];

            CompiledHost host = new()
            {
                Index = hosts.Count,
                Key = spec.Key ?? string.Empty,
                Kind = spec.Kind,
                Routes = routes,
                Policies = hostPolicies,
                FixedResponse = fixedResponse,
                HttpsRedirect = spec.ForceHttps ? httpsRedirect : null,
                Tls = tls,
                // Only hosts with names in the certificate table can be reached over HTTP/3 (an IP-literal-only
                // host has none: its HTTPS rides on the default certificate's entry and that entry's settings).
                AltSvc = serverNames.Count > 0 ? altSvc.ForHost(tls) : default,
            };

            if (spec.Names is null || spec.Names.Count == 0)
            {
                errors.Add($"{where}: needs at least one name");
            }

            // A disabled host's names go into a table of their own: validated like any others (duplicates within
            // the host included), but the host serves nothing, so it may share a name with another host.
            HostTable.Builder nameTable = spec.Enabled ? table : new HostTable.Builder();
            bool namesValid = true;
            foreach (string name in spec.Names ?? [])
            {
                if (!nameTable.TryAdd(name, host, out string? nameError))
                {
                    errors.Add($"{where}: {nameError}");
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
            Revision = string.IsNullOrEmpty(model.Revision) ? "unversioned" : model.Revision,
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

    private static Dictionary<string, ClusterConfig> CompileUpstreams(IReadOnlyList<UpstreamSpec> upstreams, List<string> errors)
    {
        // Insertion-ordered by construction; keys compared like YARP compares cluster IDs.
        Dictionary<string, ClusterConfig> clusters = new(StringComparer.OrdinalIgnoreCase);
        foreach (UpstreamSpec spec in upstreams)
        {
            string where = $"upstream '{spec.Key}'";
            if (!IsValidKey(spec.Key))
            {
                errors.Add($"{where}: invalid key (letters, digits and _ . ~ -, up to 128 characters)");
                continue;
            }

            if (clusters.ContainsKey(spec.Key))
            {
                errors.Add($"{where}: duplicate upstream key");
                continue;
            }

            Dictionary<string, DestinationConfig> destinations = new(StringComparer.OrdinalIgnoreCase);
            if (spec.Destinations is null || spec.Destinations.Count == 0)
            {
                errors.Add($"{where}: needs at least one destination");
            }

            foreach (DestinationSpec destination in spec.Destinations ?? [])
            {
                if (!IsValidKey(destination.Key))
                {
                    errors.Add($"{where}: destination '{destination.Key}' has an invalid key");
                }
                else if (!Uri.TryCreate(destination.Address, UriKind.Absolute, out Uri? address)
                    || (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps))
                {
                    errors.Add($"{where}: destination '{destination.Key}' needs an absolute http or https address, got '{destination.Address}'");
                }
                else if (!destinations.TryAdd(destination.Key, new DestinationConfig { Address = destination.Address }))
                {
                    errors.Add($"{where}: duplicate destination key '{destination.Key}'");
                }
            }

            if (spec.MaxConnectionsPerServer is < 1)
            {
                errors.Add($"{where}: MaxConnectionsPerServer must be positive");
            }

            if (spec.ActiveHealth is { } active
                && (active.Path is not ['/', ..] || active.Path.Contains('?', StringComparison.Ordinal) || active.Interval <= TimeSpan.Zero || active.Timeout <= TimeSpan.Zero))
            {
                errors.Add($"{where}: an active health check needs a path starting with '/' (no query) and a positive interval and timeout");
            }

            clusters[spec.Key] = new ClusterConfig
            {
                ClusterId = spec.Key,
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
        Dictionary<string, ClusterConfig> clusters,
        HashSet<string> routeKeys,
        List<RouteConfig> yarpRoutes,
        List<string> routeIdBySlot,
        string where,
        List<string> errors)
    {
        IReadOnlyList<RouteSpec> specs = host.Routes ?? [];
        if (specs.Count == 0)
        {
            errors.Add($"{where}: a proxy host needs at least one route");
        }

        if (host.Redirect is not null || host.Respond is not null)
        {
            errors.Add($"{where}: a proxy host can't have a redirect or respond setting");
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
            string routeWhere = $"{where}, route '{spec.Key}'";
            if (!IsValidKey(spec.Key))
            {
                errors.Add($"{routeWhere}: invalid key (letters, digits and _ . ~ -, up to 128 characters)");
            }
            else if (!routeKeys.Add(spec.Key))
            {
                errors.Add($"{routeWhere}: duplicate route key");
            }

            string path = spec.Path ?? string.Empty;
            if (path.Length == 0 || path[0] != '/' || path.Contains('?', StringComparison.Ordinal) || path.Contains('#', StringComparison.Ordinal))
            {
                errors.Add($"{routeWhere}: the path must start with '/' and have no query or fragment, got '{path}'");
            }
            else if (path.Contains('%', StringComparison.Ordinal))
            {
                errors.Add($"{routeWhere}: write the path decoded, without percent-encoding, got '{path}'");
            }
            else if (RoutePath.Canonicalize(path) is var canonical && !string.Equals(canonical, path, StringComparison.Ordinal))
            {
                // Requests are matched on the canonical view, so a non-canonical route path could never match.
                errors.Add($"{routeWhere}: the path isn't canonical ('\\', '//', ';', dot segments, trailing dots or spaces); use '{canonical}'");
            }

            if (spec.Upstream is null || !clusters.ContainsKey(spec.Upstream))
            {
                errors.Add($"{routeWhere}: upstream '{spec.Upstream}' doesn't exist");
            }

            // Only the route's own chain: the gate runs the host's chain earlier, before redirects and fixed answers.
            IRequestPolicy[] own = CompilePolicies(spec.Policies ?? [], routeWhere, errors);

            int slot = routeIdBySlot.Count;
            routeIdBySlot.Add(spec.Key ?? string.Empty);
            yarpRoutes.Add(new RouteConfig
            {
                RouteId = spec.Key ?? string.Empty,
                ClusterId = spec.Upstream,
                Match = new RouteMatch { Path = InternalRoutePrefix + spec.Key + "/{**rest}" },
            });
            routes[i] = new CompiledRoute
            {
                Key = spec.Key ?? string.Empty,
                MatchKind = spec.Match,
                Path = path,
                Slot = slot,
                Policies = own,
            };
        }

        return routes;
    }

    private static CompiledRoute[] RejectRoutes(HostSpec host, string where, List<string> errors)
    {
        if (host.Routes is { Count: > 0 })
        {
            errors.Add($"{where}: only proxy hosts have routes");
        }

        return [];
    }

    private static PrecomputedResponse? CompileFixedResponse(HostSpec host, string where, List<string> errors)
    {
        switch (host.Kind)
        {
            case HostKind.Redirect:
                if (host.Respond is not null)
                {
                    errors.Add($"{where}: a redirect host can't have a respond setting");
                }

                if (host.Redirect is not { } redirect)
                {
                    errors.Add($"{where}: a redirect host needs a redirect target");
                    return null;
                }

                if (!Uri.TryCreate(redirect.Target, UriKind.Absolute, out Uri? target)
                    || (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
                {
                    errors.Add($"{where}: the redirect target must be an absolute http or https URL, got '{redirect.Target}'");
                    return null;
                }

                if (redirect.StatusCode is not (301 or 302 or 303 or 307 or 308))
                {
                    errors.Add($"{where}: redirect status {redirect.StatusCode} isn't one of 301, 302, 303, 307, 308");
                    return null;
                }

                if (redirect.PreservePath && (target.Query.Length > 0 || target.Fragment.Length > 0))
                {
                    errors.Add($"{where}: a redirect that preserves the path can't have a query or fragment in its target");
                    return null;
                }

                return redirect.PreservePath
                    ? PrecomputedResponse.RedirectTo(redirect.StatusCode, new RedirectTarget(redirect.Target.TrimEnd('/'), IncludeRequestHost: false, string.Empty, AppendPathAndQuery: true))
                    : PrecomputedResponse.RedirectTo(redirect.StatusCode, redirect.Target);

            case HostKind.Respond:
                if (host.Redirect is not null)
                {
                    errors.Add($"{where}: a respond host can't have a redirect target");
                }

                if (host.Respond is not { } respond)
                {
                    errors.Add($"{where}: a respond host needs a status and body");
                    return null;
                }

                if (respond.StatusCode is < 200 or > 599)
                {
                    errors.Add($"{where}: respond status {respond.StatusCode} must be 200–599");
                    return null;
                }

                if (PrecomputedResponse.ForbidsBody(respond.StatusCode) && !string.IsNullOrEmpty(respond.Body))
                {
                    errors.Add($"{where}: a {respond.StatusCode} response can't have a body");
                    return null;
                }

                return PrecomputedResponse.Create(respond.StatusCode, Encoding.UTF8.GetBytes(respond.Body ?? string.Empty), respond.ContentType);

            default:
                return null;
        }
    }

    private static IRequestPolicy[] CompilePolicies(IReadOnlyList<PolicySpec> specs, string where, List<string> errors)
    {
        IRequestPolicy[] policies = new IRequestPolicy[specs.Count];
        for (int i = 0; i < specs.Count; i++)
        {
            policies[i] = CompilePolicy(specs[i], where, errors);
        }

        return policies;
    }

    private static IRequestPolicy CompilePolicy(PolicySpec spec, string where, List<string> errors)
    {
        switch (spec)
        {
            case DenyPolicySpec deny when deny.StatusCode is < 200 or > 599:
                errors.Add($"{where}: deny status {deny.StatusCode} must be 200–599");
                break;
            case DenyPolicySpec deny when PrecomputedResponse.ForbidsBody(deny.StatusCode) && !string.IsNullOrEmpty(deny.Body):
                errors.Add($"{where}: a {deny.StatusCode} deny response can't have a body");
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
                errors.Add($"{where}: unsupported policy {spec?.GetType().Name ?? "null"}");
                break;
        }

        return new StaticPolicy(PolicyDecision.Continue);
    }

    private static TrustedProxyPolicy CompileTrustedProxies(TrustedProxySpec? spec, SnapshotCompilerOptions options, List<string> errors)
    {
        spec ??= TrustedProxySpec.Off;
        const string Where = "defaults: trusted proxies";
        IReadOnlyList<string> proxies = spec.Proxies ?? [];
        bool cloudflare = spec.Mode is TrustedProxyMode.CloudflareProxy or TrustedProxyMode.CloudflareTunnel;
        if (spec.Header is not null && spec.Mode != TrustedProxyMode.Generic)
        {
            errors.Add($"{Where}: a header is only configurable in Generic mode (Cloudflare modes read CF-Connecting-IP)");
        }

        if (!cloudflare && (spec.UseTrueClientIp || spec.ForwardedForFallback))
        {
            errors.Add($"{Where}: True-Client-IP and the X-Forwarded-For fallback only apply to the Cloudflare modes");
        }

        if (spec.TrustForwardedProto && spec.Mode != TrustedProxyMode.Generic)
        {
            errors.Add($"{Where}: TrustForwardedProto only applies to Generic mode (Cloudflare modes read CF-Visitor)");
        }

        switch (spec.Mode)
        {
            case TrustedProxyMode.Off:
                if (proxies.Count > 0)
                {
                    errors.Add($"{Where}: proxies are listed but the mode is Off");
                }

                return TrustedProxyPolicy.Off;

            case TrustedProxyMode.Generic:
            case TrustedProxyMode.CloudflareTunnel:
                if (proxies.Count == 0)
                {
                    errors.Add($"{Where}: {spec.Mode} mode needs at least one proxy address or CIDR");
                    return TrustedProxyPolicy.Off;
                }

                CidrSet peers = CompileCidrs(proxies, Where, errors);
                if (spec.Mode == TrustedProxyMode.CloudflareTunnel)
                {
                    return TrustedProxyPolicy.CloudflareTunnel(peers, spec.UseTrueClientIp, spec.ForwardedForFallback);
                }

                if (spec.Header is { } header && !IsHeaderName(header))
                {
                    errors.Add($"{Where}: '{header}' isn't a valid header name");
                    return TrustedProxyPolicy.Off;
                }

                return TrustedProxyPolicy.Generic(peers, spec.Header ?? TrustedProxyPolicy.XForwardedFor, spec.TrustForwardedProto);

            case TrustedProxyMode.CloudflareProxy:
                if (proxies.Count > 0)
                {
                    errors.Add($"{Where}: CloudflareProxy mode trusts Cloudflare's published edge ranges; list no proxies");
                }

                return TrustedProxyPolicy.CloudflareProxy(options.CloudflareEdgeRanges, spec.UseTrueClientIp, spec.ForwardedForFallback);

            default:
                errors.Add($"{Where}: unknown mode {spec.Mode}");
                return TrustedProxyPolicy.Off;
        }
    }

    /// <summary>Addresses and CIDRs (both families) into one set; each bad entry is an error.</summary>
    private static CidrSet CompileCidrs(IReadOnlyList<string> blocks, string where, List<string> errors)
    {
        CidrSet.Builder builder = new();
        foreach (string block in blocks)
        {
            if (!IpNetwork.TryParse(block ?? string.Empty, out IpNetwork network, out string? error))
            {
                errors.Add($"{where}: '{block}' isn't an IP address or CIDR ({error})");
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

    private static ServerCertificate? ResolveCertificate(string? reference, string where, IServerCertificateResolver? resolver, List<string> errors)
    {
        if (reference is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(reference))
        {
            errors.Add($"{where}: the certificate reference can't be empty (leave it unset to use the default)");
            return null;
        }

        if (resolver is null)
        {
            errors.Add($"{where}: certificate '{reference}' can't be resolved: no certificate source is configured");
            return null;
        }

        if (!resolver.TryResolve(reference, out ServerCertificate? certificate))
        {
            errors.Add($"{where}: certificate '{reference}' doesn't exist");
            return null;
        }

        return certificate;
    }

    /// <summary>Equal specs give equal policies (records), so hosts with the same settings share one entry per certificate.</summary>
    private static TlsPolicy CompileTlsPolicy(HostTlsSpec? spec)
    {
        spec ??= HostTlsSpec.Default;
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
        string where,
        List<string> errors)
    {
        try
        {
            certificates.Add(serverNames, certificate, tls);
        }
        catch (ArgumentException ex)
        {
            errors.Add($"{where}: {ex.Message}");
        }
    }

    private static void RemoveSlotsFrom(CompiledRoute[] routes, List<RouteConfig> yarpRoutes, List<string> routeIdBySlot)
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

    private static bool IsValidKey([NotNullWhen(true)] string? key) =>
        key is { Length: > 0 and <= 128 } && !key.AsSpan().ContainsAnyExcept(KeyChars);
}
