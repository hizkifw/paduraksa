using Paduraksa.Contracts.Config;
using Paduraksa.Proxy.Config;

using ProxyTls = Paduraksa.Proxy.Tls;

namespace Paduraksa.Application.Compilation;

/// <summary>
/// Resolves the settings layers (PLAN §5.3, §6.4): built-in defaults → global defaults → profile → host, then the
/// route layer on top of the host's result. A null field inherits; TLS and HSTS merge field by field; the IP allow list
/// replaces as a whole, and an empty one means no restriction (that's how a layer lifts an inherited list).
/// </summary>
internal static class HostSettingsLayers
{
    /// <summary>The effective host settings of <paramref name="layers"/>, listed bottom (global defaults) to top (the
    /// host); null layers are skipped.</summary>
    public static EffectiveHostSettings Resolve(params ReadOnlySpan<HostSettings?> layers)
    {
        bool forceHttps = false;
        HostTlsSpec tls = HostTlsSpec.Default;
        HostHstsSpec hsts = HostHstsSpec.Off;
        IpAllowListSettings? allowList = null;
        foreach (HostSettings? layer in layers)
        {
            if (layer is null)
            {
                continue;
            }

            forceHttps = layer.ForceHttps ?? forceHttps;
            tls = Merge(tls, layer.Tls);
            hsts = Merge(hsts, layer.Hsts);
            allowList = layer.IpAllowList ?? allowList;
        }

        return new EffectiveHostSettings(forceHttps, tls, hsts, Restriction(allowList));
    }

    /// <summary>The effective allow list of a route: its own when set, else the host's.</summary>
    public static IpAllowListSettings? ResolveRoute(EffectiveHostSettings host, RouteSettings? route) =>
        route?.IpAllowList is { } own ? Restriction(own) : host.IpAllowList;

    /// <summary><paramref name="tls"/> with the fields <paramref name="layer"/> sets.</summary>
    public static HostTlsSpec Merge(HostTlsSpec tls, TlsSettings? layer) => layer is null
        ? tls
        : tls with
        {
            MinimumVersion = layer.MinimumVersion is { } minimum ? (ProxyTls.TlsMinimumVersion)minimum : tls.MinimumVersion,
            TcpAlpn = layer.TcpAlpn is { } alpn ? (ProxyTls.TcpAlpn)alpn : tls.TcpAlpn,
            Http3 = layer.Http3 ?? tls.Http3,
        };

    /// <summary><paramref name="hsts"/> with the fields <paramref name="layer"/> sets.</summary>
    public static HostHstsSpec Merge(HostHstsSpec hsts, HstsSettings? layer) => layer is null
        ? hsts
        : hsts with
        {
            Enabled = layer.Enabled ?? hsts.Enabled,
            MaxAgeSeconds = layer.MaxAge ?? hsts.MaxAgeSeconds,
            IncludeSubDomains = layer.IncludeSubDomains ?? hsts.IncludeSubDomains,
            Preload = layer.Preload ?? hsts.Preload,
        };

    /// <summary>Whether two effective allow lists restrict the same way (both null, or the same addresses in the same
    /// order and the same denial).</summary>
    public static bool SameRestriction(IpAllowListSettings? a, IpAllowListSettings? b) =>
        (a, b) switch
        {
            (null, null) => true,
            ({ } x, { } y) => x.DropDenied == y.DropDenied && x.Addresses.SequenceEqual(y.Addresses, StringComparer.Ordinal),
            _ => false,
        };

    // An allow list without addresses restricts nothing.
    private static IpAllowListSettings? Restriction(IpAllowListSettings? allowList) =>
        allowList is { Addresses.Count: > 0 } ? allowList : null;
}

/// <summary>A host's settings with every layer resolved.</summary>
/// <param name="ForceHttps">Redirect plain HTTP to HTTPS.</param>
/// <param name="Tls">Concrete TLS settings.</param>
/// <param name="Hsts">Concrete HSTS settings.</param>
/// <param name="IpAllowList">The allow list in force, or null for no restriction (never an empty list).</param>
internal sealed record EffectiveHostSettings(bool ForceHttps, HostTlsSpec Tls, HostHstsSpec Hsts, IpAllowListSettings? IpAllowList);
