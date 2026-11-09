using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Tls;

namespace Paduraksa.Application.Compilation;

/// <summary>
/// The settings layers (PLAN §5.3, §6.4) merged in one place: built-in defaults → global defaults → profile → host, then
/// the route layer on top of the host's result. <c>Merge</c> lays one layer over another and keeps the result a
/// <i>layer</i> (a field neither layer sets stays unset); <see cref="Resolve"/> puts <see cref="BuiltIn"/> underneath and
/// turns the result into the concrete values the compiler, the validator and the dashboard work with.
/// </summary>
/// <remarks>
/// <para><b>Adding a setting</b> is three places, all here or beside the contract: the property on <see cref="HostSettings"/>,
/// its one line in <c>Merge</c> (and in the nested record's own merge), and its value in <see cref="Concrete"/>. The built-in default is a field of <see cref="BuiltIn"/>; nothing
/// else may state one.</para>
/// <para><b>The rules.</b> A null field inherits: keeping the result nullable is what lets a merged layer be a layer in
/// its turn. Nested settings (<see cref="HostSettings.Tls"/>, <see cref="HostSettings.Hsts"/>,
/// <see cref="HostSettings.ServerHeader"/>) merge field by field and stay absent when neither layer sets a field of
/// theirs. The IP allow list replaces as a whole, and an empty one means no restriction (that is how a layer lifts an
/// inherited list). Everything else is a plain override.</para>
/// </remarks>
public static class HostSettingsLayers
{
    /// <summary>
    /// The built-in defaults under every layer: no HTTPS redirect, TLS 1.2, HTTP/2 and HTTP/1.1, HTTP/3 on, no allow
    /// list, HSTS off with a year of <c>max-age</c> waiting for whoever turns it on, the access log on, the request ID
    /// sent upstream and to the client, and the <c>Paduraksa</c> server name with no version.
    /// </summary>
    public static HostSettings BuiltIn { get; } = new()
    {
        ForceHttps = false,
        Tls = new TlsSettings { MinimumVersion = TlsMinimumVersion.Tls12, TcpAlpn = TcpAlpn.Http2AndHttp11, Http3 = true },
        IpAllowList = null,
        Hsts = new HstsSettings { Enabled = false, MaxAge = HstsHeader.DefaultMaxAgeSeconds, IncludeSubDomains = false, Preload = false },
        AccessLog = true,
        RequestIdPropagation = RequestIdPropagation.UpstreamAndResponse,
        ServerHeader = new ServerHeaderSettings { Mode = ServerHeaderMode.Paduraksa },
    };

    /// <summary>
    /// <paramref name="upper"/> laid over <paramref name="lower"/>: every field the upper layer sets wins, and one it
    /// leaves unset keeps the lower layer's. Null-preserving, so the result is a layer too.
    /// </summary>
    public static HostSettings Merge(HostSettings? lower, HostSettings? upper)
    {
        if (lower is null)
        {
            return upper ?? new HostSettings();
        }

        if (upper is null)
        {
            return lower;
        }

        return new HostSettings
        {
            ForceHttps = upper.ForceHttps ?? lower.ForceHttps,
            Tls = Merge(lower.Tls, upper.Tls),
            IpAllowList = upper.IpAllowList ?? lower.IpAllowList,
            Hsts = Merge(lower.Hsts, upper.Hsts),
            AccessLog = upper.AccessLog ?? lower.AccessLog,
            RequestIdPropagation = upper.RequestIdPropagation ?? lower.RequestIdPropagation,
            ServerHeader = Merge(lower.ServerHeader, upper.ServerHeader),
        };
    }

    /// <summary>The layers listed bottom to top, merged into one layer.</summary>
    public static HostSettings MergeAll(params ReadOnlySpan<HostSettings?> layers)
    {
        HostSettings merged = new();
        foreach (HostSettings? layer in layers)
        {
            merged = Merge(merged, layer);
        }

        return merged;
    }

