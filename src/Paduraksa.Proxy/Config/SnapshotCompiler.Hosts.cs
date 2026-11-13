using System.Text;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.AccessLists;
using Paduraksa.Proxy.ForwardAuth;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Http;
using Paduraksa.Proxy.Metrics;
using Paduraksa.Proxy.Pages;
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
        // By reference: the mapping hands a route without rules of its own its host's spec instance, so it shares the host's
        // compiled rules and its errors are reported once, at the host.
        private readonly Dictionary<HostHeadersSpec, CompiledHeaders> _headers = new(ReferenceEqualityComparer.Instance);

        /// <summary>The compiled form of <paramref name="spec"/>, compiled once per instance.</summary>
        public CompiledHeaders HeadersOf(HostHeadersSpec spec, CompileErrorSource where)
        {
            if (!_headers.TryGetValue(spec, out var compiled))
            {
                var errors = Errors;
                compiled = CompiledHeaders.Compile(spec, message => errors.Add(new(where, message)));
                _headers[spec] = compiled;
            }

            return compiled;
        }

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

            var hostPolicies = CompilePolicies(spec.Policies ?? [], where, Errors, AccessLists, RuleSets);

            // An admin host's answers and its app's own headers are the admin's: custom rules apply to proxy hosts only.
            var hostHeaders = spec.Kind == HostKind.Admin ? CompiledHeaders.None : HeadersOf(spec.Headers, where);

            // One policy object per (policy, host) pair, shared by every route that resolves to it: that pair owns the
            // buckets. A disabled host serves nothing and a Redirect/Respond host has no routes, so neither registers a
            // pair (the setting is validated, and inert for those kinds).
            Dictionary<RateLimitPolicyId, RateLimitPolicy>? rateLimitCache = spec.Enabled && spec.Kind is HostKind.Proxy or HostKind.Admin ? [] : null;
            RateLimitPolicy? hostRateLimit = null;
            if (rateLimitCache is not null && spec.RateLimit.EffectivePolicyId is { } hostPolicyId)
            {
                hostRateLimit = CompileRateLimitPolicy(this, spec, hostPolicyId, routeOverride: false, where, rateLimitCache);
            }

            var routes = spec.Kind switch
            {
                HostKind.Admin => CompileAdminRoute(spec, Options.AdminUpstream, RouteIdBySlot, YarpRoutes, where, Errors, hostRateLimit, AccessListOf(spec.AccessList.Effective)),
                HostKind.Proxy => CompileRoutes(
                    spec, Clusters, RouteIds, YarpRouteIds, YarpRoutes, RouteIdBySlot, where, Errors, Options.ProofOfWork,
                    this, rateLimitCache, hostRateLimit, spec.RateLimit.EffectivePolicyId),
                _ => RejectRoutes(spec, where, Errors),
            };
            var fixedResponse = CompileFixedResponse(spec, where, Errors);
            var robotsTxt = CompileRobots(spec.Robots, where, Errors);
            var errorPages = CompiledErrorPages.Compile(spec.ErrorPages);
            var maintenance = CompileMaintenance(this, spec, where, Errors);

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
            var originPulls = spec.Cloudflare.AuthenticatedOriginPulls && IsCloudflareMode(TrustedProxies.Mode);
            var tls = CompileTlsPolicy(spec.Tls, where, Errors, originPulls ? OriginPullPolicy : null);
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

            CheckRequestLimits(spec, where, Errors);
            CheckErrorPages(spec.ErrorPages, where, Errors);
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
                    : originPulls
                        ? "Authenticated Origin Pulls: Cloudflare doesn't connect to origins over HTTP/3"
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
                Names = spec.Names ?? [],
                Routes = routes,
                Policies = hostPolicies,
                FixedResponse = fixedResponse,
                RobotsTxt = robotsTxt,
                RobotsMode = spec.Robots.Mode,
                ErrorPages = errorPages,
                Maintenance = maintenance,
                RateLimit = hostRateLimit,
                OriginLockdown = CompileOriginLockdown(spec.Cloudflare, TrustedProxies.Mode, where, Errors),
                AuthenticatedOriginPulls = originPulls,
                AccessList = AccessListOf(spec.AccessList.Effective),
                RuleSet = RuleSetOf(spec.RuleSet.Effective),
                AuthProvider = spec.Kind == HostKind.Proxy ? AuthProviderOf(spec.AuthProvider.Effective) : null,
                IdentityProviders = spec.Kind == HostKind.Proxy ? IdentityProvidersOf(AuthProviderOf(spec.AuthProvider.Effective), routes) : [],
                HttpsRedirect = spec.ForceHttps ? HttpsRedirect : null,
                Tls = tls,
                Https = spec.Https,
                AccessLog = spec.AccessLog,
                RequestIdPropagation = spec.RequestIdPropagation,
                ResponseHeaders = responseHeaders,
                Headers = hostHeaders,
                ProofOfWork = spec.ProofOfWork,
                RequestLimits = spec.RequestLimits,
                ServesProofOfWork = servesProofOfWork,
                ProofOfWorkHostScope = hostScope,
                MetricSlot = MetricSlotOf(MetricSubject.Host, spec.Id.Value, spec.Enabled),
            };

            CheckIdentityHeaders(host, spec, where, Errors);
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

    /// <summary>The lockdown the gate applies: the host's own setting when the trusted-proxy mode is a Cloudflare one, else
    /// none, because only then is there a trusted peer to tell from the rest.</summary>
    private static OriginLockdownMode CompileOriginLockdown(HostCloudflareSpec spec, TrustedProxyMode mode, CompileErrorSource where, List<CompileError> errors)
    {
        if (!Enum.IsDefined(spec.OriginLockdown))
        {
            errors.Add(new(where, $"unknown origin lockdown mode {spec.OriginLockdown}"));
            return OriginLockdownMode.Off;
        }

        return IsCloudflareMode(mode) ? spec.OriginLockdown : OriginLockdownMode.Off;
    }

    private static bool IsCloudflareMode(TrustedProxyMode mode) => mode is TrustedProxyMode.CloudflareProxy or TrustedProxyMode.CloudflareTunnel;

    /// <summary>
    /// The implicit route of an admin host: one prefix route matching every path, forwarding to the
    /// <see cref="YarpConfig.AdminClusterId"/> cluster. It has no user route ID, so its slot's <see cref="RouteId"/> is empty.
    /// </summary>
    private static CompiledRoute[] CompileAdminRoute(
        HostSpec host, bool available, List<RouteId> routeIdBySlot, List<RouteConfig> yarpRoutes, CompileErrorSource where, List<CompileError> errors,
        RateLimitPolicy? rateLimit,
        AccessLists.CompiledAccessList? accessList)
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
                    Policies = rateLimit is null ? [] : [rateLimit],
                    AccessList = accessList,
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
                Policies = rateLimit is null ? [] : [rateLimit],
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
        ProofOfWorkRuntime? proofOfWork,
        CompileContext context,
        Dictionary<RateLimitPolicyId, RateLimitPolicy>? rateLimits,
        RateLimitPolicy? hostRateLimit,
        RateLimitPolicyId? hostRateLimitId)
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
            var own = CompilePolicies(spec.Policies ?? [], routeWhere, errors, context.AccessLists, context.RuleSets);

            // After the access policies and before proof of work: a client already over its budget gets the 429, not a
            // challenge page. A route that resolves to the host's policy shares its pair's buckets; another policy gets
            // its own pair for the same host.
            if (rateLimits is not null && spec.RateLimit is { } routePolicyId)
            {
                var routePolicy = routePolicyId == hostRateLimitId && hostRateLimit is not null
                    ? hostRateLimit
                    : CompileRateLimitPolicy(context, host, routePolicyId, spec.RateLimitRouteOverride, routeWhere, rateLimits);
                if (routePolicy is not null)
                {
                    own = [.. own, routePolicy];
                }
            }

            // After the rate limit (a client over its budget is told so, and costs the endpoint nothing) and before proof of work, which
            // only a request the provider let through is asked to solve.
            if (spec.AuthProvider is { } providerId)
            {
                if (context.AuthProviderOf(providerId) is { } provider)
                {
                    own = [.. own, new ForwardAuthPolicy(provider, spec.AuthProviderRouteOverride)];
                }
                else
                {
                    errors.Add(new(routeWhere, $"auth provider '{providerId}' doesn't exist or didn't compile"));
                }
            }

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
                AccessList = context.AccessListOf(spec.AccessList),
                RuleSet = context.RuleSetOf(spec.RuleSet),
                AuthProvider = context.AuthProviderOf(spec.AuthProvider),
                ProofOfWork = routeProofOfWork,
                ProofOfWorkRouteOverride = spec.ProofOfWorkRouteOverride,
                MaxBodySize = spec.MaxBodySizeRouteOverride ? spec.MaxBodySize : null,
                MetricSlot = context.MetricSlotOf(MetricSubject.Route, spec.Id.Value, host.Enabled),
                Upstream = context.UpstreamMetricsOf(spec.Upstream, host.Enabled),
                UpstreamHost = context.UpstreamHosts.GetValueOrDefault(spec.Upstream) ?? UpstreamHost.Original,
                // A route's resolved rules can only be empty when its host's are, so an empty spec (a hand-built model's default) is the host's.
                Headers = spec.Headers.IsEmpty ? context.HeadersOf(host.Headers, where) : context.HeadersOf(spec.Headers, routeWhere),
                Rewrite = CompileRewrite(spec, path, routeWhere, errors),
            };
        }

        return routes;
    }

    /// <summary>The route's rewrite, checked again (the validator's backstop); null when it does nothing.</summary>
    private static PathRewrite? CompileRewrite(RouteSpec spec, string path, CompileErrorSource where, List<CompileError> errors)
    {
        var add = string.IsNullOrEmpty(spec.AddPrefix) ? null : spec.AddPrefix;
        if (!spec.StripPrefix && add is null)
        {
            return null;
        }

        if (spec.StripPrefix && path == "/")
        {
            errors.Add(new(where, "a route on / has no prefix to strip"));
        }

        if (add is not null && PathRewrite.AddPrefixProblem(add) is { } problem)
        {
            errors.Add(new(where, $"the route's added prefix: {problem}"));
        }

        return new PathRewrite(path, spec.Match, spec.StripPrefix, add, spec.RewriteLocation);
    }

    /// <summary>
    /// A request rule may not set a header an auth provider of the host vouches for: every route strips those from the client's
    /// request, and a rule applied after the strip would forge the identity the upstream trusts.
    /// </summary>
    private static void CheckIdentityHeaders(CompiledHost host, HostSpec spec, CompileErrorSource where, List<CompileError> errors)
    {
        if (host.IdentityProviders.Length == 0)
        {
            return;
        }

        CheckOwned(host.Headers, host.IdentityProviders, where, errors);
        for (var i = 0; i < host.Routes.Length; i++)
        {
            if (!ReferenceEquals(host.Routes[i].Headers, host.Headers))
            {
                CheckOwned(host.Routes[i].Headers, host.IdentityProviders, CompileErrorSource.Route(spec.Id, host.Routes[i].Id), errors);
            }
        }

        static void CheckOwned(CompiledHeaders headers, CompiledAuthProvider[] providers, CompileErrorSource where, List<CompileError> errors)
        {
            foreach (var rule in headers.Request)
            {
                foreach (var provider in providers)
                {
                    if (rule.Operation != Core.Model.HeaderOperation.Remove && provider.Owns(rule.Name))
                    {
                        errors.Add(new(where, $"custom request header '{rule.Name}' is an identity header auth provider '{provider.Id}' vouches for; only the provider may set it"));
                    }
                }
            }
        }
    }

    /// <summary>The distinct providers the host and its routes ask, whose identity headers every route of the host must strip.</summary>
    private static CompiledAuthProvider[] IdentityProvidersOf(CompiledAuthProvider? host, CompiledRoute[] routes)
    {
        List<CompiledAuthProvider> providers = [];
        if (host is not null)
        {
            providers.Add(host);
        }

        foreach (var route in routes)
        {
            if (route.AuthProvider is { } provider && !providers.Contains(provider))
            {
                providers.Add(provider);
            }
        }

        return [.. providers];
    }

    /// <summary>The compiler's backstop for the values the validator already judged: a limit outside its bounds would let the
    /// transport refuse what the host admits, or make the gate's checks meaningless.</summary>
    private static void CheckRequestLimits(HostSpec host, CompileErrorSource where, List<CompileError> errors)
    {
        var limits = host.RequestLimits;
        CheckBodySize(limits.MaxBodySize, where, "the host", errors);
        if (limits.MaxHeaderCount is < RequestLimitBounds.MinHeaderCount or > RequestLimitBounds.MaxHeaderCount)
        {
            errors.Add(new(where, $"the host's header limit must be {RequestLimitBounds.MinHeaderCount}-{RequestLimitBounds.MaxHeaderCount} lines, got {limits.MaxHeaderCount}"));
        }

        if (limits.MaxHeaderBytes is < RequestLimitBounds.MinHeaderBytes or > RequestLimitBounds.MaxHeaderBytes)
        {
            errors.Add(new(where, $"the host's header size limit must be {RequestLimitBounds.MinHeaderBytes}-{RequestLimitBounds.MaxHeaderBytes} bytes, got {limits.MaxHeaderBytes}"));
        }

        if (limits.MaxUrlLength is < RequestLimitBounds.MinUrlLength or > RequestLimitBounds.MaxUrlLength)
        {
            errors.Add(new(where, $"the host's URL limit must be {RequestLimitBounds.MinUrlLength}-{RequestLimitBounds.MaxUrlLength} bytes, got {limits.MaxUrlLength}"));
        }

        foreach (var route in host.Routes ?? [])
        {
            if (route.MaxBodySizeRouteOverride)
            {
                CheckBodySize(route.MaxBodySize, CompileErrorSource.Route(host.Id, route.Id), "the route", errors);
            }
        }
    }

    private static void CheckBodySize(long limit, CompileErrorSource where, string what, List<CompileError> errors)
    {
        if (limit is < 0 or > RequestLimitBounds.MaxBodySize)
        {
            errors.Add(new(where, $"{what}'s body limit must be 0 (none) or at most {RequestLimitBounds.MaxBodySize} bytes, got {limit}"));
        }
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

    /// <summary>The backstop for what the validator judges: a page that can't be served as written (a mode that isn't one, a message without
    /// a heading, a document past the cap, a variable that doesn't exist) fails the compile instead of serving something else.</summary>
    internal static void CheckErrorPages(HostErrorPagesSpec pages, CompileErrorSource where, List<CompileError> errors)
    {
        if (!Enum.IsDefined(pages.ReplaceUpstream))
        {
            errors.Add(new(where, $"unknown upstream error replacement {pages.ReplaceUpstream}"));
        }

        foreach (var (key, page) in pages.Pages)
        {
            var name = ErrorPageStatuses.NameOf(key);
            if (ErrorPageStatuses.IndexOf(key) < 0)
            {
                errors.Add(new(where, $"{key} isn't a status with an error page"));
                continue;
            }

            if (!Enum.IsDefined(page.Mode))
            {
                errors.Add(new(where, $"the {name} error page has an unknown mode {page.Mode}"));
                continue;
            }

            var texts = page.Mode == ErrorPageMode.Message
                ? (Parts: new[] { page.Heading, page.Text }, Valid: !string.IsNullOrWhiteSpace(page.Heading)
                    && page.Heading.Length <= ErrorPageText.MaxHeadingLength && (page.Text ?? string.Empty).Length <= ErrorPageText.MaxTextLength
                    && ErrorPageText.IsPlainLine(page.Heading) && ErrorPageText.IsPlainLine(page.Text ?? string.Empty) && page.Html is null)
                : (Parts: new[] { page.Html }, Valid: !string.IsNullOrWhiteSpace(page.Html) && ErrorPageText.IsWithinSizeLimit(page.Html)
                    && ErrorPageText.IsPlainDocument(page.Html) && page.Heading is null && page.Text is null);
            if (!texts.Valid)
            {
                errors.Add(new(where, $"the {name} error page isn't a valid {(page.Mode == ErrorPageMode.Message ? $"message (a heading of up to {ErrorPageText.MaxHeadingLength} characters and a sentence of up to {ErrorPageText.MaxTextLength})" : "document (up to 64 KiB)")}"));
                continue;
            }

            foreach (var text in texts.Parts)
            {
                ErrorPageText.Parse(text ?? string.Empty, out var unknown);
                foreach (var variable in unknown)
                {
                    errors.Add(new(where, $"the {name} error page uses {{{variable}}}, which isn't a variable; the variables are {ErrorPageText.VariableList}"));
                }
            }
        }
    }

    /// <summary>
    /// A host's maintenance mode, or null when it serves none. The page is the host's own maintenance entry, else the built-in one, compiled
    /// for the status the host answers with; the bypass list must exist, as a policy's does.
    /// </summary>
    private static CompiledMaintenance? CompileMaintenance(CompileContext context, HostSpec spec, CompileErrorSource where, List<CompileError> errors)
    {
        var maintenance = spec.Maintenance;
        if (!spec.Enabled || !maintenance.Enabled || spec.Kind == HostKind.Admin)
        {
            return null;
        }

        if (!ErrorPageStatuses.IsMaintenanceStatus(maintenance.Status))
        {
            errors.Add(new(where, $"a maintenance answer's status is one of {string.Join(", ", ErrorPageStatuses.MaintenanceStatuses)}, got {maintenance.Status}"));
            return null;
        }

        if (maintenance.RetryAfterSeconds is < 0 or > ErrorPageStatuses.MaxRetryAfterSeconds)
        {
            errors.Add(new(where, $"the maintenance retry delay is 0 to {ErrorPageStatuses.MaxRetryAfterSeconds} seconds, got {maintenance.RetryAfterSeconds}"));
            return null;
        }

        CompiledAccessList? bypass = null;
        if (!maintenance.BypassAccessListId.IsEmpty && (bypass = context.AccessListOf(maintenance.BypassAccessListId)) is null)
        {
            errors.Add(new(where, $"the maintenance bypass access list '{maintenance.BypassAccessListId}' doesn't exist"));
            return null;
        }

        var page = spec.ErrorPages.Pages.TryGetValue(ErrorPageStatuses.MaintenanceKey, out var own)
            ? CustomErrorPage.Compile(own, maintenance.Status)
            : CustomErrorPage.BuiltInMaintenance(maintenance.Status);
        return new CompiledMaintenance(
            PrecomputedResponse.Maintenance(maintenance.Status, page),
            maintenance.RetryAfterSeconds > 0 ? CompiledMaintenance.RetryAfterOf(maintenance.RetryAfterSeconds) : null,
            bypass,
            page);
    }

    /// <summary>
    /// The host's <c>robots.txt</c> answer. Passthrough has none (the upstream's own file is served); the presets share
    /// one encoded answer each; a custom text is normalised and encoded here, once per host.
    /// </summary>
    private static PrecomputedResponse? CompileRobots(HostRobotsSpec robots, CompileErrorSource where, List<CompileError> errors)
    {
        if (!Enum.IsDefined(robots.Mode))
        {
            errors.Add(new(where, $"unknown robots.txt mode {robots.Mode}"));
            return null;
        }

        return robots.Mode switch
        {
            RobotsMode.Passthrough => null,
            RobotsMode.DisallowAll => RobotsResponses.DisallowAll,
            RobotsMode.BlockAiCrawlers => RobotsResponses.BlockAiCrawlers,
            _ when string.IsNullOrWhiteSpace(robots.CustomText) => MissingCustomRobots(where, errors),
            _ => RobotsResponses.Custom(robots.CustomText!),
        };

        static PrecomputedResponse? MissingCustomRobots(CompileErrorSource where, List<CompileError> errors)
        {
            errors.Add(new(where, "a custom robots.txt needs text"));
            return null;
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
