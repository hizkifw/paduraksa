using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Tls;

namespace Paduraksa.Application.Compilation;

/// <summary>
/// The settings layers merged in one place: built-in defaults → global defaults → profile → host, then
/// the route layer on top of the host's result. <c>Merge</c> lays one layer over another and keeps the result a
/// <i>layer</i> (a field neither layer sets stays unset); <see cref="Resolve"/> puts <see cref="BuiltIn"/> underneath and
/// turns the result into the concrete values the compiler, the validator and the dashboard work with.
/// </summary>
/// <remarks>
/// <para><b>Adding a setting</b> follows the checklist in PLAN §5.3: the property, its line in <c>Merge</c> (and in
/// the nested record's own merge), its value in <see cref="Concrete"/> and its built-in default among the constants
/// below, then its validator rules, its dashboard field and the tests.</para>
/// <para><b>The rules.</b> A null field inherits: keeping the result nullable is what lets a merged layer be a layer in
/// its turn. Nested settings (<see cref="HostSettings.Tls"/>, <see cref="HostSettings.Hsts"/>,
/// <see cref="HostSettings.ServerHeader"/>, <see cref="HostSettings.ProofOfWork"/>, <see cref="HostSettings.RateLimit"/>,
/// <see cref="HostSettings.Robots"/>, <see cref="HostSettings.Cloudflare"/>, <see cref="HostSettings.AccessList"/>, <see cref="HostSettings.RequestLimits"/>, <see cref="HostSettings.RuleSet"/>, <see cref="HostSettings.AuthProvider"/>, <see cref="HostSettings.ErrorPages"/>, <see cref="HostSettings.Maintenance"/>) merge field by field and stay absent when
/// neither layer sets a field of theirs. Everything else is a plain override.</para>
/// </remarks>
public static class HostSettingsLayers
{
    // The built-in defaults as concrete values: one statement each, so the layer every host sits on and the fallback
    // Concrete reads can't disagree, and neither needs a null-forgiving assertion about what BuiltIn holds.
    private const bool BuiltInForceHttps = false;
    private const TlsMinimumVersion BuiltInMinimumTlsVersion = TlsMinimumVersion.Tls12;
    private const TcpAlpn BuiltInTcpAlpn = TcpAlpn.Http2AndHttp11;
    private const bool BuiltInHttp3 = true;
    private const bool BuiltInHstsEnabled = false;
    private const int BuiltInHstsMaxAge = HstsHeader.DefaultMaxAgeSeconds;
    private const bool BuiltInHstsIncludeSubDomains = false;
    private const bool BuiltInHstsPreload = false;
    private const bool BuiltInAccessLog = true;
    private const RequestIdPropagation BuiltInRequestIdPropagation = RequestIdPropagation.UpstreamAndResponse;
    private const ServerHeaderMode BuiltInServerHeaderMode = ServerHeaderMode.Paduraksa;
    private const bool BuiltInProofOfWorkEnabled = false;
    private const int BuiltInProofOfWorkDifficulty = ProofOfWorkLimits.DefaultDifficulty;
    private const int BuiltInProofOfWorkTokenLifetimeHours = ProofOfWorkLimits.DefaultTokenLifetimeHours;
    private const bool BuiltInRateLimitEnabled = true;
    private const RobotsMode BuiltInRobotsMode = RobotsMode.Passthrough;
    private const OriginLockdownMode BuiltInOriginLockdown = OriginLockdownMode.Off;
    private const bool BuiltInAuthenticatedOriginPulls = false;
    private const bool BuiltInAccessListEnabled = true;
    private const long BuiltInMaxBodySize = RequestLimitBounds.DefaultMaxBodySize;
    private const int BuiltInMaxHeaderCount = RequestLimitBounds.DefaultMaxHeaderCount;
    private const int BuiltInMaxHeaderBytes = RequestLimitBounds.DefaultMaxHeaderBytes;
    private const int BuiltInMaxUrlLength = RequestLimitBounds.DefaultMaxUrlLength;
    private const bool BuiltInRuleSetEnabled = true;
    private const bool BuiltInAuthProviderEnabled = true;
    private const UpstreamErrorReplacement BuiltInReplaceUpstream = UpstreamErrorReplacement.Off;
    private const bool BuiltInMaintenanceEnabled = false;
    private const int BuiltInMaintenanceStatus = ErrorPageStatuses.DefaultMaintenanceStatus;
    private const int BuiltInMaintenanceRetryAfterSeconds = 0;

