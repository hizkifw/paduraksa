using System.Buffers;

using Microsoft.AspNetCore.Http;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Net;

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
                // Only an error once an enabled host routes here (all destinations disabled for maintenance).
                withoutDestinations.Add(spec.Id);
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
                errors.Add(new(where, $"deny status {deny.StatusCode} must be 200-599"));
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
}
