using Paduraksa.Application.Compilation;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Config;

namespace Paduraksa.Dashboard.SettingsArea;

/// <summary>
/// The layers of host settings as the forms edit them: the contract's own <see cref="HostSettings"/> records, where a null
/// field means <i>inherit</i>. The forms bind to them directly; the built-in defaults and the merge are the backend's
/// (<see cref="HostSettingsLayers"/>), so the dashboard can never promise a value the proxy would not apply.
/// </summary>
public static class HostSettingsEdit
{
    /// <summary><paramref name="settings"/> with its TLS record changed, and no record at all when nothing is left set.</summary>
    public static HostSettings WithTls(HostSettings settings, Func<TlsSettings, TlsSettings> change)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(change);
        TlsSettings changed = change(settings.Tls ?? new TlsSettings());
        return settings with { Tls = changed.SetsNothing ? null : changed };
    }

    /// <summary><paramref name="settings"/> with its HSTS record changed, and no record at all when nothing is left set.</summary>
    public static HostSettings WithHsts(HostSettings settings, Func<HstsSettings, HstsSettings> change)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(change);
        HstsSettings changed = change(settings.Hsts ?? new HstsSettings());
        return settings with { Hsts = changed.SetsNothing ? null : changed };
    }

    /// <summary><paramref name="settings"/> with its <c>Server</c> header record changed, and no record at all when nothing is left set.</summary>
    public static HostSettings WithServerHeader(HostSettings settings, Func<ServerHeaderSettings, ServerHeaderSettings> change)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(change);
        ServerHeaderSettings changed = change(settings.ServerHeader ?? new ServerHeaderSettings());
        return settings with { ServerHeader = changed.SetsNothing ? null : changed };
    }

    /// <summary>
    /// How many settings a layer sets: each top-level field, and each field of a nested record, that is not null. The walk reads
    /// <see cref="HostSettings"/> itself, so a new setting is counted without a hand-kept list. A preset-or-custom choice with no
    /// seconds typed sets nothing, so it is not counted: the record really carries no value yet.
    /// </summary>
    public static int CountSet(HostSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        int count = 0;
        foreach (System.Reflection.PropertyInfo property in typeof(HostSettings).GetProperties())
        {
            object? value = property.GetValue(settings);
            if (value is null)
            {
                continue;
            }

            count += value is ISettingsLayer
                ? property.PropertyType.GetProperties().Count(nested => nested.Name != nameof(ISettingsLayer.SetsNothing) && nested.GetValue(value) is not null)
                : 1;
        }

        return count;
    }
}

/// <summary>What a layer sets, in short phrases for a card ("HTTPS redirect", "TLS 1.3 minimum", "HSTS: 1 year").</summary>
public static class SettingsSummary
{
    /// <summary>
    /// One phrase per setting, in the contract's field order. A new setting adds its phrase here, beside the field it describes.
    /// </summary>
    private static readonly Func<HostSettings, string?>[] Phrases =
    [
        static settings => settings.ForceHttps is { } force ? (force ? "Redirects to HTTPS" : "No HTTPS redirect") : null,
        static settings => settings.Tls?.MinimumVersion is { } minimum ? $"{TlsText(minimum)} minimum" : null,
        static settings => settings.Tls?.TcpAlpn is { } alpn ? AlpnText(alpn) : null,
        static settings => settings.Tls?.Http3 is { } http3 ? (http3 ? "HTTP/3 on" : "HTTP/3 off") : null,

        // Set but empty is not "no list": it overrides whatever list the layer below sets, so every client is let through again.
        static settings => settings.IpAllowList is { } allow
            ? (allow.Addresses.Count == 0 ? "Allow list: none (overrides the inherited one)" : $"Allow list: {allow.Addresses.Count} {(allow.Addresses.Count == 1 ? "entry" : "entries")}")
            : null,
        static settings => settings.Hsts is { } hsts ? HstsText(hsts) : null,
        static settings => settings.AccessLog is { } accessLog ? (accessLog ? "Access log on" : "Access log off") : null,
        static settings => settings.RequestIdPropagation is { } propagation ? $"Request ID: {PropagationText(propagation)}" : null,
        static settings => settings.ServerHeader is { } server ? ServerText(server) : null,
    ];

    /// <summary>What <paramref name="settings"/> sets, in short phrases.</summary>
    public static IReadOnlyList<string> Summary(HostSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        List<string> parts = [];
        foreach (Func<HostSettings, string?> phrase in Phrases)
        {
            if (phrase(settings) is { } part)
            {
                parts.Add(part);
            }
        }

        return parts;
    }

