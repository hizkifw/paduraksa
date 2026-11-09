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
    /// <summary>The built-in defaults under every layer (PLAN §6.4): no HTTPS redirect, TLS 1.2, HTTP/2 and HTTP/1.1,
    /// HTTP/3 on, no allow list, HSTS off with a year of max-age waiting for whoever turns it on, the access log on, and
    /// the request ID sent upstream and to the client.</summary>
    public static HostSettings BuiltIn { get; } = new()
    {
        ForceHttps = false,
        Tls = new TlsSettings { MinimumVersion = TlsMinimumVersion.Tls12, TcpAlpn = Contracts.Config.TcpAlpn.Http2AndHttp11, Http3 = true },
        Hsts = new HstsSettings { Enabled = false, MaxAge = HstsMaxAgePresets.OneYear, IncludeSubDomains = false, Preload = false },
        AccessLog = true,
        RequestIdPropagation = Contracts.Config.RequestIdPropagation.UpstreamAndResponse,
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

    public bool? HstsEnabled { get; set; }

    /// <summary>The common <c>max-age</c> picked from the list, or <see cref="HstsMaxAgeChoice.Custom"/> for
    /// <see cref="HstsCustomMaxAge"/>.</summary>
    public HstsMaxAgeChoice HstsMaxAge { get; set; }

    /// <summary>The <c>max-age</c> in seconds when <see cref="HstsMaxAgeChoice.Custom"/> is picked.</summary>
    public int? HstsCustomMaxAge { get; set; }

    public bool? HstsIncludeSubDomains { get; set; }

    public bool? HstsPreload { get; set; }

    /// <summary>Whether this host's requests get an access-log line; null inherits.</summary>
    public bool? AccessLog { get; set; }

    /// <summary>Where the request ID is sent; null inherits.</summary>
    public RequestIdPropagation? RequestIdPropagation { get; set; }

    /// <summary>How many fields this layer sets.</summary>
    public int Overrides =>
        (ForceHttps is null ? 0 : 1) + (MinimumTls is null ? 0 : 1) + (TcpAlpn is null ? 0 : 1) + (Http3 is null ? 0 : 1) + (AllowListSet ? 1 : 0)
        + (HstsEnabled is null ? 0 : 1) + (HstsMaxAge == HstsMaxAgeChoice.Inherit ? 0 : 1) + (HstsIncludeSubDomains is null ? 0 : 1)
        + (HstsPreload is null ? 0 : 1) + (AccessLog is null ? 0 : 1) + (RequestIdPropagation is null ? 0 : 1);

    public static HostSettingsModel From(HostSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        HstsMaxAgeChoice maxAge = HstsMaxAgePresets.ChoiceOf(settings.Hsts?.MaxAge, out int? custom);
        return new HostSettingsModel
        {
            ForceHttps = settings.ForceHttps,
            MinimumTls = settings.Tls?.MinimumVersion,
            TcpAlpn = settings.Tls?.TcpAlpn,
            Http3 = settings.Tls?.Http3,
            AllowListSet = settings.IpAllowList is not null,
            AllowList = string.Join('\n', settings.IpAllowList?.Addresses ?? []),
            DropDenied = settings.IpAllowList?.DropDenied ?? false,
            HstsEnabled = settings.Hsts?.Enabled,
            HstsMaxAge = maxAge,
            HstsCustomMaxAge = custom,
            HstsIncludeSubDomains = settings.Hsts?.IncludeSubDomains,
            HstsPreload = settings.Hsts?.Preload,
            AccessLog = settings.AccessLog,
            RequestIdPropagation = settings.RequestIdPropagation,
        };
    }

    /// <summary><paramref name="stored"/> with this form's fields in place of its own.</summary>
    public HostSettings ApplyTo(HostSettings stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        int? maxAge = HstsMaxAgePresets.SecondsOf(HstsMaxAge, HstsCustomMaxAge);
        return stored with
        {
            ForceHttps = ForceHttps,
            Tls = MinimumTls is null && TcpAlpn is null && Http3 is null ? null : new TlsSettings { MinimumVersion = MinimumTls, TcpAlpn = TcpAlpn, Http3 = Http3 },
            IpAllowList = AllowListSet ? new IpAllowListSettings(HostEditModel.SplitList(AllowList), DropDenied) : null,
            Hsts = HstsEnabled is null && maxAge is null && HstsIncludeSubDomains is null && HstsPreload is null
                ? null
                : new HstsSettings { Enabled = HstsEnabled, MaxAge = maxAge, IncludeSubDomains = HstsIncludeSubDomains, Preload = HstsPreload },
            AccessLog = AccessLog,
            RequestIdPropagation = RequestIdPropagation,
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
            Hsts = new HstsSettings
            {
                Enabled = upper.Hsts?.Enabled ?? lower.Hsts?.Enabled,
                MaxAge = upper.Hsts?.MaxAge ?? lower.Hsts?.MaxAge,
                IncludeSubDomains = upper.Hsts?.IncludeSubDomains ?? lower.Hsts?.IncludeSubDomains,
                Preload = upper.Hsts?.Preload ?? lower.Hsts?.Preload,
            },
            AccessLog = upper.AccessLog ?? lower.AccessLog,
            RequestIdPropagation = upper.RequestIdPropagation ?? lower.RequestIdPropagation,
        };
    }

    /// <summary>What a layer sets, in short phrases for a card ("HTTPS redirect", "TLS 1.3 minimum", "HSTS: 1 year").</summary>
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
            // Set but empty is not "no list": it overrides whatever list the layer below sets, so every client is let through again.
            parts.Add(allow.Addresses.Count == 0 ? "Allow list: none (overrides the inherited one)" : $"Allow list: {allow.Addresses.Count} {(allow.Addresses.Count == 1 ? "entry" : "entries")}");
        }

        if (settings.Hsts is { } hsts)
        {
            if (hsts.Enabled == false)
            {
                parts.Add("HSTS off");
            }
            else if (hsts is not { Enabled: null, MaxAge: null, IncludeSubDomains: null, Preload: null })
            {
                // "when on" when the switch is left to the layer below: the values only take effect if something turns it on.
                parts.Add(hsts.Enabled == true ? $"HSTS: {HstsSummary(hsts)}" : $"HSTS when on: {HstsSummary(hsts)}");
            }
        }

        if (settings.AccessLog is { } accessLog)
        {
            parts.Add(accessLog ? "Access log on" : "No access log");
        }

        if (settings.RequestIdPropagation is { } propagation)
        {
            parts.Add($"Request ID: {PropagationText(propagation)}");
        }

        return parts;
    }

    /// <summary>At least its age, and what it covers: "1 year, subdomains, preload".</summary>
    private static string HstsSummary(HstsSettings hsts)
    {
        List<string> parts = [AgeText(hsts.MaxAge ?? HstsMaxAgePresets.OneYear)];
        if (hsts.IncludeSubDomains == true)
        {
            parts.Add("subdomains");
        }

        if (hsts.Preload == true)
        {
            parts.Add("preload");
        }

        return string.Join(", ", parts);
    }

    public static string TlsText(TlsMinimumVersion version) => version == TlsMinimumVersion.Tls13 ? "TLS 1.3" : "TLS 1.2";

    public static string AlpnText(TcpAlpn alpn) => alpn == Contracts.Config.TcpAlpn.Http11Only ? "HTTP/1.1 only" : "HTTP/2 and HTTP/1.1";

    /// <summary>A <c>max-age</c> in words: "5 minutes", "1 day", "6 months", "1 year", "2 years", else its seconds
    /// ("0 seconds" included: "forget it" is the help under the field, not the value).</summary>
    public static string AgeText(int seconds) => seconds switch
    {
        HstsMaxAgePresets.FiveMinutes => "5 minutes",
        HstsMaxAgePresets.OneDay => "1 day",
        HstsMaxAgePresets.SixMonths => "6 months",
        HstsMaxAgePresets.OneYear => "1 year",
        HstsMaxAgePresets.TwoYears => "2 years",
        _ => $"{seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} seconds",
    };

    /// <summary>Where the request ID goes, in the words of the form's "inherited from ..." option.</summary>
    public static string PropagationText(RequestIdPropagation propagation) => propagation switch
    {
        Contracts.Config.RequestIdPropagation.UpstreamOnly => "upstream only",
        Contracts.Config.RequestIdPropagation.ResponseOnly => "the client only",
        Contracts.Config.RequestIdPropagation.Off => "off",
        _ => "upstream and the client",
    };
}