    /// <summary><paramref name="lower"/> with the fields <paramref name="upper"/> sets; absent when neither sets one.</summary>
    public static TlsSettings? Merge(TlsSettings? lower, TlsSettings? upper)
    {
        if (upper is null)
        {
            return lower;
        }

        TlsSettings merged = new()
        {
            MinimumVersion = upper.MinimumVersion ?? lower?.MinimumVersion,
            TcpAlpn = upper.TcpAlpn ?? lower?.TcpAlpn,
            Http3 = upper.Http3 ?? lower?.Http3,
        };

        return merged is { MinimumVersion: null, TcpAlpn: null, Http3: null } ? null : merged;
    }

    /// <summary><paramref name="lower"/> with the fields <paramref name="upper"/> sets; absent when neither sets one.</summary>
    public static HstsSettings? Merge(HstsSettings? lower, HstsSettings? upper)
    {
        if (upper is null)
        {
            return lower;
        }

        HstsSettings merged = new()
        {
            Enabled = upper.Enabled ?? lower?.Enabled,
            MaxAge = upper.MaxAge ?? lower?.MaxAge,
            IncludeSubDomains = upper.IncludeSubDomains ?? lower?.IncludeSubDomains,
            Preload = upper.Preload ?? lower?.Preload,
        };

        return merged is { Enabled: null, MaxAge: null, IncludeSubDomains: null, Preload: null } ? null : merged;
    }

    /// <summary><paramref name="lower"/> with the fields <paramref name="upper"/> sets; absent when neither sets
    /// one.</summary>
    public static ServerHeaderSettings? Merge(ServerHeaderSettings? lower, ServerHeaderSettings? upper)
    {
        if (upper is null)
        {
            return lower;
        }

        ServerHeaderSettings merged = new()
        {
            Mode = upper.Mode ?? lower?.Mode,
            Value = upper.Value ?? lower?.Value,
        };

        return merged is { Mode: null, Value: null } ? null : merged;
    }

    /// <summary>The effective host settings of <paramref name="layers"/>, listed bottom (global defaults) to top (the
    /// host); null layers are skipped, and the built-in defaults sit underneath them all.</summary>
    internal static EffectiveHostSettings Resolve(params ReadOnlySpan<HostSettings?> layers) => Concrete(MergeAll(layers));

    /// <summary>The effective allow list of a route: its own when set, else the host's.</summary>
    internal static IpAllowListSettings? ResolveRoute(EffectiveHostSettings host, RouteSettings? route) =>
        route?.IpAllowList is { } own ? Restriction(own) : host.IpAllowList;

    /// <summary>Whether two effective allow lists restrict the same way (both null, or the same addresses in the same
    /// order and the same denial).</summary>
    internal static bool SameRestriction(IpAllowListSettings? a, IpAllowListSettings? b) =>
        (a, b) switch
        {
            (null, null) => true,
            ({ } x, { } y) => x.DropDenied == y.DropDenied && x.Addresses.SequenceEqual(y.Addresses, StringComparer.Ordinal),
            _ => false,
        };

    /// <summary>
    /// Which of a host's layers set a field: the one to go and change when a message has to name it, or
    /// <see cref="SettingsLayer.BuiltIn"/> when nothing set it.
    /// </summary>
    internal static SettingsLayer LayerOf(HostSettings? defaults, HostSettings? profile, HostSettings? host, Func<HostSettings, object?> field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (host is not null && field(host) is not null)
        {
            return SettingsLayer.Host;
        }

        if (profile is not null && field(profile) is not null)
        {
            return SettingsLayer.Profile;
        }

        return defaults is not null && field(defaults) is not null ? SettingsLayer.GlobalDefaults : SettingsLayer.BuiltIn;
    }

