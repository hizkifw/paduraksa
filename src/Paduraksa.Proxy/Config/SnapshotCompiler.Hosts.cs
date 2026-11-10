using System.Text;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Http;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Tls;
using Paduraksa.Proxy.Yarp;

using Yarp.ReverseProxy.Configuration;

namespace Paduraksa.Proxy.Config;

/// <summary>One host, from its spec to its compiled routes, headers and certificate-table entries.</summary>
public static partial class SnapshotCompiler
{
    internal sealed partial class CompileContext
    {
        /// <summary>
        /// Compiles one host into the candidate, reporting every rule it breaks. The admin-host rules live here, so a
        /// test can run one host through them without a whole document.
        /// </summary>
        public void CompileHost(HostSpec spec)
        {
            var where = CompileErrorSource.Host(spec.Id);
            if (spec.Id.IsEmpty)
            {
                Errors.Add(new(where, "the host ID is empty"));
            }
            else if (!HostIds.Add(spec.Id))
            {
                Errors.Add(new(where, "duplicate host ID"));
            }

            if (!Enum.IsDefined(spec.Kind))
            {
                Errors.Add(new(where, $"unknown host kind {spec.Kind}"));
            }

            if (spec.Kind == HostKind.Admin)
            {
                if (AdminHost)
                {
                    Errors.Add(new(where, "only one admin host can exist"));
                }

                AdminHost = true;
                if (!spec.ForceHttps)
                {
                    Errors.Add(new(where, "an admin host forces HTTPS"));
                }
            }

            var hostPolicies = CompilePolicies(spec.Policies ?? [], where, Errors);
            var routes = spec.Kind switch
            {
                HostKind.Admin => CompileAdminRoute(spec, Options.AdminUpstream, RouteIdBySlot, YarpRoutes, where, Errors),
                HostKind.Proxy => CompileRoutes(spec, Clusters, RouteIds, YarpRouteIds, YarpRoutes, RouteIdBySlot, where, Errors, Options.ProofOfWork),
                _ => RejectRoutes(spec, where, Errors),
            };
            var fixedResponse = CompileFixedResponse(spec, where, Errors);

            if (spec.ForceHttps)
            {
                HttpsRedirect ??= new HttpsRedirect(Options.PublicHttpsPort);
            }

            if (!spec.Https && spec.ForceHttps)
            {
                Errors.Add(new(where, "a host without HTTPS can't force HTTPS"));
            }

            if (!spec.Https && spec.Certificate is not null)
            {
                Errors.Add(new(where, "a host without HTTPS can't name a certificate"));
            }

            var certificate = !spec.Https ? null
                : spec.Certificate is null ? DefaultCertificate
                : ResolveCertificate(spec.Certificate, where, Options.Certificates, Errors);
            var nameCertificates = ResolveNameCertificates(spec, where, Options.Certificates, Errors);
            var tls = CompileTlsPolicy(spec.Tls, where, Errors);
            var servesProofOfWork = spec.Kind == HostKind.Proxy
                && (spec.ProofOfWork.Enabled || AnyProofOfWorkRoute(spec));
            if (servesProofOfWork)
            {
                CheckProofOfWork(spec.ProofOfWork, where, "the host", Errors);
                foreach (var route in spec.Routes ?? [])
                {
                    if (!route.ProofOfWorkRouteOverride)
                    {
                        continue;
                    }

                    if (route.ProofOfWork.Enabled)
                    {
                        CheckProofOfWork(route.ProofOfWork, CompileErrorSource.Route(spec.Id, route.Id), "the route", Errors);
                    }

                    if (route.ProofOfWork.Difficulty != spec.ProofOfWork.Difficulty
                        || route.ProofOfWork.TokenLifetimeHours != spec.ProofOfWork.TokenLifetimeHours)
                    {
                        Errors.Add(new(CompileErrorSource.Route(spec.Id, route.Id), "a route only overrides the proof-of-work switch; the difficulty and token lifetime are the host's"));
                    }
                }
            }

            var hostScope = HostScopeOf(spec.Id);
            var serverNames =
                spec.Enabled ? GroupByCertificate(ServerNamesOf(spec.Names ?? []), certificate, nameCertificates) : [];
            // Only hosts with names in the certificate table can be reached over HTTP/3 (an IP-literal-only host has none:
            // its HTTPS rides on the default certificate's entry and that entry's settings).
            var responseHeaders = CompileResponseHeaders(
                serverHeader: spec.ServerHeader,
                hsts: spec.Hsts,
                altSvc: serverNames.Count > 0 ? AltSvc.ForHost(tls) : default,
                altSvcReason: serverNames.Count == 0
                    ? "its names are IP literals, which never arrive as a server name"
                    : "the host doesn't serve HTTP/3",
                hstsReason: "the host has HSTS off",
                propagation: spec.RequestIdPropagation,
                where,
                Errors);

            CompiledHost host = new()
            {
                Index = Hosts.Count,
                Id = spec.Id,
                Kind = spec.Kind,
                Routes = routes,
                Policies = hostPolicies,
                FixedResponse = fixedResponse,
                HttpsRedirect = spec.ForceHttps ? HttpsRedirect : null,
                Tls = tls,
                Https = spec.Https,
                AccessLog = spec.AccessLog,
                RequestIdPropagation = spec.RequestIdPropagation,
                ResponseHeaders = responseHeaders,
                ProofOfWork = spec.ProofOfWork,
                ServesProofOfWork = servesProofOfWork,
                ProofOfWorkHostScope = hostScope,
            };

            if (spec.Names is null || spec.Names.Count == 0)
            {
                Errors.Add(new(where, "needs at least one name"));
            }

            // A disabled host's names go into a table of their own: validated like any others (duplicates within
            // the host included), but the host serves nothing, so it may share a name with another host.
            var nameTable = spec.Enabled ? Table : new HostTable.Builder();
            var namesValid = true;
            foreach (var name in spec.Names ?? [])
            {
                if (!nameTable.TryAdd(name, host, out var nameError))
                {
                    Errors.Add(new(where, nameError!));
                    namesValid = false;
                }
            }

            if (namesValid)
            {
                foreach (var (named, names) in serverNames)
                {
                    AddServerNames(Certificates, names, named, tls, where, Errors);
                }
            }

            if (spec.Enabled)
            {
                Hosts.Add(host);
                foreach (var route in spec.Kind == HostKind.Proxy ? spec.Routes ?? [] : [])
                {
                    if (WithoutDestinations.Contains(route.Upstream) && Reported.Add(route.Upstream))
                    {
                        Errors.Add(new(CompileErrorSource.Upstream(route.Upstream), "needs at least one destination: an enabled host routes to it"));
                    }
                }
            }
            else
            {
                // A disabled host takes no names, routes or slots; drop what it registered.
                RemoveSlotsFrom(routes, YarpRoutes, RouteIdBySlot);
            }
        }
    }

