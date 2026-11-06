using System.Buffers;
using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;

using Microsoft.AspNetCore.Http;

using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Snapshot;
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
/// </summary>
public static class SnapshotCompiler
{
    /// <summary>Prefix of the internal YARP route paths; nothing ever matches on them.</summary>
    public const string InternalRoutePrefix = "/.paduraksa/r/";

    /// <summary>Cookie name for session affinity.</summary>
    public const string AffinityCookieName = ".Paduraksa.Affinity";

    private static readonly SearchValues<char> KeyChars =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_.~-");

    public static CompileResult Compile(ProxyConfigModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        List<string> errors = [];

        DefaultsSpec defaults = model.Defaults ?? new DefaultsSpec();
        if (defaults.PublicHttpsPort is < 1 or > 65535)
        {
            errors.Add($"defaults: public HTTPS port {defaults.PublicHttpsPort} is out of range");
        }

        Dictionary<string, ClusterConfig> clusters = CompileUpstreams(model.Upstreams ?? [], errors);

        HostTable.Builder table = new();
        HashSet<string> hostKeys = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> routeKeys = new(StringComparer.OrdinalIgnoreCase);
        List<CompiledHost> hosts = [];
        List<RouteConfig> yarpRoutes = [];
        List<string> routeIdBySlot = [];
        HttpsRedirect? httpsRedirect = null;

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

            if (spec.ForceHttps && defaults.PublicHttpsPort is >= 1 and <= 65535)
            {
                httpsRedirect ??= new HttpsRedirect(defaults.PublicHttpsPort);
            }

            CompiledHost host = new()
            {
                Index = hosts.Count,
                Key = spec.Key ?? string.Empty,
                Kind = spec.Kind,
                Routes = routes,
                Policies = hostPolicies,
                FixedResponse = fixedResponse,
                HttpsRedirect = spec.ForceHttps ? httpsRedirect : null,
            };

            if (spec.Names is null || spec.Names.Count == 0)
            {
                errors.Add($"{where}: needs at least one name");
            }

            foreach (string name in spec.Names ?? [])
            {
                if (spec.Enabled && !table.TryAdd(name, host, out string? nameError))
                {
                    errors.Add($"{where}: {nameError}");
                }
                else if (!spec.Enabled && !HostNames.TryNormalize(name, out _, out _, out nameError))
                {
                    errors.Add($"{where}: {nameError}");
                }
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
                HealthCheck = spec.PassiveHealth
                    ? new HealthCheckConfig
                    {
                        Passive = new PassiveHealthCheckConfig
                        {
                            Enabled = true,
                            Policy = HealthCheckConstants.PassivePolicy.TransportFailureRate,
                            ReactivationPeriod = spec.ReactivationPeriod,
                        },
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
                HashSet<UInt128> addresses = [];
                foreach (string text in allow.Addresses ?? [])
                {
                    if (!text.Contains('%', StringComparison.Ordinal) && IPAddress.TryParse(text, out IPAddress? address))
                    {
                        addresses.Add(ClientAddressKey.From(address));
                    }
                    else
                    {
                        errors.Add($"{where}: '{text}' isn't an IP address");
                    }
                }

                FrozenSet<UInt128> allowed = addresses.ToFrozenSet();
                PolicyDecision denied = allow.DropDenied
                    ? PolicyDecision.Drop
                    : PolicyDecision.Respond(PrecomputedResponse.Text(StatusCodes.Status403Forbidden, "forbidden\n"));
                return new IpAllowListPolicy(allowed.Contains, denied);
            default:
                errors.Add($"{where}: unsupported policy {spec?.GetType().Name ?? "null"}");
                break;
        }

        return new StaticPolicy(PolicyDecision.Continue);
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
