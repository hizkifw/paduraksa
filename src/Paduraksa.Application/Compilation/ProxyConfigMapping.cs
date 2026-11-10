using Paduraksa.Application.Certificates;
using Paduraksa.Application.Config;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;

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
    /// <summary>The compiler input for <paramref name="document"/>, labelled with <paramref name="revision"/>.</summary>
    public static MappedConfig Map(ConfigDocument document, ConfigRevisionId revision, CertificateInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(inventory);
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

        var hosts = new HostSpec[canonical.Hosts.Count];
        for (var i = 0; i < hosts.Length; i++)
        {
            var host = canonical.Hosts[i];
            var profile = host.ProfileId is { } id ? profiles.GetValueOrDefault(id) : null;
            hosts[i] = MapHost(host, HostSettingsLayers.Resolve(settings.Defaults, profile, host.Settings), inventory, errors, warnings);
        }

        ProxyConfigModel model = new()
        {
            Revision = revision,
            Hosts = hosts,
            Upstreams = [.. canonical.Upstreams.Select(u => MapUpstream(u, errors))],
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
        HostConfig host, EffectiveHostSettings effective, CertificateInventory inventory, List<CompileError> errors, List<MappedWarning> warnings)
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

        IpAllowListSettings?[] routeLists = [.. host.Routes.Select(r => HostSettingsLayers.ResolveRoute(effective, r.Settings))];
        var uniform = routeLists.All(list => HostSettingsLayers.SameRestriction(list, routeLists[0]));
        var hostChain = routeLists.Length == 0 ? AllowListPolicy(effective.IpAllowList)
            : uniform ? AllowListPolicy(routeLists[0])
            : AllowListPolicy(Union(routeLists));

        var routes = new RouteSpec[host.Routes.Count];
        for (var j = 0; j < routes.Length; j++)
        {
            var route = host.Routes[j];
            routes[j] = new RouteSpec
            {
                Id = route.Id,
                Match = (RouteMatchKind)route.Match,
                Path = route.Path,
                Order = route.Order,
                Upstream = route.UpstreamId,
                Policies = uniform ? [] : AllowListPolicy(routeLists[j]),
            };
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
            Routes = routes,
            Policies = hostChain,
            Redirect = host.Redirect is { } redirect
                ? new RedirectSpec { Target = redirect.Target, StatusCode = redirect.StatusCode, PreservePath = redirect.PreservePath }
                : null,
            Respond = respond,
        };
    }

    /// <summary>The union of route lists, or null when one of them restricts nothing. Denied requests are dropped only
    /// when every list drops them.</summary>
    private static IpAllowListSettings? Union(IpAllowListSettings?[] lists)
    {
        if (lists.Any(list => list is null))
        {
            return null;
        }

        return new IpAllowListSettings(
            [.. lists.SelectMany(list => list!.Addresses).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            lists.All(list => list!.DropDenied));
    }

    private static PolicySpec[] AllowListPolicy(IpAllowListSettings? allowList) =>
        allowList is null ? [] : [new IpAllowListPolicySpec(allowList.Addresses, allowList.DropDenied)];

    private static UpstreamSpec MapUpstream(UpstreamConfig upstream, List<CompileError> errors)
    {
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