/// <summary>The common <c>max-age</c> values a form offers, plus a custom number of seconds (PLAN §14 M6).</summary>
public enum HstsMaxAgeChoice
{
    /// <summary>The layer below's value.</summary>
    Inherit,

    /// <summary>300 seconds: long enough to try it, short enough to undo.</summary>
    FiveMinutes,

    /// <summary>A day.</summary>
    OneDay,

    /// <summary>About half a year.</summary>
    SixMonths,

    /// <summary>A year, the built-in default and the shortest a preload list accepts.</summary>
    OneYear,

    /// <summary>Two years, the longest Paduraksa sends.</summary>
    TwoYears,

    /// <summary>Whatever <see cref="HostSettingsModel.HstsCustomMaxAge"/> says.</summary>
    Custom,
}

/// <summary>The seconds behind each <see cref="HstsMaxAgeChoice"/>, and the choice a stored number is.</summary>
public static class HstsMaxAgePresets
{
    public const int FiveMinutes = 300;
    public const int OneDay = 86_400;
    public const int SixMonths = 15_552_000;
    public const int OneYear = 31_536_000;
    public const int TwoYears = 63_072_000;

    /// <summary>The longest <c>max-age</c> Paduraksa accepts.</summary>
    public const int Longest = TwoYears;

    /// <summary>The choice <paramref name="seconds"/> is; null is <see cref="HstsMaxAgeChoice.Inherit"/>.</summary>
    public static HstsMaxAgeChoice ChoiceOf(int? seconds, out int? custom)
    {
        switch (seconds)
        {
            case null:
                custom = null;
                return HstsMaxAgeChoice.Inherit;
            case FiveMinutes:
                custom = null;
                return HstsMaxAgeChoice.FiveMinutes;
            case OneDay:
                custom = null;
                return HstsMaxAgeChoice.OneDay;
            case SixMonths:
                custom = null;
                return HstsMaxAgeChoice.SixMonths;
            case OneYear:
                custom = null;
                return HstsMaxAgeChoice.OneYear;
            case TwoYears:
                custom = null;
                return HstsMaxAgeChoice.TwoYears;
            default:
                custom = seconds;
                return HstsMaxAgeChoice.Custom;
        }
    }

    /// <summary>The seconds a choice means; null for <see cref="HstsMaxAgeChoice.Inherit"/>.</summary>
    public static int? SecondsOf(HstsMaxAgeChoice choice, int? custom) => choice switch
    {
        HstsMaxAgeChoice.FiveMinutes => FiveMinutes,
        HstsMaxAgeChoice.OneDay => OneDay,
        HstsMaxAgeChoice.SixMonths => SixMonths,
        HstsMaxAgeChoice.OneYear => OneYear,
        HstsMaxAgeChoice.TwoYears => TwoYears,
        HstsMaxAgeChoice.Custom => custom,
        _ => null,
    };
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

    private ProfileEditModel(ProfileConfig? original, ProfileId id, ProfileConfig? source = null)
    {
        _original = original;
        Id = id;
        source ??= original;
        Name = source?.Name ?? string.Empty;
        Notes = source?.Notes;
        Settings = HostSettingsModel.From(source?.Settings ?? new HostSettings());
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

    /// <summary>A form for a profile made earlier and not applied yet (staged in a host's draft): still new, with what it was given.</summary>
    public static ProfileEditModel FromDraft(ProfileConfig profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new ProfileEditModel(null, profile.Id, profile);
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
