using Paduraksa.Application.Certificates;
using Paduraksa.Application.Config;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Proxy.AccessLists;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.RateLimiting;

using Yarp.ReverseProxy.LoadBalancing;

namespace Paduraksa.Application.Compilation;

/// <summary>
/// Maps a config document onto the snapshot compiler's input: a pure function, cold path. It reads the
/// canonical form (<see cref="CanonicalConfigJson.Canonicalize"/>), so two spellings of one config give the same model.
/// It never throws: an invalid document maps to a model the compiler rejects, plus errors for the few invalid values the
/// model can't carry (the validator reports all of them first).
/// </summary>
/// <remarks>
/// <para><b>Settings.</b> Each host's settings resolve built-in defaults → global defaults → its profile → itself
/// (<see cref="HostSettingsLayers"/>), each route's allow list on top of its host's. A profile that doesn't exist adds
/// no layer (the validator rejects it: <see cref="ConfigErrorCodes.HostProfileUnknown"/>).</para>
/// <para><b>Allow lists: host chain and route chains.</b> The gate runs a host's chain before its HTTPS redirect, its
/// fixed answer and route matching, then the matched route's own chain. A route's list <i>replaces</i> the host's, so the
/// host's list can't simply sit in the host chain: a route could tighten it but never lift it. Instead:</para>
/// <list type="bullet">
/// <item>When every route ends up with the same list (none overrides the host's, or all override it alike), or the host
/// has no routes, that list goes into the host chain alone and guards everything the host answers.</item>
/// <item>Otherwise each route carries its own effective list (the host's or its override), and the host chain carries
/// their <b>union</b>, dropping denied requests only when every route does. So a client that no route admits is blocked
/// on every path, the HTTPS redirect and the 404 for paths no route takes included, and can't map the host; one that
/// some route admits is judged again by the route it hits. When some route is unrestricted, the host chain carries
/// nothing: anyone may reach that route, so there is nothing to hide.</item>
/// </list>
/// <para><b>Certificates.</b> Bindings resolve against the <see cref="CertificateInventory"/>. Auto → each DNS
/// name of the host gets the most specific valid certificate covering it (<see cref="CertificateSelection"/>), per name
/// (<see cref="HostSpec.NameCertificates"/>); names nothing covers get no certificate of their own (the default one
/// serves them) and are reported. Specific → that certificate when it is usable, else none of its own (reported). None →
/// <see cref="HostSpec.Https"/> off. The default certificate is the one the settings name when usable, else the
/// inventory's fallback. An unusable certificate never fails the mapping: it is a <see cref="MappedConfig.Warnings"/>
/// entry, and the command pipeline decides whether it blocks a change. Unknown host behaviour and the trusted proxies come
/// from the global settings; the default certificate's TLS settings are the global defaults' TLS.</para>
/// <para><b>Undefined values.</b> The enums the document and the proxy share are one type in <c>Paduraksa.Core.Model</c>,
/// so nothing is cast and the compiler rejects an undefined value. The model can't carry an undefined certificate binding
/// mode or load-balancing policy, nor a respond host without a content type, so the mapping reports those itself, as
/// <see cref="MappedConfig.Errors"/>. A null respond body is the empty body.</para>
/// </remarks>
public static class ProxyConfigMapping
{
    /// <summary>The compiler input for <paramref name="document"/>, labelled with <paramref name="revision"/>. Each access-list user
    /// gets the vault's handle on their password (<paramref name="credentials"/>); one the vault holds no password for, and that is not in
    /// <paramref name="pending"/>'s assumed set (about to be set by the command being compiled), is a warning; a credential in its overlay
    /// compiles against the overlay's handle (a draft's password that the vault doesn't hold).</summary>
    public static MappedConfig Map(
        ConfigDocument document, ConfigRevisionId revision, CertificateInventory inventory, IPasswordCredentials credentials, PendingPasswords pending)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(pending);
        var canonical = CanonicalConfigJson.Canonicalize(document);
        var settings = canonical.Settings;
        List<CompileError> errors = [];
        List<MappedWarning> warnings = [];
        // The built-in defaults with the global layer: what a host that sets nothing inherits, and what an unknown host gets.
        var globalDefaults = HostSettingsLayers.Resolve(settings.Defaults);