    /// <summary>The built-in defaults under every layer, one constant per statement below.</summary>
    public static HostSettings BuiltIn { get; } = new()
    {
        ForceHttps = BuiltInForceHttps,
        Tls = new TlsSettings { MinimumVersion = BuiltInMinimumTlsVersion, TcpAlpn = BuiltInTcpAlpn, Http3 = BuiltInHttp3 },
        Hsts = new HstsSettings { Enabled = BuiltInHstsEnabled, MaxAge = BuiltInHstsMaxAge, IncludeSubDomains = BuiltInHstsIncludeSubDomains, Preload = BuiltInHstsPreload },
        AccessLog = BuiltInAccessLog,
        RequestIdPropagation = BuiltInRequestIdPropagation,
        ServerHeader = new ServerHeaderSettings { Mode = BuiltInServerHeaderMode },
        ProofOfWork = new ProofOfWorkSettings
        {
            Enabled = BuiltInProofOfWorkEnabled,
            Difficulty = BuiltInProofOfWorkDifficulty,
            TokenLifetimeHours = BuiltInProofOfWorkTokenLifetimeHours,
        },
        RateLimit = new RateLimitSettings { Enabled = BuiltInRateLimitEnabled },
        Robots = new RobotsSettings { Mode = BuiltInRobotsMode },
        Cloudflare = new CloudflareSettings { OriginLockdown = BuiltInOriginLockdown, AuthenticatedOriginPulls = BuiltInAuthenticatedOriginPulls },
        AccessList = new AccessListSettings { Enabled = BuiltInAccessListEnabled },
        RequestLimits = new RequestLimitsSettings
        {
            MaxBodySize = BuiltInMaxBodySize,
            MaxHeaderCount = BuiltInMaxHeaderCount,
            MaxHeaderBytes = BuiltInMaxHeaderBytes,
            MaxUrlLength = BuiltInMaxUrlLength,
        },
        RuleSet = new RuleSetSettings { Enabled = BuiltInRuleSetEnabled },
        AuthProvider = new AuthProviderSettings { Enabled = BuiltInAuthProviderEnabled },
        ErrorPages = new ErrorPagesSettings { ReplaceUpstream = BuiltInReplaceUpstream },
        Maintenance = new MaintenanceSettings
        {
            Enabled = BuiltInMaintenanceEnabled,
            Status = BuiltInMaintenanceStatus,
            RetryAfterSeconds = BuiltInMaintenanceRetryAfterSeconds,
        },
    };