    /// <summary>
    /// What a message says after a rule's sentence when the layer that set the field isn't the host's own: that is the
    /// layer to go and change. Empty for a field the host sets itself, and for a built-in default (there is nowhere to
    /// go).
    /// </summary>
    internal static string InheritedSuffix(SettingsLayer layer) => layer switch
    {
        SettingsLayer.Profile => " (inherited from its profile)",
        SettingsLayer.GlobalDefaults => " (inherited from the global defaults)",
        _ => string.Empty,
    };

    // The concrete values: the built-in defaults, then the merged layer over them.
    private static EffectiveHostSettings Concrete(HostSettings settings)
    {
        TlsSettings tls = settings.Tls ?? new TlsSettings();
        HstsSettings hsts = settings.Hsts ?? new HstsSettings();
        ServerHeaderSettings server = settings.ServerHeader ?? new ServerHeaderSettings();
        TlsSettings builtInTls = BuiltIn.Tls!;
        HstsSettings builtInHsts = BuiltIn.Hsts!;
        ServerHeaderSettings builtInServer = BuiltIn.ServerHeader!;
        return new EffectiveHostSettings(
            settings.ForceHttps ?? BuiltIn.ForceHttps!.Value,
            new HostTlsSpec
            {
                MinimumVersion = tls.MinimumVersion ?? builtInTls.MinimumVersion!.Value,
                TcpAlpn = tls.TcpAlpn ?? builtInTls.TcpAlpn!.Value,
                Http3 = tls.Http3 ?? builtInTls.Http3!.Value,
            },
            Restriction(settings.IpAllowList),
            new HostHstsSpec
            {
                Enabled = hsts.Enabled ?? builtInHsts.Enabled!.Value,
                MaxAgeSeconds = hsts.MaxAge ?? builtInHsts.MaxAge!.Value,
                IncludeSubDomains = hsts.IncludeSubDomains ?? builtInHsts.IncludeSubDomains!.Value,
                Preload = hsts.Preload ?? builtInHsts.Preload!.Value,
            },
            settings.AccessLog ?? BuiltIn.AccessLog!.Value,
            settings.RequestIdPropagation ?? BuiltIn.RequestIdPropagation!.Value,
            new HostServerHeaderSpec
            {
                Mode = server.Mode ?? builtInServer.Mode!.Value,
                Value = server.Value ?? builtInServer.Value,
            });
    }

    // An allow list without addresses restricts nothing.
    private static IpAllowListSettings? Restriction(IpAllowListSettings? allowList) =>
        allowList is { Addresses.Count: > 0 } ? allowList : null;
}

/// <summary>Which layer of a host's chain set a field (PLAN §5.3), when a message has to name it.</summary>
public enum SettingsLayer
{
    /// <summary>Nothing set it, so the built-in default (<see cref="HostSettingsLayers.BuiltIn"/>) holds.</summary>
    BuiltIn,

    /// <summary>The global defaults.</summary>
    GlobalDefaults,

    /// <summary>The host's profile.</summary>
    Profile,

    /// <summary>The host itself.</summary>
    Host,
}

/// <summary>A host's settings with every layer resolved, in <see cref="HostSettings"/>'s field order.</summary>
/// <param name="ForceHttps">Redirect plain HTTP to HTTPS.</param>
/// <param name="Tls">Concrete TLS settings.</param>
/// <param name="IpAllowList">The allow list in force, or null for no restriction (never an empty list).</param>
/// <param name="Hsts">Concrete HSTS settings.</param>
/// <param name="AccessLog">Write a line per request to the access log (built-in default: on).</param>
/// <param name="RequestIdPropagation">Where the request ID is sent (built-in default: upstream and response).</param>
/// <param name="ServerHeader">Concrete <c>Server</c> header settings.</param>
internal sealed record EffectiveHostSettings(
    bool ForceHttps,
    HostTlsSpec Tls,
    IpAllowListSettings? IpAllowList,
    HostHstsSpec Hsts,
    bool AccessLog,
    RequestIdPropagation RequestIdPropagation,
    HostServerHeaderSpec ServerHeader);