        // First one wins on a duplicate ID (invalid anyway).
        Dictionary<ProfileId, HostSettings> profiles = [];
        foreach (var profile in canonical.Profiles)
        {
            profiles.TryAdd(profile.Id, profile.Settings);
        }

        // First one wins on a duplicate ID (invalid anyway).
        Dictionary<AccessListId, AccessListConfig> accessLists = [];
        foreach (var list in canonical.AccessLists)
        {
            accessLists.TryAdd(list.Id, list);
        }

        var hosts = new HostSpec[canonical.Hosts.Count];
        for (var i = 0; i < hosts.Length; i++)
        {
            var host = canonical.Hosts[i];
            var profile = host.ProfileId is { } id ? profiles.GetValueOrDefault(id) : null;
            hosts[i] = MapHost(host, HostSettingsLayers.Resolve(settings.Defaults, profile, host.Settings), settings.TrustedProxies.Mode, inventory, accessLists, errors, warnings);
        }

        ProxyConfigModel model = new()
        {
            Revision = revision,
            Hosts = hosts,
            Upstreams = [.. canonical.Upstreams.Select(u => MapUpstream(u, errors, warnings))],
            RateLimitPolicies =
            [
                .. canonical.RateLimitPolicies
                    .Select(p => MapRateLimitPolicy(p, errors))
                    .Where(static p => p is not null)
                    .Select(static p => p!),
            ],
            AccessLists = [.. canonical.AccessLists.Select(l => MapAccessList(l, credentials, pending, errors, warnings))],
            Defaults = new DefaultsSpec
            {
                UnknownHost = (UnknownHostBehaviour)settings.UnknownHost,
                Certificate = DefaultCertificate(settings.DefaultCertificateId, inventory, warnings),
                Tls = globalDefaults.Tls,
                TrustedProxies = MapTrustedProxies(settings.TrustedProxies),
                // An unknown host has no layer of its own, so it follows the global default.
                AccessLog = globalDefaults.AccessLog,
                RequestIdPropagation = globalDefaults.RequestIdPropagation,
                ServerHeader = globalDefaults.ServerHeader,
            },
        };
        return new MappedConfig(model, errors, warnings);
    }

    /// <summary>The default certificate: the one the settings name when it is usable, else the inventory's fallback.</summary>
    private static CertificateId? DefaultCertificate(CertificateId? named, CertificateInventory inventory, List<MappedWarning> warnings)
    {
        if (named is not { } id)
        {
            return inventory.FallbackDefault;
        }

        // An empty ID is the compiler's to reject.
        if (id.IsEmpty || inventory.Resolver.TryResolve(id, out _))
        {
            return id;
        }

        warnings.Add(new MappedWarning(
            CompileErrorSource.Defaults,
            "/defaultCertificateId",
            ConfigWarningCodes.CertificateDefaultUnusable,
            $"the default certificate {id} can't be used: {Why(id, inventory)}"
            + (inventory.FallbackDefault is null ? "; unknown names are rejected" : "; the fallback default certificate serves instead")));
        return inventory.FallbackDefault;
    }

    /// <summary>Resolves a host's certificate binding against the inventory.</summary>
    private static (CertificateId? Certificate, IReadOnlyDictionary<string, CertificateId>? ByName) BindCertificate(
        HostConfig host, CertificateInventory inventory, List<MappedWarning> warnings)
    {
        var binding = host.Certificate;
        var where = CompileErrorSource.Host(host.Id);
        switch (binding.Mode)
        {
            case CertificateBindingMode.Specific:
                // An empty ID makes the compiler say a specific binding needs one.
                if (binding.CertificateId is not { IsEmpty: false } id)
                {
                    return (CertificateId.Empty, null);
                }

                // A disabled host serves nothing, so its certificate only has to be valid (it is never loaded).
                if (!inventory.TryGet(id, out var entry) || !(host.Enabled ? entry.IsUsable : entry.IsValid))
                {
                    warnings.Add(new MappedWarning(
                        where,
                        "/certificate",
                        ConfigWarningCodes.CertificateSpecificUnusable,
                        $"the certificate {id} can't be used: {Why(id, inventory)}; the host's names are served with the default certificate, if any"));
                    return (null, null);
                }

                if (!host.Enabled)
                {
                    return (null, null);
                }

                string[] uncovered = [.. CertificateSelection.ServerNames(host.Names).Where(n => CertificateSelection.Match(entry.Names, n) == NameMatch.None)];
                if (uncovered.Length > 0)
                {
                    warnings.Add(new MappedWarning(
                        where,
                        "/certificate",
                        ConfigWarningCodes.CertificateSpecificNoCoverage,
                        $"the certificate {id} doesn't cover {string.Join(", ", uncovered)}"));
                }

                return (id, null);
            case CertificateBindingMode.Auto when host.Enabled:
                Dictionary<string, CertificateId> byName = [];
                List<string> uncoveredNames = [];
                var usable = inventory.Certificates.Where(c => c.IsUsable);
                foreach (var name in CertificateSelection.ServerNames(host.Names))
                {
                    if (CertificateSelection.Best(usable, name) is { } best)
                    {
                        byName[name] = best.Id;
                    }
                    else
                    {
                        uncoveredNames.Add(name);
                    }
                }

                if (uncoveredNames.Count > 0)
                {
                    warnings.Add(new MappedWarning(
                        where,
                        string.Empty,
                        ConfigWarningCodes.CertificateAutoNoCoverage,
                        $"no valid certificate covers {string.Join(", ", uncoveredNames)}; they are served with the default certificate, if any"));
                }

                return (null, byName.Count == 0 ? null : byName);
            default:
                return (null, null);
        }
    }

    private static string Why(CertificateId id, CertificateInventory inventory) =>
        inventory.TryGet(id, out var entry) ? entry.UnusableReason ?? "it isn't loaded" : "the inventory has no such certificate";

    private static HostSpec MapHost(
        HostConfig host,
        EffectiveHostSettings effective,
        TrustedProxyMode proxyMode,
        CertificateInventory inventory,
        Dictionary<AccessListId, AccessListConfig> accessLists,
        List<CompileError> errors,
        List<MappedWarning> warnings)
    {
        var where = CompileErrorSource.Host(host.Id);
        if (host.Kind == HostKind.Admin && host.Settings.ForceHttps is false)
        {
            warnings.Add(new MappedWarning(
                where,
                "/settings/forceHttps",
                ConfigWarningCodes.AdminForceHttpsIgnored,
                "an admin host is always served over HTTPS and redirects plain HTTP; forceHttps: false is ignored"));
        }

        AddCloudflareWarnings(host, effective.Cloudflare, proxyMode, where, warnings);

        var (hostChain, routeChains) = AccessListChains(host, effective, accessLists);

        var routes = new RouteSpec[host.Routes.Count];
        for (var j = 0; j < routes.Length; j++)
        {
            var route = host.Routes[j];
            var rateLimit = HostSettingsLayers.ResolveRouteRateLimit(effective, route.Settings);
            routes[j] = new RouteSpec
            {
                Id = route.Id,
                Match = (RouteMatchKind)route.Match,
                Path = route.Path,
                Order = route.Order,
                Upstream = route.UpstreamId,
                Policies = routeChains[j],
                ProofOfWork = HostSettingsLayers.ResolveRouteProofOfWork(effective, route.Settings),
                ProofOfWorkRouteOverride = HostSettingsLayers.RouteOverridesProofOfWork(route.Settings),
                RateLimit = rateLimit.EffectivePolicyId,
                RateLimitRouteOverride = HostSettingsLayers.RouteOverridesRateLimit(route.Settings),
                AccessList = HostSettingsLayers.ResolveRouteAccessList(effective, route.Settings).Effective,
            };
        }

        // A password over plain HTTP travels in the clear. A host that forces the redirect never asks for one there (the
        // route chain runs after the redirect), so only the rest are worth a warning.
        if (host is { Enabled: true } && host.Kind != HostKind.Admin && !effective.ForceHttps
            && UsedLists(effective, host).Any(id => accessLists.TryGetValue(id, out var list) && list.BasicAuth is { Users.Count: > 0 }))
        {
            warnings.Add(new MappedWarning(
                where,
                string.Empty,
                ConfigWarningCodes.AccessListInsecureCredentials,
                "an access list with users guards this host, which also answers plain HTTP without forcing HTTPS, so a password could be sent in the clear; turn on forceHttps"));
        }

        // A plain-HTTP cookie cannot be Secure. A host that forces the redirect never serves a page over plain HTTP, so
        // only the rest are worth a warning.
        if (host is { Kind: HostKind.Proxy, Enabled: true } && !effective.ForceHttps
            && (effective.ProofOfWork.Enabled || routes.Any(static route => route.ProofOfWork.Enabled)))
        {
            warnings.Add(new MappedWarning(
                where,
                string.Empty,
                ConfigWarningCodes.ProofOfWorkInsecureCookie,
                "the proof-of-work gate is on, but this host answers plain HTTP without forcing HTTPS, so its cookie cannot be Secure; turn on forceHttps or serve the host over HTTPS only"));
        }

        var binding = host.Certificate;
        if (!Enum.IsDefined(binding.Mode))
        {
            errors.Add(new(where, $"unknown certificate binding mode {binding.Mode}"));
        }

        RespondSpec? respond = null;
        if (host.Respond is { } fixedAnswer)
        {
            if (string.IsNullOrWhiteSpace(fixedAnswer.ContentType))
            {
                errors.Add(new(where, "a respond host needs a content type"));
            }

            respond = new RespondSpec { StatusCode = fixedAnswer.StatusCode, Body = fixedAnswer.Body ?? string.Empty, ContentType = fixedAnswer.ContentType ?? string.Empty };
        }

        var (certificate, certificatesByName) = BindCertificate(host, inventory, warnings);
        return new HostSpec
        {
            Id = host.Id,
            Names = host.Names,
            Kind = (HostKind)host.Kind,
            Enabled = host.Enabled,
            // The admin is only ever reached over HTTPS: it forces the redirect whatever the layers say.
            ForceHttps = host.Kind == HostKind.Admin || effective.ForceHttps,
            Https = binding.Mode != CertificateBindingMode.None,
            Certificate = certificate,
            NameCertificates = certificatesByName,
            Tls = effective.Tls,
            Hsts = effective.Hsts,
            AccessLog = effective.AccessLog,
            RequestIdPropagation = effective.RequestIdPropagation,
            ServerHeader = effective.ServerHeader,
            ProofOfWork = effective.ProofOfWork,
            RateLimit = effective.RateLimit,
            AccessList = effective.AccessList,
            Robots = effective.Robots,
            Cloudflare = effective.Cloudflare,
            Routes = routes,
            Policies = hostChain,
            Redirect = host.Redirect is { } redirect
                ? new RedirectSpec { Target = redirect.Target, StatusCode = redirect.StatusCode, PreservePath = redirect.PreservePath }
                : null,
            Respond = respond,
        };
    }

    /// <summary>The two Cloudflare settings that look set but enforce nothing, or can only refuse: said at the host that
    /// would carry them, wherever the layer that set them is.</summary>
    private static void AddCloudflareWarnings(
        HostConfig host, HostCloudflareSpec cloudflare, TrustedProxyMode proxyMode, CompileErrorSource where, List<MappedWarning> warnings)
    {
        var lockdown = cloudflare.OriginLockdown != OriginLockdownMode.Off;
        if (!host.Enabled || !(lockdown || cloudflare.AuthenticatedOriginPulls))
        {
            return;
        }

        if (proxyMode is not (TrustedProxyMode.CloudflareProxy or TrustedProxyMode.CloudflareTunnel))
        {
            warnings.Add(new MappedWarning(
                where,
                "/settings/cloudflare",
                ConfigWarningCodes.CloudflareNeedsMode,
                "origin lockdown and Authenticated Origin Pulls do nothing while the trusted-proxy mode is neither Cloudflare proxy nor Cloudflare Tunnel"));
            return;
        }

        if (cloudflare.AuthenticatedOriginPulls && proxyMode == TrustedProxyMode.CloudflareTunnel)
        {
            warnings.Add(new MappedWarning(
                where,
                "/settings/cloudflare/authenticatedOriginPulls",
                ConfigWarningCodes.CloudflareAopTunnel,
                "Authenticated Origin Pulls doesn't apply through a Cloudflare Tunnel: cloudflared sends no client certificate, so the host refuses every request"));
        }

        if (cloudflare.AuthenticatedOriginPulls && host.Certificate.Mode == CertificateBindingMode.None)
        {
            warnings.Add(new MappedWarning(
                where,
                "/settings/cloudflare/authenticatedOriginPulls",
                ConfigWarningCodes.CloudflareAopNeedsHttps,
                "Authenticated Origin Pulls needs a certificate: without HTTPS the host can't see Cloudflare's client certificate, so it refuses every request"));
        }
    }

    /// <summary>
    /// The access-list policies of a host's chain and of each route's chain. A route's chain asks for the passwords (after
    /// the HTTPS redirect, so none is asked over plain HTTP); the host's chain only refuses by address, before the
    /// redirect and the fixed answers, so a blocked client gets 403 and never a redirect. A host without routes (a Redirect
    /// or Respond host) has nothing after its host chain, so it carries the whole list there.
    /// </summary>
    private static (PolicySpec[] Host, PolicySpec[][] Routes) AccessListChains(
        HostConfig host, EffectiveHostSettings effective, Dictionary<AccessListId, AccessListConfig> lists)
    {
        PolicySpec[][] routeChains = [.. host.Routes.Select(static _ => Array.Empty<PolicySpec>())];
        if (host.Routes.Count == 0)
        {
            // Nothing follows the host chain but the HTTPS redirect, which it would answer before a password is asked: so on
            // a host that redirects, the password waits for the HTTPS request.
            var redirects = host.Kind == HostKind.Admin || effective.ForceHttps;
            return (effective.AccessList.Effective is { } only ? [new AccessListPolicySpec(only, AccessListStage.Full, redirects)] : [], routeChains);
        }

        AccessListId?[] named = [.. host.Routes.Select(r => HostSettingsLayers.ResolveRouteAccessList(effective, r.Settings).Effective)];
        var uniform = named.All(id => id == named[0]);
        PolicySpec[] hostChain = [];
        if (uniform && named[0] is { } shared)
        {
            var list = lists.GetValueOrDefault(shared);
            if (list is null or { BasicAuth: null or { Users.Count: 0 } })
            {
                return ([new AccessListPolicySpec(shared)], routeChains);
            }

            hostChain = RefusesByAddress(list) ? [new AccessListPolicySpec(shared, AccessListStage.AddressOnly)] : [];
        }
        else if (!uniform && named.All(static id => id is not null))
        {
            AccessListId[] distinct = [.. named.Select(static id => id!.Value).Distinct()];
            hostChain = distinct.All(id => lists.TryGetValue(id, out var list) && RefusesByAddress(list))
                ? [new AccessListUnionPolicySpec(distinct)]
                : [];
        }

        for (var j = 0; j < routeChains.Length; j++)
        {
            routeChains[j] = named[j] is { } id ? [new AccessListPolicySpec(id)] : [];
        }

        return (hostChain, routeChains);
    }

    /// <summary>Whether the address rules alone can turn a request away before a route is known: a deny entry always, an
    /// allow list unless a password can still let the others in.</summary>
    private static bool RefusesByAddress(AccessListConfig list) =>
        list.Ip is { } ip
        && (ip.Deny.Count > 0 || (ip.Allow.Count > 0 && !(list.BasicAuth is { Users.Count: > 0 } && list.Satisfy == AccessListSatisfy.Any)));

    private static IEnumerable<AccessListId> UsedLists(EffectiveHostSettings effective, HostConfig host)
    {
        if (effective.AccessList.Effective is { } own)
        {
            yield return own;
        }

        foreach (var route in host.Routes)
        {
            if (HostSettingsLayers.ResolveRouteAccessList(effective, route.Settings).Effective is { } id)
            {
                yield return id;
            }
        }
    }

    private static AccessListSpec MapAccessList(
        AccessListConfig list, IPasswordCredentials credentials, PendingPasswords pending, List<CompileError> errors, List<MappedWarning> warnings)
    {
        List<AccessListUserSpec> users = [];
        var listed = list.BasicAuth?.Users ?? [];
        for (var i = 0; i < listed.Count; i++)
        {
            var user = listed[i];
            if (user.CredentialId is not { IsEmpty: false } id)
            {
                errors.Add(new CompileError(CompileErrorSource.AccessList(list.Id), $"the user '{user.Username}' has no password credential"));
                continue;
            }

            // The pointer names the user's own entry, so a reader of the warning needs nothing from its text; the list is canonical
            // here, as the command pipeline hands it over, so the position is the one the stored document has.
            var credential = pending.Overlay.TryGetValue(id, out var prepared) ? prepared : credentials.Open(id);
            if (!credential.IsSet && !pending.AssumedSet.Contains(id))
            {
                warnings.Add(new MappedWarning(
                    CompileErrorSource.AccessList(list.Id),
                    $"/basicAuth/users/{i}",
                    ConfigWarningCodes.AccessListUserNoPassword,
                    $"the user '{user.Username}' has no password in the vault, so they can't sign in until one is set"));
            }

            users.Add(new AccessListUserSpec(user.Username, credential));
        }

        return new()
        {
            Id = list.Id,
            Name = list.Name,
            Allow = list.Ip?.Allow ?? [],
            Deny = list.Ip?.Deny ?? [],
            DropDenied = list.Ip?.DropDenied ?? false,
            Satisfy = list.Satisfy,
            PassCredentialsUpstream = list.PassCredentialsUpstream,
            Realm = list.BasicAuth?.Realm ?? list.Name,
            Users = users,
        };
    }

    /// <summary>
    /// One rate-limit policy's numbers. The validator has already judged them; a value the engine would throw on is
    /// reported here as a mapping error rather than aborting the whole compile.
    /// </summary>
    private static RateLimitPolicySpec? MapRateLimitPolicy(RateLimitPolicyConfig policy, List<CompileError> errors)
    {
        double? seconds = policy.Per switch
        {
            RateLimitPeriod.Second => 1,
            RateLimitPeriod.Minute => 60,
            RateLimitPeriod.Hour => 3600,
            _ => null,
        };
        if (seconds is null)
        {
            errors.Add(new(CompileErrorSource.RateLimitPolicy(policy.Id), $"unknown rate-limit period {policy.Per}"));
            return null;
        }

        try
        {
            return new RateLimitPolicySpec
            {
                Id = policy.Id,
                Name = policy.Name,
                Parameters = new RateLimitParameters(policy.Requests / seconds.Value, policy.Burst),
                Key = policy.Key,
                Header = ConfigNormalization.CanonicalRateLimitHeader(policy.Header),
            };
        }
        catch (ArgumentOutOfRangeException ex)
        {
            errors.Add(new(CompileErrorSource.RateLimitPolicy(policy.Id), ex.Message));
            return null;
        }
    }

    private static UpstreamSpec MapUpstream(UpstreamConfig upstream, List<CompileError> errors, List<MappedWarning> warnings)
    {
        var trust = upstream.Tls?.Trust ?? UpstreamTlsTrust.System;
        if (trust == UpstreamTlsTrust.Any
            && upstream.Destinations.Any(d => d.Enabled && d.Address.StartsWith("https:", StringComparison.OrdinalIgnoreCase)))
        {
            warnings.Add(new MappedWarning(
                CompileErrorSource.Upstream(upstream.Id),
                "/tls/trust",
                ConfigWarningCodes.UpstreamTlsUnverified,
                $"upstream {upstream.Name} accepts any certificate: the traffic is encrypted but not authenticated, so pin the certificate's fingerprint instead if you can"));
        }

        string? loadBalancing = null;
        if (upstream.LoadBalancing is { } policy)
        {
            loadBalancing = YarpPolicyName(policy);
            if (loadBalancing is null)
            {
                errors.Add(new(CompileErrorSource.Upstream(upstream.Id), $"unknown load-balancing policy {policy}"));
            }
        }

        return new UpstreamSpec
        {
            Id = upstream.Id,
            Destinations = [.. upstream.Destinations.Where(d => d.Enabled).Select(d => new DestinationSpec(d.Id, d.Address))],
            LoadBalancing = loadBalancing,
            SessionAffinity = upstream.SessionAffinity,
            PassiveHealth = upstream.PassiveHealth is not null,
            ReactivationPeriod = upstream.PassiveHealth?.ReactivationPeriod,
            ActiveHealth = upstream.ActiveHealth is { } active
                ? new ActiveHealthSpec
                {
                    Path = active.Path,
                    Interval = active.Interval ?? ActiveHealthConfig.DefaultInterval,
                    Timeout = active.Timeout ?? ActiveHealthConfig.DefaultTimeout,
                }
                : null,
            MaxConnectionsPerServer = upstream.MaxConnectionsPerServer,
            TlsTrust = trust,
            PinnedSha256 = trust == UpstreamTlsTrust.Pinned
                ? [.. (upstream.Tls?.PinnedSha256 ?? []).Select(pin => Paduraksa.Proxy.Yarp.UpstreamCertificatePins.TryNormalize(pin, out var normal) ? normal : pin).Distinct(StringComparer.Ordinal)]
                : [],
        };
    }

    private static string? YarpPolicyName(LoadBalancingPolicy policy) => policy switch
    {
        LoadBalancingPolicy.PowerOfTwoChoices => LoadBalancingPolicies.PowerOfTwoChoices,
        LoadBalancingPolicy.RoundRobin => LoadBalancingPolicies.RoundRobin,
        LoadBalancingPolicy.Random => LoadBalancingPolicies.Random,
        LoadBalancingPolicy.LeastRequests => LoadBalancingPolicies.LeastRequests,
        LoadBalancingPolicy.FirstAlphabetical => LoadBalancingPolicies.FirstAlphabetical,
        _ => null,
    };

    private static TrustedProxySpec MapTrustedProxies(TrustedProxySettings trusted) => new()
    {
        Mode = (TrustedProxyMode)trusted.Mode,
        Proxies = trusted.Proxies,
        Header = trusted.Header,
        UseTrueClientIp = trusted.UseTrueClientIp,
        ForwardedForFallback = trusted.ForwardedForFallback,
        TrustForwardedProto = trusted.TrustForwardedProto,
    };
}

