using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Logging;
using Paduraksa.Proxy.Net;
using Yarp.ReverseProxy.Transforms;

namespace Paduraksa.Proxy.Yarp;

/// <summary>
/// The forwarding headers an upstream receives. YARP's own <c>X-Forwarded-*</c> transforms are
/// disabled; this replaces them. Every value comes from the gate's resolution of the request, never from what the
/// client sent, and is <b>overwritten, never appended to</b>:
/// <list type="bullet">
/// <item><c>X-Forwarded-For</c> and <c>X-Real-IP</c>: exactly one address, the resolved client
/// (<see cref="GateRequestState.ClientAddress"/>: the PROXY protocol source or socket peer, or, behind a trusted proxy,
/// the address its header carried). IPv4 as dotted quad (also for IPv4-mapped peers), IPv6 without brackets. Left out
/// when the address is unknown (a non-IP transport).</item>
/// <item><c>X-Forwarded-Proto</c>: <c>https</c> or <c>http</c>, the request's trusted scheme
/// (<see cref="GateRequestState.IsHttps"/>, the same one the HTTPS redirect uses): from a trusted Cloudflare peer its
/// <c>CF-Visitor</c>, else its <c>X-Forwarded-Proto</c>; from a trusted generic proxy its <c>X-Forwarded-Proto</c> only
/// with <c>TrustForwardedProto</c>; otherwise the scheme the client used to reach Paduraksa.</item>
/// <item><c>X-Forwarded-Host</c>: the request's <c>Host</c> (H2/H3 <c>:authority</c>) as received, port included.</item>
/// <item><c>X-Request-Id</c>: the request's ID when the host sends one upstream
/// (<see cref="Snapshot.CompiledHost.RequestIdPropagation"/>): always the gate's own, never the client's. Otherwise
/// whatever the client sent is removed.</item>
/// <item>Removed and not replaced: <c>Forwarded</c>, <c>X-Forwarded-Prefix</c>, <c>X-Forwarded-Port</c>, which only a
/// client could have set.</item>
/// <item>Cloudflare's headers (every <c>cf-*</c> header plus <c>True-Client-IP</c>,
/// <see cref="CloudflareHeaders.IsStrippedWhenUntrusted"/>) pass through only when
/// <see cref="ClientIpResolution.CloudflareHeadersTrusted"/> (a Cloudflare mode and a trusted peer); otherwise they're
/// removed, so a client can't spoof them (e.g. forge Cloudflare Access's identity headers past a tunnel).</item>
/// </list>
/// Cost per request: the header-store entries YARP's request needs anyway; the client address string is cached per
/// connection/stream (<see cref="GateRequestState.FormattedClientAddress"/>), the other values are shared constants or the
/// strings Kestrel already holds.
/// </summary>
internal static class ForwardedHeadersTransform
{
    private const string Http = "http";
    private const string Https = "https";

    /// <summary>Client-supplied forwarding headers that are dropped without a replacement.</summary>
    private static readonly string[] Dropped = [ForwardingHeaders.Forwarded, ForwardingHeaders.XForwardedPrefix, ForwardingHeaders.XForwardedPort];

    public static ValueTask ApplyAsync(RequestTransformContext context)
    {
        var upstream = context.ProxyRequest.Headers;
        var request = context.HttpContext.Request;
        var state = context.HttpContext.Features.Get<IGateFeature>()?.State;

        var proto = (state?.IsHttps ?? request.IsHttps) ? Https : Http;

        // The proxy's own credentials, consumed by an access list: the upstream has no use for them.
        if (state is { StripAuthorization: true })
        {
            upstream.Remove(Microsoft.Net.Http.Headers.HeaderNames.Authorization);
        }

        // The identity an auth provider vouches for: whatever the client sent under those names is removed first, on every route of a host that has a
        // provider (also one with sign-in off, which no provider vouches for), so an identity header can only ever come from the provider.
        if (state?.Host is { IdentityProviders: { Length: > 0 } providers })
        {
            ApplyForwardAuth(upstream, providers, state.ForwardAuthHeaders);
        }
        else if (state is { ForwardAuthProvider: { } answered })
        {
            ApplyForwardAuth(upstream, [answered], state.ForwardAuthHeaders);
        }

        upstream.Remove(ForwardingHeaders.XForwardedFor);
        upstream.Remove(ForwardingHeaders.XRealIp);
        upstream.Remove(ForwardingHeaders.XForwardedProto);
        upstream.Remove(ForwardingHeaders.XForwardedHost);
        foreach (var name in Dropped)
        {
            upstream.Remove(name);
        }

        if (state is null || !state.ClientIp.CloudflareHeadersTrusted)
        {
            // Usually there is none: one scan of the names (struct enumerators, 0 B). Each removal restarts the scan,
            // as the collection can't change under its enumerator.
            while (FindCloudflareHeader(upstream) is { } name)
            {
                upstream.Remove(name);
            }
        }

        if (state is not null && state.ClientAddress != IpKey.Unspecified)
        {
            var client = state.FormattedClientAddress();
            upstream.TryAddWithoutValidation(ForwardingHeaders.XForwardedFor, client);
            upstream.TryAddWithoutValidation(ForwardingHeaders.XRealIp, client);
        }

        upstream.TryAddWithoutValidation(ForwardingHeaders.XForwardedProto, proto);
        var host = request.Headers.Host;
        if (host.Count == 1 && !string.IsNullOrEmpty(host[0]))
        {
            upstream.TryAddWithoutValidation(ForwardingHeaders.XForwardedHost, host[0]);
        }

        // Always ours when the host sends one upstream (never the client's), and removed entirely when it doesn't: the
        // setting means what it says, also for a value a trusted proxy supplied.
        upstream.Remove(RequestIds.HeaderName);
        if (state?.Snapshot is { } snapshot && snapshot.RequestIdPropagationFor(state.Host).SendsUpstream())
        {
            upstream.TryAddWithoutValidation(RequestIds.HeaderName, state.RequestIdText());
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Removes the client's copies of the headers <paramref name="providers"/> own (exact names and prefixes, <c>_</c> and <c>-</c> alike) and
    /// adds the ones the provider that answered returned.</summary>
    internal static void ApplyForwardAuth(HttpRequestHeaders upstream, ForwardAuth.CompiledAuthProvider[] providers, ForwardAuth.ForwardAuthHeader[]? answered)
    {
        // Usually there is none: one scan of the names (struct enumerators, 0 B). Each removal restarts the scan, as the collection can't change
        // under its enumerator.
        while (FindOwned(upstream, providers) is { } name)
        {
            upstream.Remove(name);
        }

        if (answered is null)
        {
            return;
        }

        foreach (var header in answered)
        {
            upstream.TryAddWithoutValidation(header.Name, header.Value);
        }
    }

    private static string? FindOwned(HttpRequestHeaders headers, ForwardAuth.CompiledAuthProvider[] providers)
    {
        foreach (var header in headers.NonValidated)
        {
            foreach (var provider in providers)
            {
                if (provider.Owns(header.Key))
                {
                    return header.Key;
                }
            }
        }

        return null;
    }

    /// <summary>The first header <see cref="CloudflareHeaders.IsStrippedWhenUntrusted"/> applies to, or null. 0 B.</summary>
    internal static string? FindCloudflareHeader(HttpRequestHeaders headers)
    {
        foreach (var header in headers.NonValidated)
        {
            if (CloudflareHeaders.IsStrippedWhenUntrusted(header.Key))
            {
                return header.Key;
            }
        }

        return null;
    }

}