    /// <summary>A <c>Server</c> mode in words; the custom mode names its value when it has one.</summary>
    public static string ServerHeaderText(ServerHeaderMode? mode, string? value) => mode switch
    {
        ServerHeaderMode.Paduraksa => "Paduraksa",
        ServerHeaderMode.Custom => string.IsNullOrEmpty(value) ? "custom" : $"custom: {value}",
        ServerHeaderMode.Upstream => "the upstream's header",
        ServerHeaderMode.Remove => "none",
        _ => "the layer below's",
    };

    public static string TlsText(TlsMinimumVersion version) => version == TlsMinimumVersion.Tls13 ? "TLS 1.3" : "TLS 1.2";

    public static string AlpnText(TcpAlpn alpn) => alpn == Paduraksa.Core.Model.TcpAlpn.Http11Only ? "HTTP/1.1 only" : "HTTP/2 and HTTP/1.1";

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
        Paduraksa.Core.Model.RequestIdPropagation.UpstreamOnly => "upstream only",
        Paduraksa.Core.Model.RequestIdPropagation.ResponseOnly => "the client only",
        Paduraksa.Core.Model.RequestIdPropagation.Off => "off",
        _ => "upstream and the client",
    };

    /// <summary>At least its age, and what it covers: "1 year, subdomains, preload".</summary>
    private static string? HstsText(HstsSettings hsts)
    {
        if (hsts.Enabled == false)
        {
            return "HSTS off";
        }

        if (hsts is { Enabled: null, MaxAge: null, IncludeSubDomains: null, Preload: null })
        {
            return null;
        }

        List<string> parts = [AgeText(hsts.MaxAge ?? HstsMaxAgePresets.OneYear)];
        if (hsts.IncludeSubDomains == true)
        {
            parts.Add("subdomains");
        }

        if (hsts.Preload == true)
        {
            parts.Add("preload");
        }

        // "when on" when the switch is left to the layer below: the values only take effect if something turns it on.
        return $"{(hsts.Enabled == true ? "HSTS" : "HSTS when on")}: {string.Join(", ", parts)}";
    }

    private static string? ServerText(ServerHeaderSettings server)
    {
        if (server.Mode is { } mode)
        {
            return $"Server header: {(mode == ServerHeaderMode.Custom && !string.IsNullOrEmpty(server.Value) ? server.Value : ServerHeaderText(mode, server.Value))}";
        }

        return string.IsNullOrEmpty(server.Value) ? null : $"Server header value: {server.Value}";
    }
}

/// <summary>The common <c>max-age</c> values a form offers, plus a custom number of seconds.</summary>
public enum HstsMaxAgeChoice
{
    /// <summary>The layer below's value.</summary>
    Inherit,

    /// <summary>300 seconds: long enough to try it, short enough to undo.</summary>
    FiveMinutes,

    OneDay,

    SixMonths,

    /// <summary>A year, the built-in default and the shortest a preload list accepts.</summary>
    OneYear,

    /// <summary>Two years, the longest Paduraksa sends.</summary>
    TwoYears,

    /// <summary>Whatever number of seconds the custom field holds.</summary>
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
/// The global settings as the defaults form edits them: the bottom layer of host settings, what an unknown name gets,
/// the default certificate and who may tell us the client's address. Options that don't apply to the chosen trusted-proxy mode are
/// dropped when the settings are built, so switching modes never leaves a field the validator refuses.
/// </summary>
public sealed class GlobalSettingsEditModel
{
    private readonly GlobalSettings _original;

    private GlobalSettingsEditModel(GlobalSettings original)
    {
        _original = original;
        Defaults = original.Defaults;
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

    /// <summary>The layer of host settings, each field null to inherit the built-in default. Shared with the
    /// stored document: replace it with <c>with</c>, never mutate it in place.</summary>
    public HostSettings Defaults { get; set; }

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
            Defaults = Defaults,
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
        Settings = source?.Settings ?? new HostSettings();
    }

    public ProfileId Id { get; }

    public bool IsNew => _original is null;

    /// <summary>The version the form opened with (0 for a new profile).</summary>
    public long Version => _original?.Version ?? 0;

    public string Name { get; set; }

    public string? Notes { get; set; }

    /// <summary>Its layer of host settings, each field null to inherit the global defaults. Shared with the
    /// stored document: replace it with <c>with</c>, never mutate it in place.</summary>
    public HostSettings Settings { get; set; }

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
        Settings,
        string.IsNullOrWhiteSpace(Notes) ? null : Notes);

    /// <summary>The hosts that name <paramref name="profile"/>.</summary>
    public static IReadOnlyList<HostConfig> HostsUsing(ConfigDocument document, ProfileId profile)
    {
        ArgumentNullException.ThrowIfNull(document);
        return [.. document.Hosts.Where(h => h.ProfileId == profile).OrderBy(ChangeView.HostTitle, StringComparer.OrdinalIgnoreCase)];
    }
}