/// <summary>Outcome of <see cref="ProxyConfigMapping.Map"/>.</summary>
/// <param name="Model">The compiler input.</param>
/// <param name="Errors">Invalid values the model can't carry, attributed like compile errors; empty for a valid
/// document.</param>
/// <param name="Warnings">What maps fine but probably isn't intended (certificate coverage and usability).</param>
public sealed record MappedConfig(ProxyConfigModel Model, IReadOnlyList<CompileError> Errors, IReadOnlyList<MappedWarning> Warnings);

/// <summary>A certificate warning of <see cref="ProxyConfigMapping.Map"/>, attributed like a compile error.</summary>
/// <param name="Source">The object it is about.</param>
/// <param name="Member">A JSON pointer suffix below that object's path (<c>/certificate</c>); empty for the object.</param>
/// <param name="Code">A <see cref="ConfigWarningCodes"/> code.</param>
/// <param name="Message">The explanation.</param>
public sealed record MappedWarning(CompileErrorSource Source, string Member, string Code, string Message);

/// <summary>
/// What a compile should know about passwords the vault doesn't hold yet. <paramref name="AssumedSet"/>: credentials the command is about to
/// set (or import) before it commits, so a user on them is not reported as having no password. <paramref name="Overlay"/>: for a dry run or a
/// draft that will never commit, handles that carry the prepared, already-hashed password for that compile alone, which the mapping uses in
/// place of the vault's own handle, so the request simulator verifies a draft's passwords without anything reaching the vault or its cache.
/// </summary>
public sealed record PendingPasswords(IReadOnlySet<PasswordCredentialId> AssumedSet, IReadOnlyDictionary<PasswordCredentialId, IPasswordCredential> Overlay)
{
    public static PendingPasswords None { get; } = new(new HashSet<PasswordCredentialId>(), new Dictionary<PasswordCredentialId, IPasswordCredential>());
}
