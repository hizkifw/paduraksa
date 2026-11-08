using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Config;

namespace Paduraksa.Dashboard.SettingsArea;

/// <summary>
/// One layer of host settings as a form edits it (PLAN §5.3): every field may be left to inherit (null). <see cref="ApplyTo"/> writes the
/// fields back over the stored layer, so a setting the form doesn't show is kept.
/// </summary>
public sealed class HostSettingsModel
{
    /// <summary>The built-in defaults under every layer (PLAN §6.4): no HTTPS redirect, TLS 1.2, HTTP/2 and HTTP/1.1, HTTP/3 on, no allow list.</summary>
    public static HostSettings BuiltIn { get; } = new()
    {
        ForceHttps = false,
        Tls = new TlsSettings { MinimumVersion = TlsMinimumVersion.Tls12, TcpAlpn = Contracts.Config.TcpAlpn.Http2AndHttp11, Http3 = true },
    };

    public bool? ForceHttps { get; set; }

    public TlsMinimumVersion? MinimumTls { get; set; }

    public TcpAlpn? TcpAlpn { get; set; }

    public bool? Http3 { get; set; }

    /// <summary>Whether this layer sets its own IP allow list (otherwise it inherits one).</summary>
    public bool AllowListSet { get; set; }

    /// <summary>The allow list's addresses or networks, one per line (or separated by commas).</summary>
    public string AllowList { get; set; } = string.Empty;

    public bool DropDenied { get; set; }

    /// <summary>How many fields this layer sets.</summary>
    public int Overrides =>
        (ForceHttps is null ? 0 : 1) + (MinimumTls is null ? 0 : 1) + (TcpAlpn is null ? 0 : 1) + (Http3 is null ? 0 : 1) + (AllowListSet ? 1 : 0);

    public static HostSettingsModel From(HostSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new HostSettingsModel
        {
            ForceHttps = settings.ForceHttps,
            MinimumTls = settings.Tls?.MinimumVersion,
            TcpAlpn = settings.Tls?.TcpAlpn,
            Http3 = settings.Tls?.Http3,
            AllowListSet = settings.IpAllowList is not null,
            AllowList = string.Join('\n', settings.IpAllowList?.Addresses ?? []),
            DropDenied = settings.IpAllowList?.DropDenied ?? false,
        };
    }

    /// <summary><paramref name="stored"/> with this form's fields in place of its own.</summary>
    public HostSettings ApplyTo(HostSettings stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        return stored with
        {
            ForceHttps = ForceHttps,
            Tls = MinimumTls is null && TcpAlpn is null && Http3 is null ? null : new TlsSettings { MinimumVersion = MinimumTls, TcpAlpn = TcpAlpn, Http3 = Http3 },
            IpAllowList = AllowListSet ? new IpAllowListSettings(HostEditModel.SplitList(AllowList), DropDenied) : null,
        };
    }

    /// <summary><paramref name="upper"/> resolved over <paramref name="lower"/>: what a host with no setting of its own ends up with.</summary>
    public static HostSettings Layer(HostSettings upper, HostSettings lower)
    {
        ArgumentNullException.ThrowIfNull(upper);
        ArgumentNullException.ThrowIfNull(lower);
        return new HostSettings
        {
            ForceHttps = upper.ForceHttps ?? lower.ForceHttps,
            Tls = new TlsSettings
            {
                MinimumVersion = upper.Tls?.MinimumVersion ?? lower.Tls?.MinimumVersion,
                TcpAlpn = upper.Tls?.TcpAlpn ?? lower.Tls?.TcpAlpn,
                Http3 = upper.Tls?.Http3 ?? lower.Tls?.Http3,
            },
            IpAllowList = upper.IpAllowList ?? lower.IpAllowList,
        };
    }

    /// <summary>What a layer sets, in short phrases for a card ("HTTPS redirect", "TLS 1.3 minimum", "Allow list: 2 entries").</summary>
    public static IReadOnlyList<string> Summary(HostSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        List<string> parts = [];
        if (settings.ForceHttps is { } force)
        {
            parts.Add(force ? "Redirects to HTTPS" : "No HTTPS redirect");
        }

        if (settings.Tls?.MinimumVersion is { } minimum)
        {
            parts.Add($"{TlsText(minimum)} minimum");
        }

        if (settings.Tls?.TcpAlpn is { } alpn)
        {
            parts.Add(AlpnText(alpn));
        }

        if (settings.Tls?.Http3 is { } http3)
        {
            parts.Add(http3 ? "HTTP/3 on" : "HTTP/3 off");
        }

        if (settings.IpAllowList is { } allow)
        {
            parts.Add(allow.Addresses.Count == 0 ? "No allow list" : $"Allow list: {allow.Addresses.Count} {(allow.Addresses.Count == 1 ? "entry" : "entries")}");
        }

        return parts;
    }

    public static string TlsText(TlsMinimumVersion version) => version == TlsMinimumVersion.Tls13 ? "TLS 1.3" : "TLS 1.2";