    /// <summary>
    /// The implicit route of an admin host: one prefix route matching every path, forwarding to the
    /// <see cref="YarpConfig.AdminClusterId"/> cluster. It has no user route ID, so its slot's <see cref="RouteId"/> is empty.
    /// </summary>
    private static CompiledRoute[] CompileAdminRoute(
        HostSpec host, bool available, List<RouteId> routeIdBySlot, List<RouteConfig> yarpRoutes, CompileErrorSource where, List<CompileError> errors)
    {
        if (host.Routes is { Count: > 0 })
        {
            errors.Add(new(where, "an admin host has no routes of its own: it forwards every path to the admin app"));
        }

        if (host.Redirect is not null || host.Respond is not null)
        {
            errors.Add(new(where, "an admin host can't have a redirect or respond setting"));
        }

        if (!host.Https)
        {
            errors.Add(new(where, "an admin host is served over HTTPS"));
        }

        // With the internal listener disabled the route still matches, but there is no YARP endpoint, so the gate answers the
        // snapshot's 503 instead of taking every other host down with it.
        if (!available)
        {
            return
            [
                new CompiledRoute
                {
                    Id = RouteId.Empty,
                    MatchKind = RouteMatchKind.Prefix,
                    Path = "/",
                    Slot = -1,
                    Policies = [],
                    Admin = true,
                },
            ];
        }

        var slot = routeIdBySlot.Count;
        routeIdBySlot.Add(RouteId.Empty);
        var routeId = YarpConfig.AdminClusterId + "-" + host.Id;
        yarpRoutes.Add(new RouteConfig
        {
            RouteId = routeId,
            ClusterId = YarpConfig.AdminClusterId,
            Match = new RouteMatch { Path = InternalRoutePrefix + routeId + "/{**rest}" },
        });
        return
        [
            new CompiledRoute
            {
                Id = RouteId.Empty,
                MatchKind = RouteMatchKind.Prefix,
                Path = "/",
                Slot = slot,
                Policies = [],
                Admin = true,
            },
        ];
    }