    /// <summary>Null-preserving, so the result is a layer too: a field neither layer sets stays unset.</summary>
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
            Hsts = Merge(lower.Hsts, upper.Hsts),
            AccessLog = upper.AccessLog ?? lower.AccessLog,
            RequestIdPropagation = upper.RequestIdPropagation ?? lower.RequestIdPropagation,
            ServerHeader = Merge(lower.ServerHeader, upper.ServerHeader),
            ProofOfWork = Merge(lower.ProofOfWork, upper.ProofOfWork),
            RateLimit = Merge(lower.RateLimit, upper.RateLimit),
            Robots = Merge(lower.Robots, upper.Robots),
            Cloudflare = Merge(lower.Cloudflare, upper.Cloudflare),
            AccessList = Merge(lower.AccessList, upper.AccessList),
            RequestLimits = Merge(lower.RequestLimits, upper.RequestLimits),
            RuleSet = Merge(lower.RuleSet, upper.RuleSet),
            AuthProvider = Merge(lower.AuthProvider, upper.AuthProvider),
            ErrorPages = Merge(lower.ErrorPages, upper.ErrorPages),
            Maintenance = Merge(lower.Maintenance, upper.Maintenance),
        };
    }

    /// <summary>The layers listed bottom to top, merged into one layer.</summary>
    public static HostSettings MergeAll(params ReadOnlySpan<HostSettings?> layers)
    {
        HostSettings merged = new();
        foreach (var layer in layers)
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

    /// <summary><paramref name="lower"/> with the fields <paramref name="upper"/> sets; absent when neither sets
    /// one.</summary>
    public static ProofOfWorkSettings? Merge(ProofOfWorkSettings? lower, ProofOfWorkSettings? upper)
    {
        if (upper is null)
        {
            return lower;
        }

        ProofOfWorkSettings merged = new()
        {
            Enabled = upper.Enabled ?? lower?.Enabled,
            Difficulty = upper.Difficulty ?? lower?.Difficulty,
            TokenLifetimeHours = upper.TokenLifetimeHours ?? lower?.TokenLifetimeHours,
        };

        return merged is { Enabled: null, Difficulty: null, TokenLifetimeHours: null } ? null : merged;
    }

    /// <summary><paramref name="lower"/> with the fields <paramref name="upper"/> sets; absent when neither sets
    /// one.</summary>
    public static RateLimitSettings? Merge(RateLimitSettings? lower, RateLimitSettings? upper)
    {
        if (upper is null)
        {
            return lower;
        }

        RateLimitSettings merged = new()
        {
            Enabled = upper.Enabled ?? lower?.Enabled,
            PolicyId = upper.PolicyId ?? lower?.PolicyId,
        };

        return merged is { Enabled: null, PolicyId: null } ? null : merged;
    }

    /// <summary><paramref name="lower"/> with the fields <paramref name="upper"/> sets; absent when neither sets
    /// one.</summary>
    public static RobotsSettings? Merge(RobotsSettings? lower, RobotsSettings? upper)
    {
        if (upper is null)
        {
            return lower;
        }

        RobotsSettings merged = new()
        {
            Mode = upper.Mode ?? lower?.Mode,
            CustomText = upper.CustomText ?? lower?.CustomText,
        };

        return merged is { Mode: null, CustomText: null } ? null : merged;
    }

    /// <summary><paramref name="lower"/> with the fields <paramref name="upper"/> sets; absent when neither sets
    /// one.</summary>
    public static CloudflareSettings? Merge(CloudflareSettings? lower, CloudflareSettings? upper)
    {
        if (upper is null)
        {
            return lower;
        }

        CloudflareSettings merged = new()
        {
            OriginLockdown = upper.OriginLockdown ?? lower?.OriginLockdown,
            AuthenticatedOriginPulls = upper.AuthenticatedOriginPulls ?? lower?.AuthenticatedOriginPulls,
        };

        return merged is { OriginLockdown: null, AuthenticatedOriginPulls: null } ? null : merged;
    }

    /// <summary><paramref name="lower"/> with the fields <paramref name="upper"/> sets; absent when neither sets
    /// one.</summary>
    public static AccessListSettings? Merge(AccessListSettings? lower, AccessListSettings? upper)
    {
        if (upper is null)
        {
            return lower;
        }

        AccessListSettings merged = new()
        {
            Enabled = upper.Enabled ?? lower?.Enabled,
            AccessListId = upper.AccessListId ?? lower?.AccessListId,
        };

        return merged is { Enabled: null, AccessListId: null } ? null : merged;
    }

    /// <summary><paramref name="lower"/> with the fields <paramref name="upper"/> sets; absent when neither sets
    /// one.</summary>
    public static RequestLimitsSettings? Merge(RequestLimitsSettings? lower, RequestLimitsSettings? upper)
    {
        if (upper is null)
        {
            return lower;
        }

        RequestLimitsSettings merged = new()
        {
            MaxBodySize = upper.MaxBodySize ?? lower?.MaxBodySize,
            MaxHeaderCount = upper.MaxHeaderCount ?? lower?.MaxHeaderCount,
            MaxHeaderBytes = upper.MaxHeaderBytes ?? lower?.MaxHeaderBytes,
            MaxUrlLength = upper.MaxUrlLength ?? lower?.MaxUrlLength,
        };

        return merged.SetsNothing ? null : merged;
    }

    /// <summary><paramref name="lower"/> with the fields <paramref name="upper"/> sets; absent when neither sets
    /// one.</summary>
    public static RuleSetSettings? Merge(RuleSetSettings? lower, RuleSetSettings? upper)
    {
        if (upper is null)
        {
            return lower;
        }

        RuleSetSettings merged = new()
        {
            Enabled = upper.Enabled ?? lower?.Enabled,
            RuleSetId = upper.RuleSetId ?? lower?.RuleSetId,
        };

        return merged is { Enabled: null, RuleSetId: null } ? null : merged;
    }

    /// <summary><paramref name="lower"/> with the fields <paramref name="upper"/> sets; absent when neither sets
    /// one.</summary>
    public static AuthProviderSettings? Merge(AuthProviderSettings? lower, AuthProviderSettings? upper)
    {
        if (upper is null)
        {
            return lower;
        }

        AuthProviderSettings merged = new()
        {
            Enabled = upper.Enabled ?? lower?.Enabled,
            AuthProviderId = upper.AuthProviderId ?? lower?.AuthProviderId,
        };

        return merged.SetsNothing ? null : merged;
    }

    /// <summary>
    /// <paramref name="lower"/> with the entries <paramref name="upper"/> sets, each replacing the lower layer's whole; absent when
    /// neither sets one.
    /// </summary>
    public static ErrorPagesSettings? Merge(ErrorPagesSettings? lower, ErrorPagesSettings? upper)
    {
        if (upper is null)
        {
            return lower;
        }

        var merged = new ErrorPagesSettings { ReplaceUpstream = upper.ReplaceUpstream ?? lower?.ReplaceUpstream };
        foreach (var (key, content) in upper.Entries())
        {
            if ((content ?? lower?.Entry(key)) is { } entry)
            {
                merged = merged.WithEntry(key, entry);
            }
        }

        return merged.SetsNothing ? null : merged;
    }

    /// <summary><paramref name="lower"/> with the fields <paramref name="upper"/> sets; absent when neither sets
    /// one.</summary>
    public static MaintenanceSettings? Merge(MaintenanceSettings? lower, MaintenanceSettings? upper)
    {
        if (upper is null)
        {
            return lower;
        }

        MaintenanceSettings merged = new()
        {
            Enabled = upper.Enabled ?? lower?.Enabled,
            Status = upper.Status ?? lower?.Status,
            RetryAfterSeconds = upper.RetryAfterSeconds ?? lower?.RetryAfterSeconds,
            BypassAccessListId = upper.BypassAccessListId ?? lower?.BypassAccessListId,
        };

        return merged.SetsNothing ? null : merged;
    }

    /// <summary>
    /// When, in <paramref name="layers"/> (listed bottom to top), the switch of the auth provider ends up off and the layer that names the provider is
    /// above the one that last set the switch, the index of the layer that names it, else -1. The merge leaves the switch where the lower layer put it,
    /// so the provider is named and never asked.
    /// </summary>
    internal static int ProviderNamingLayerAboveOffSwitch(params ReadOnlySpan<HostSettings?> layers)
    {
        var switchAt = -1;
        var namedAt = -1;
        var enabled = BuiltInAuthProviderEnabled;
        for (var i = 0; i < layers.Length; i++)
        {
            if (layers[i]?.AuthProvider is not { } layer)
            {
                continue;
            }

            if (layer.Enabled is { } on)
            {
                switchAt = i;
                enabled = on;
            }

            if (layer.AuthProviderId is { IsEmpty: false })
            {
                namedAt = i;
            }
        }

        return !enabled && namedAt > switchAt ? namedAt : -1;
    }

    /// <summary>The effective host settings of <paramref name="layers"/>, listed bottom (global defaults) to top (the
    /// host); null layers are skipped, and the built-in defaults sit underneath them all.</summary>
    internal static EffectiveHostSettings Resolve(params ReadOnlySpan<HostSettings?> layers) => Concrete(MergeAll(layers));

    /// <summary>The effective access list of a route: the route's own switch and list, field by field, over the host's
    /// resolved ones. So a route inherits, names another list, or turns the list off.</summary>
    internal static HostAccessListSpec ResolveRouteAccessList(EffectiveHostSettings host, RouteSettings? route)
    {
        var own = route?.AccessList;
        return own is null
            ? host.AccessList
            : new HostAccessListSpec { Enabled = own.Enabled ?? host.AccessList.Enabled, AccessListId = own.AccessListId ?? host.AccessList.AccessListId };
    }

    /// <summary>The effective rule set of a route: the route's own switch and set, field by field, over the host's
    /// resolved ones. So a route inherits, names another set, or turns the rules off.</summary>
    internal static HostRuleSetSpec ResolveRouteRuleSet(EffectiveHostSettings host, RouteSettings? route)
    {
        var own = route?.RuleSet;
        return own is null
            ? host.RuleSet
            : new HostRuleSetSpec { Enabled = own.Enabled ?? host.RuleSet.Enabled, RuleSetId = own.RuleSetId ?? host.RuleSet.RuleSetId };
    }

    /// <summary>The effective auth provider of a route: the route's own switch and provider, field by field, over the host's
    /// resolved ones. So a route inherits, names another provider, or turns authentication off.</summary>
    internal static HostAuthProviderSpec ResolveRouteAuthProvider(EffectiveHostSettings host, RouteSettings? route)
    {
        var own = route?.AuthProvider;
        return own is null
            ? host.AuthProvider
            : new HostAuthProviderSpec { Enabled = own.Enabled ?? host.AuthProvider.Enabled, AuthProviderId = own.AuthProviderId ?? host.AuthProvider.AuthProviderId };
    }

    /// <summary>The effective proof of work of a route: its own switch over the host's resolved values.</summary>
    internal static HostProofOfWorkSpec ResolveRouteProofOfWork(EffectiveHostSettings host, RouteSettings? route) =>
        route?.ProofOfWork?.Enabled is { } enabled ? host.ProofOfWork with { Enabled = enabled } : host.ProofOfWork;

    /// <summary>Whether the route's own layer decides its proof-of-work switch.</summary>
    internal static bool RouteOverridesProofOfWork(RouteSettings? route) => route?.ProofOfWork?.Enabled is not null;

    /// <summary>The effective rate limit of a route: the route's own switch and policy, field by field, over the host's
    /// resolved ones. So a route inherits, names another policy, or turns the limit off.</summary>
    internal static HostRateLimitSpec ResolveRouteRateLimit(EffectiveHostSettings host, RouteSettings? route)
    {
        var effective = host.RateLimit;
        var own = route?.RateLimit;
        if (own is null)
        {
            return effective;
        }

        return new HostRateLimitSpec
        {
            Enabled = own.Enabled ?? effective.Enabled,
            PolicyId = own.PolicyId ?? effective.PolicyId,
        };
    }

    /// <summary>Whether the route's own layer decides its rate limit.</summary>
    internal static bool RouteOverridesRateLimit(RouteSettings? route) =>
        route?.RateLimit is { Enabled: not null } or { PolicyId: not null };

    /// <summary>The body limit of a route: its own, when its layer sets one, else the host's resolved limit.</summary>
    internal static long ResolveRouteMaxBodySize(EffectiveHostSettings host, RouteSettings? route) =>
        route?.RequestLimits?.MaxBodySize ?? host.RequestLimits.MaxBodySize;

    /// <summary>Whether the route's own layer decides its body limit.</summary>
    internal static bool RouteOverridesMaxBodySize(RouteSettings? route) => route?.RequestLimits?.MaxBodySize is not null;

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
        var tls = settings.Tls ?? new TlsSettings();
        var hsts = settings.Hsts ?? new HstsSettings();
        var server = settings.ServerHeader ?? new ServerHeaderSettings();
        var proofOfWork = settings.ProofOfWork ?? new ProofOfWorkSettings();
        var rateLimit = settings.RateLimit ?? new RateLimitSettings();
        var robots = settings.Robots ?? new RobotsSettings();
        var cloudflare = settings.Cloudflare ?? new CloudflareSettings();
        var accessList = settings.AccessList ?? new AccessListSettings();
        var requestLimits = settings.RequestLimits ?? new RequestLimitsSettings();
        var ruleSet = settings.RuleSet ?? new RuleSetSettings();
        var authProvider = settings.AuthProvider ?? new AuthProviderSettings();
        var errorPages = settings.ErrorPages ?? new ErrorPagesSettings();
        var maintenance = settings.Maintenance ?? new MaintenanceSettings();
        return new EffectiveHostSettings(
            settings.ForceHttps ?? BuiltInForceHttps,
            new HostTlsSpec
            {
                MinimumVersion = tls.MinimumVersion ?? BuiltInMinimumTlsVersion,
                TcpAlpn = tls.TcpAlpn ?? BuiltInTcpAlpn,
                Http3 = tls.Http3 ?? BuiltInHttp3,
            },
            new HostHstsSpec
            {
                Enabled = hsts.Enabled ?? BuiltInHstsEnabled,
                MaxAgeSeconds = hsts.MaxAge ?? BuiltInHstsMaxAge,
                IncludeSubDomains = hsts.IncludeSubDomains ?? BuiltInHstsIncludeSubDomains,
                Preload = hsts.Preload ?? BuiltInHstsPreload,
            },
            settings.AccessLog ?? BuiltInAccessLog,
            settings.RequestIdPropagation ?? BuiltInRequestIdPropagation,
            new HostServerHeaderSpec
            {
                Mode = server.Mode ?? BuiltInServerHeaderMode,

                // No built-in value: only the Custom mode sends one, and the validator requires it there.
                Value = server.Value,
            },
            new HostProofOfWorkSpec
            {
                Enabled = proofOfWork.Enabled ?? BuiltInProofOfWorkEnabled,
                Difficulty = proofOfWork.Difficulty ?? BuiltInProofOfWorkDifficulty,
                TokenLifetimeHours = proofOfWork.TokenLifetimeHours ?? BuiltInProofOfWorkTokenLifetimeHours,
            },
            new HostRateLimitSpec
            {
                // Naming a policy is enough: only a layer that says otherwise turns the limit off.
                Enabled = rateLimit.Enabled ?? BuiltInRateLimitEnabled,
                PolicyId = rateLimit.PolicyId ?? RateLimitPolicyId.Empty,
            },
            new HostRobotsSpec
            {
                Mode = robots.Mode ?? BuiltInRobotsMode,

                // No built-in text: only the Custom mode serves one, and the validator requires it there.
                CustomText = robots.CustomText,
            },
            new HostCloudflareSpec
            {
                OriginLockdown = cloudflare.OriginLockdown ?? BuiltInOriginLockdown,
                AuthenticatedOriginPulls = cloudflare.AuthenticatedOriginPulls ?? BuiltInAuthenticatedOriginPulls,
            },
            new HostAccessListSpec
            {
                // Naming a list is enough: only a layer that says otherwise turns it off.
                Enabled = accessList.Enabled ?? BuiltInAccessListEnabled,
                AccessListId = accessList.AccessListId ?? AccessListId.Empty,
            },
            new HostRequestLimitsSpec
            {
                MaxBodySize = requestLimits.MaxBodySize ?? BuiltInMaxBodySize,
                MaxHeaderCount = requestLimits.MaxHeaderCount ?? BuiltInMaxHeaderCount,
                MaxHeaderBytes = requestLimits.MaxHeaderBytes ?? BuiltInMaxHeaderBytes,
                MaxUrlLength = requestLimits.MaxUrlLength ?? BuiltInMaxUrlLength,
            },
            new HostRuleSetSpec
            {
                // Naming a set is enough: only a layer that says otherwise turns it off.
                Enabled = ruleSet.Enabled ?? BuiltInRuleSetEnabled,
                RuleSetId = ruleSet.RuleSetId ?? RuleSetId.Empty,
            },
            new HostAuthProviderSpec
            {
                // Naming a provider is enough: only a layer that says otherwise turns it off.
                Enabled = authProvider.Enabled ?? BuiltInAuthProviderEnabled,
                AuthProviderId = authProvider.AuthProviderId ?? AuthProviderId.Empty,
            },
            ErrorPageSpecs(errorPages),
            new HostMaintenanceSpec
            {
                Enabled = maintenance.Enabled ?? BuiltInMaintenanceEnabled,
                Status = maintenance.Status ?? BuiltInMaintenanceStatus,
                RetryAfterSeconds = maintenance.RetryAfterSeconds ?? BuiltInMaintenanceRetryAfterSeconds,
                BypassAccessListId = maintenance.BypassAccessListId ?? AccessListId.Empty,
            });
    }

    // The pages a layer sets, as the compiler takes them. Where each came from is the caller's to say (ErrorPageSources),
    // since a merged layer no longer knows.
    private static HostErrorPagesSpec ErrorPageSpecs(ErrorPagesSettings settings)
    {
        Dictionary<int, ErrorPageSpec> pages = [];
        foreach (var (key, content) in settings.Entries())
        {
            if (content is not null)
            {
                pages[key] = new ErrorPageSpec { Mode = content.Mode, Heading = content.Heading, Text = content.Text, Html = content.Html };
            }
        }

        return new HostErrorPagesSpec { ReplaceUpstream = settings.ReplaceUpstream ?? BuiltInReplaceUpstream, Pages = pages };
    }

    /// <summary>The pages of <paramref name="resolved"/> with the layer each one was set by, judged on the host's own chain.</summary>
    internal static HostErrorPagesSpec WithErrorPageSources(HostErrorPagesSpec resolved, HostSettings? defaults, HostSettings? profile, HostSettings? host)
    {
        Dictionary<int, ErrorPageSpec> pages = [];
        foreach (var (key, page) in resolved.Pages)
        {
            var layer = LayerOf(defaults, profile, host, settings => settings.ErrorPages?.Entry(key));
            var source = layer switch
            {
                SettingsLayer.Host => ErrorPageSource.Host,
                SettingsLayer.Profile => ErrorPageSource.Profile,
                SettingsLayer.GlobalDefaults => ErrorPageSource.GlobalDefaults,
                _ => ErrorPageSource.BuiltIn,
            };
            pages[key] = page with { Source = source };
        }

        return resolved with { Pages = pages };
    }
}