    public static string AlpnText(TcpAlpn alpn) => alpn == Contracts.Config.TcpAlpn.Http11Only ? "HTTP/1.1 only" : "HTTP/2 and HTTP/1.1";
}

/// <summary>
/// The global settings as the defaults form edits them (PLAN §5.1, §7.6): the bottom layer of host settings, what an unknown name gets,
/// the default certificate and who may tell us the client's address. Options that don't apply to the chosen trusted-proxy mode are
/// dropped when the settings are built, so switching modes never leaves a field the validator refuses.
/// </summary>
public sealed class GlobalSettingsEditModel
{
    private readonly GlobalSettings _original;

    private GlobalSettingsEditModel(GlobalSettings original)
    {
        _original = original;
        Defaults = HostSettingsModel.From(original.Defaults);
        UnknownHost = original.UnknownHost;
        DefaultCertificateId = original.DefaultCertificateId;
        ProxyMode = original.TrustedProxies.Mode;
        Proxies = string.Join('\n', original.TrustedProxies.Proxies);
        Header = original.TrustedProxies.Header;
        UseTrueClientIp = original.TrustedProxies.UseTrueClientIp;
        ForwardedForFallback = original.TrustedProxies.ForwardedForFallback;
        TrustForwardedProto = original.TrustedProxies.TrustForwardedProto;
    }

    /// <summary>The version the form opened with.</summary>
    public long Version => _original.Version;

    public HostSettingsModel Defaults { get; }

    public UnknownHostBehaviour UnknownHost { get; set; }

    public CertificateId? DefaultCertificateId { get; set; }

    public TrustedProxyMode ProxyMode { get; set; }

    /// <summary>The trusted proxies' addresses or networks, one per line.</summary>
    public string Proxies { get; set; }

    /// <summary>Generic mode: the header with the client's address; empty is <c>X-Forwarded-For</c>.</summary>
    public string? Header { get; set; }

    public bool UseTrueClientIp { get; set; }

    public bool ForwardedForFallback { get; set; }

    public bool TrustForwardedProto { get; set; }

    /// <summary>Whether the mode takes a list of proxies.</summary>
    public bool ModeTakesProxies => ProxyMode is TrustedProxyMode.Generic or TrustedProxyMode.CloudflareTunnel;

    public static GlobalSettingsEditModel From(GlobalSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new GlobalSettingsEditModel(settings);
    }

    /// <summary>The settings as the form describes them, with the version it opened with.</summary>
    public GlobalSettings ToSettings()
    {
        bool generic = ProxyMode == TrustedProxyMode.Generic;
        bool cloudflare = ProxyMode is TrustedProxyMode.CloudflareProxy or TrustedProxyMode.CloudflareTunnel;
        return _original with
        {
            Defaults = Defaults.ApplyTo(_original.Defaults),
            UnknownHost = UnknownHost,
            DefaultCertificateId = DefaultCertificateId,
            TrustedProxies = new TrustedProxySettings(
                ProxyMode,
                ModeTakesProxies ? HostEditModel.SplitList(Proxies) : [],
                generic && !string.IsNullOrWhiteSpace(Header) ? Header.Trim() : null,
                cloudflare && UseTrueClientIp,
                cloudflare && ForwardedForFallback,
                generic && TrustForwardedProto),
        };
    }
}

/// <summary>A profile as its form edits it: name, notes and its layer of host settings. A new profile gets a fresh typed ID.</summary>
public sealed class ProfileEditModel
{
    private readonly ProfileConfig? _original;

    private ProfileEditModel(ProfileConfig? original, ProfileId id)
    {
        _original = original;
        Id = id;
        Name = original?.Name ?? string.Empty;
        Notes = original?.Notes;
        Settings = HostSettingsModel.From(original?.Settings ?? new HostSettings());
    }

    public ProfileId Id { get; }

    public bool IsNew => _original is null;

    /// <summary>The version the form opened with (0 for a new profile).</summary>
    public long Version => _original?.Version ?? 0;

    public string Name { get; set; }

    public string? Notes { get; set; }

    public HostSettingsModel Settings { get; }

    public static ProfileEditModel New() => new(null, ProfileId.New());

    public static ProfileEditModel From(ProfileConfig profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new ProfileEditModel(profile, profile.Id);
    }

    public ProfileConfig ToConfig() => new(
        Id,
        Name.Trim(),
        Version,
        Settings.ApplyTo(_original?.Settings ?? new HostSettings()),
        string.IsNullOrWhiteSpace(Notes) ? null : Notes);

    /// <summary>The hosts that name <paramref name="profile"/>.</summary>
    public static IReadOnlyList<HostConfig> HostsUsing(ConfigDocument document, ProfileId profile)
    {
        ArgumentNullException.ThrowIfNull(document);
        return [.. document.Hosts.Where(h => h.ProfileId == profile).OrderBy(ChangeView.HostTitle, StringComparer.OrdinalIgnoreCase)];
    }
}