    private static CompiledRoute[] CompileRoutes(
        HostSpec host,
        Dictionary<UpstreamId, ClusterConfig> clusters,
        HashSet<RouteId> routeIds,
        Dictionary<string, RouteId> yarpRouteIds,
        List<RouteConfig> yarpRoutes,
        List<RouteId> routeIdBySlot,
        CompileErrorSource where,
        List<CompileError> errors,
        ProofOfWorkRuntime? proofOfWork)
    {
        var specs = host.Routes ?? [];
        if (specs.Count == 0)
        {
            errors.Add(new(where, "a proxy host needs at least one route"));
        }

        if (host.Redirect is not null || host.Respond is not null)
        {
            errors.Add(new(where, "a proxy host can't have a redirect or respond setting"));
        }

        // The evaluation order, fixed here so matching stays a scan: exact first, then the longest prefix, then Order,
        // then as listed. The listed order is the last tiebreaker, never the rule.
        var keys = new RouteOrderKey[specs.Count];
        var order = new int[specs.Count];
        for (var i = 0; i < order.Length; i++)
        {
            keys[i] = RouteOrderKey.For(specs[i].Match, specs[i].Path, specs[i].Order, i);
            order[i] = i;
        }

        Array.Sort(order, (a, b) => keys[a].CompareTo(keys[b]));

        var routes = new CompiledRoute[specs.Count];
        for (var i = 0; i < order.Length; i++)
        {
            var spec = specs[order[i]];
            var routeId = spec.Id.ToString();
            var routeWhere = CompileErrorSource.Route(host.Id, spec.Id);
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

            var path = spec.Path ?? string.Empty;
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
            var own = CompilePolicies(spec.Policies ?? [], routeWhere, errors);
            var routeProofOfWork = spec.ProofOfWorkRouteOverride ? spec.ProofOfWork : host.ProofOfWork;
            if (routeProofOfWork.Enabled)
            {
                // After the route's other policies: an allow list or a deny has already decided, so the challenge is only
                // reached by a request the rest of the chain lets through.
                own = [.. own, new ProofOfWorkPolicy(routeProofOfWork, HostScopeOf(host.Id), spec.ProofOfWorkRouteOverride, proofOfWork ?? ProofOfWorkRuntime.Unavailable)];
            }

            var slot = routeIdBySlot.Count;
            routeIdBySlot.Add(spec.Id);
            // YARP never matches on order: each route's internal path is unique and the gate sets the endpoint
            // itself. The compiled route order above is the only evaluation order, so these routes carry no Order.
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
                ProofOfWork = routeProofOfWork,
                ProofOfWorkRouteOverride = spec.ProofOfWorkRouteOverride,
            };
        }

        return routes;
    }

    /// <summary>The host's scope bytes: its stable <see cref="HostId"/>, so a token signed for one host never verifies on another.</summary>
    private static byte[] HostScopeOf(HostId id)
    {
        var scope = new byte[16];
        id.Value.TryWriteBytes(scope, bigEndian: true, out _);
        return scope;
    }

    private static bool AnyProofOfWorkRoute(HostSpec spec)
    {
        foreach (var route in spec.Routes ?? [])
        {
            if ((route.ProofOfWorkRouteOverride ? route.ProofOfWork : spec.ProofOfWork).Enabled)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The compiler's backstop for the values the validator and the mapping already judged.</summary>
    private static void CheckProofOfWork(HostProofOfWorkSpec spec, CompileErrorSource where, string what, List<CompileError> errors)
    {
        if (spec.Difficulty is < ProofOfWorkLimits.MinDifficulty or > ProofOfWorkLimits.MaxDifficulty)
        {
            errors.Add(new(where, $"{what}'s proof-of-work difficulty must be {ProofOfWorkLimits.MinDifficulty}-{ProofOfWorkLimits.MaxDifficulty}, got {spec.Difficulty}"));
        }

        if (spec.TokenLifetimeHours is < ProofOfWorkLimits.MinTokenLifetimeHours or > ProofOfWorkLimits.MaxTokenLifetimeHours)
        {
            errors.Add(new(where, $"{what}'s proof-of-work token lifetime must be {ProofOfWorkLimits.MinTokenLifetimeHours}-{ProofOfWorkLimits.MaxTokenLifetimeHours} hours, got {spec.TokenLifetimeHours}"));
        }
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

                if (!Uri.TryCreate(redirect.Target, UriKind.Absolute, out var target)
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
                    errors.Add(new(where, $"respond status {respond.StatusCode} must be 200-599"));
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
}