/// <summary>Which layer of a host's chain set a field, when a message has to name it.</summary>
public enum SettingsLayer
{
    /// <summary>Nothing set it, so the built-in default (<see cref="HostSettingsLayers.BuiltIn"/>) holds.</summary>
    BuiltIn,

    GlobalDefaults,

    Profile,

    Host,
}

/// <summary>A host's settings with every layer resolved, in <see cref="HostSettings"/>'s field order.</summary>
/// <param name="ForceHttps">Redirect plain HTTP to HTTPS.</param>
/// <param name="Tls">Concrete TLS settings.</param>
/// <param name="Hsts">Concrete HSTS settings.</param>
/// <param name="AccessLog">Write a line per request to the access log (built-in default: on).</param>
/// <param name="RequestIdPropagation">Where the request ID is sent (built-in default: upstream and response).</param>
/// <param name="ServerHeader">Concrete <c>Server</c> header settings.</param>
/// <param name="ProofOfWork">Concrete proof-of-work settings.</param>
/// <param name="RateLimit">The policy in force, with the switch as it resolved.</param>
/// <param name="Robots">What the host answers at <c>/robots.txt</c>.</param>
/// <param name="Cloudflare">Origin lockdown and Authenticated Origin Pulls.</param>
/// <param name="AccessList">The access list in force, with the switch as it resolved.</param>
/// <param name="RequestLimits">The body, header and request-target limits.</param>
/// <param name="RuleSet">The rule set in force, with the switch as it resolved.</param>
/// <param name="AuthProvider">The auth provider in force, with the switch as it resolved.</param>
/// <param name="ErrorPages">The pages the layers set, and whether an upstream's error bodies are replaced.</param>
/// <param name="Maintenance">Maintenance mode as it resolved.</param>
internal sealed record EffectiveHostSettings(
    bool ForceHttps,
    HostTlsSpec Tls,
    HostHstsSpec Hsts,
    bool AccessLog,
    RequestIdPropagation RequestIdPropagation,
    HostServerHeaderSpec ServerHeader,
    HostProofOfWorkSpec ProofOfWork,
    HostRateLimitSpec RateLimit,
    HostRobotsSpec Robots,
    HostCloudflareSpec Cloudflare,
    HostAccessListSpec AccessList,
    HostRequestLimitsSpec RequestLimits,
    HostRuleSetSpec RuleSet,
    HostAuthProviderSpec AuthProvider,
    HostErrorPagesSpec ErrorPages,
    HostMaintenanceSpec Maintenance);
