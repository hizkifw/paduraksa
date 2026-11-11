using System.Buffers;

using Microsoft.AspNetCore.Http;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.AccessLists;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Pages;
using Paduraksa.Proxy.RuleSets;
using Paduraksa.Proxy.Yarp;

using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Health;

namespace Paduraksa.Proxy.Config;

/// <summary>Upstreams, policies and trusted proxies: everything a host's routes and the request's client address compile from.</summary>
public static partial class SnapshotCompiler
{
    private static Dictionary<UpstreamId, ClusterConfig> CompileUpstreams(
        IReadOnlyList<UpstreamSpec> upstreams,
        HashSet<UpstreamId> withoutDestinations,
        List<CompileError> errors)
    {
        // Insertion-ordered by construction.
        Dictionary<UpstreamId, ClusterConfig> clusters = [];
        Dictionary<string, UpstreamId> clusterIds = new(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in upstreams)
        {
            var where = CompileErrorSource.Upstream(spec.Id);
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

            var clusterId = spec.Id.ToString();
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
                // Only an error once an enabled host routes here (all destinations disabled for maintenance).
                withoutDestinations.Add(spec.Id);
            }

            foreach (var destination in spec.Destinations ?? [])
            {
                var destinationId = destination.Id.ToString();
                var destinationWhere = CompileErrorSource.Destination(spec.Id, destination.Id);
                if (destination.Id.IsEmpty)
                {
                    errors.Add(new(destinationWhere, "the destination ID is empty"));
                }
                else if (!Uri.TryCreate(destination.Address, UriKind.Absolute, out var address)
                    || (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps))
                {
                    errors.Add(new(destinationWhere, $"needs an absolute http or https address, got '{destination.Address}'"));
                }
                else if (!destinationIds.TryAdd(destinationId, destination.Id))
                {
                    var existing = destinationIds[destinationId];
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

            // Pinned trust with nothing pinned would refuse every certificate, which no one means.
            if (spec.TlsTrust == UpstreamTlsTrust.Pinned && !AreNormalisedPins(spec.PinnedSha256))
            {
                errors.Add(new(where, "pinned TLS trust needs at least one normalised SHA-256 fingerprint"));
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
                HttpClient = spec.MaxConnectionsPerServer is not null || spec.TlsTrust == UpstreamTlsTrust.Any
                    ? new HttpClientConfig
                    {
                        MaxConnectionsPerServer = spec.MaxConnectionsPerServer,
                        DangerousAcceptAnyServerCertificate = spec.TlsTrust == UpstreamTlsTrust.Any ? true : null,
                    }
                    : null,
                Metadata = spec.TlsTrust == UpstreamTlsTrust.Pinned
                    ? new Dictionary<string, string> { [UpstreamCertificatePins.MetadataKey] = string.Join(',', spec.PinnedSha256) }
                    : null,
            };
        }

        return clusters;
    }

    private static bool AreNormalisedPins(IReadOnlyList<string> pins)
    {
        if (pins.Count == 0)
        {
            return false;
        }

        foreach (var pin in pins)
        {
            if (!UpstreamCertificatePins.TryNormalize(pin, out var normal) || !string.Equals(normal, pin, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static IRequestPolicy[] CompilePolicies(IReadOnlyList<PolicySpec> specs, CompileErrorSource where, List<CompileError> errors, AccessListBinding accessLists, RuleSetBinding ruleSets)
    {
        var policies = new IRequestPolicy[specs.Count];
        for (var i = 0; i < specs.Count; i++)
        {
            policies[i] = CompilePolicy(specs[i], where, errors, accessLists, ruleSets);
        }

        return policies;
    }

    private static IRequestPolicy CompilePolicy(PolicySpec spec, CompileErrorSource where, List<CompileError> errors, AccessListBinding accessLists, RuleSetBinding ruleSets)
    {
        switch (spec)
        {
            case DenyPolicySpec deny when deny.StatusCode is < 200 or > 599:
                errors.Add(new(where, $"deny status {deny.StatusCode} must be 200-599"));
                break;
            case DenyPolicySpec deny when PrecomputedResponse.ForbidsBody(deny.StatusCode) && !string.IsNullOrEmpty(deny.Body):
                errors.Add(new(where, $"a {deny.StatusCode} deny response can't have a body"));
                break;
            case DenyPolicySpec deny:
                return new StaticPolicy(PolicyDecision.Respond(PrecomputedResponse.Text(deny.StatusCode, deny.Body ?? string.Empty)));
            case DropPolicySpec:
                return new StaticPolicy(PolicyDecision.Drop);
            case AccessListPolicySpec access when accessLists.Lists.TryGetValue(access.List, out var list):
                return new AccessListPolicy(list, access.Stage, accessLists.Runtime, access.DeferPasswordsOnPlainHttp);
            case AccessListPolicySpec access:
                errors.Add(new(where, $"access list '{access.List}' doesn't exist"));
                break;
            case AccessListUnionPolicySpec union:
                if (TryBindUnion(union, accessLists, out var lists))
                {
                    return new AccessListUnionPolicy(lists);
                }

                errors.Add(new(where, "a union of access lists names one that doesn't exist, or none"));
                break;
            case RuleSetPolicySpec ruleSet when ruleSets.Sets.TryGetValue(ruleSet.RuleSet, out var compiled):
                return new RuleSetPolicy(compiled, ruleSet.RouteOverride);
            case RuleSetPolicySpec ruleSet:
                errors.Add(new(where, $"rule set '{ruleSet.RuleSet}' doesn't exist or didn't compile"));
                break;
            default:
                errors.Add(new(where, $"unsupported policy {spec?.GetType().Name ?? "null"}"));
                break;
        }

        return new StaticPolicy(PolicyDecision.Continue);
    }

    private static TrustedProxyPolicy CompileTrustedProxies(
        TrustedProxySpec? spec, SnapshotCompilerOptions options, List<CompileError> errors, out EdgeRangeList? edgeRanges)
    {
        edgeRanges = null;
        spec ??= TrustedProxySpec.Off;
        var where = CompileErrorSource.TrustedProxies;
        var proxies = spec.Proxies ?? [];
        var cloudflare = spec.Mode is TrustedProxyMode.CloudflareProxy or TrustedProxyMode.CloudflareTunnel;
        if (spec.Header is not null && spec.Mode != TrustedProxyMode.Generic)
        {
            errors.Add(new(where, "a header is only configurable in Generic mode (Cloudflare modes read CF-Connecting-IP)"));
        }

        if (!cloudflare && spec.UseTrueClientIp)
        {
            errors.Add(new(where, "True-Client-IP only applies to the Cloudflare modes"));
        }

        if (!cloudflare && spec.ForwardedForFallback)
        {
            errors.Add(new(where, "the X-Forwarded-For fallback only applies to the Cloudflare modes"));
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

                var peers = CompileCidrs(proxies, where, errors);
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

                edgeRanges = options.CloudflareEdgeRanges is { } fixedSet
                    ? EdgeRangeList.FromSet(fixedSet)
                    : options.CloudflareRanges?.Current ?? EdgeRangeList.BuiltIn;
                return TrustedProxyPolicy.CloudflareProxy(edgeRanges.Ranges, spec.UseTrueClientIp, spec.ForwardedForFallback);

            default:
                errors.Add(new(where, $"unknown mode {spec.Mode}"));
                return TrustedProxyPolicy.Off;
        }
    }

    /// <summary>Addresses and CIDRs (both families) into one set; each bad entry is an error.</summary>
    private static CidrSet CompileCidrs(IReadOnlyList<string> blocks, CompileErrorSource where, List<CompileError> errors)
    {
        CidrSet.Builder builder = new();
        foreach (var block in blocks)
        {
            if (!IpNetwork.TryParse(block ?? string.Empty, out var network, out var error))
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
}
