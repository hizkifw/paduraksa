using Paduraksa.Contracts.Config;
using Paduraksa.Core.Ids;
using Paduraksa.Core.Model;

namespace Paduraksa.Tests.Unit.Config;

/// <summary>Documents for the config tests: a small valid one, and seeded random ones for property-style tests.</summary>
internal static class ConfigSamples
{
    public static readonly ProfileId LanProfile = Id<ProfileId>(1);
    public static readonly UpstreamId AppUpstream = Id<UpstreamId>(1);
    public static readonly DestinationId AppDestination = Id<DestinationId>(1);
    public static readonly HostId AppHost = Id<HostId>(1);
    public static readonly HostId RedirectHost = Id<HostId>(2);
    public static readonly HostId RespondHost = Id<HostId>(3);
    public static readonly RouteId RootRoute = Id<RouteId>(1);
    public static readonly RouteId ApiRoute = Id<RouteId>(2);
    public static readonly RateLimitPolicyId ApiPolicy = Id<RateLimitPolicyId>(1);
    public static readonly RateLimitPolicyId OtherPolicy = Id<RateLimitPolicyId>(2);

    /// <summary>A small valid document: one profile, one upstream, a proxy, a redirect and a respond host.</summary>
    public static ConfigDocument Valid() => new(
        Settings: new GlobalSettings(
            Defaults: new HostSettings { ForceHttps = true },
            TrustedProxies: new TrustedProxySettings(TrustedProxyMode.Generic, ["10.0.0.0/8"])),
        Profiles:
        [
            new ProfileConfig(LanProfile, "LAN only", Settings: new HostSettings { IpAllowList = new IpAllowListSettings(["192.168.0.0/16"]) }),
        ],
        Upstreams:
        [
            new UpstreamConfig(
                AppUpstream,
                "app",
                Destinations: [new DestinationConfig(AppDestination, "http://app:8080")],
                ActiveHealth: new ActiveHealthConfig("/healthz")),
        ],
        Hosts:
        [
            new HostConfig(
                AppHost,
                ["app.example.com", "*.app.example.com"],
                ProfileId: LanProfile,
                Routes: [new RouteConfig(RootRoute, AppUpstream, Order: 10), new RouteConfig(ApiRoute, AppUpstream, Path: "/api")],
                Tags: ["media"]),
            new HostConfig(RedirectHost, ["old.example.com"], Kind: HostKind.Redirect, Redirect: new RedirectConfig("https://app.example.com")),
            new HostConfig(RespondHost, ["soon.example.com"], Kind: HostKind.Respond, Respond: new RespondConfig(503, "coming soon")),
        ],
        RateLimitPolicies: [new RateLimitPolicyConfig(ApiPolicy, "api")]);

    /// <summary>A deterministic ID: <paramref name="n"/> in the low bits of a UUIDv7-shaped value.</summary>
    public static T Id<T>(int n)
        where T : struct, ITypedId<T, Guid> =>
        T.FromValue(new Guid($"01900000-0000-7000-8000-{n:x12}"));

    /// <summary>A random but structurally complete document; IDs, lists and optional parts vary with the seed.</summary>
    public static ConfigDocument Random(int seed)
    {
        Random random = new(seed);
        T NewId<T>()
            where T : struct, ITypedId<T, Guid>
        {
            Span<byte> bytes = stackalloc byte[16];
            random.NextBytes(bytes);
            return T.FromValue(new Guid(bytes));
        }

        bool Coin() => random.Next(2) == 0;
        T? Maybe<T>(Func<T> make)
            where T : class => Coin() ? make() : null;
        string[] Some(string[] pool) => [.. pool.Where(_ => Coin())];

        string[] addresses = ["10.0.0.0/8", "192.168.1.0/24", "2001:db8::/32", "203.0.113.7", "::1"];
        IpAllowListSettings AllowList()
        {
            var list = Some(addresses);
            return new IpAllowListSettings(list, DropDenied: list.Length > 0 && Coin());
        }

        HostSettings Settings() => new()
        {
            ForceHttps = Coin() ? Coin() : null,
            Tls = Maybe(() => new TlsSettings
            {
                MinimumVersion = Coin() ? (TlsMinimumVersion)random.Next(2) : null,
                TcpAlpn = Coin() ? (TcpAlpn)random.Next(2) : null,
                Http3 = Coin() ? Coin() : null,
            }),
            IpAllowList = Maybe(AllowList),
            Hsts = Maybe(() => new HstsSettings
            {
                Enabled = Coin() ? Coin() : null,
                MaxAge = Coin() ? random.Next(0, 63_072_001) : null,
                IncludeSubDomains = Coin() ? Coin() : null,
                Preload = Coin() ? Coin() : null,
            }),
            ServerHeader = Maybe(() => new ServerHeaderSettings
            {
                Mode = Coin() ? (ServerHeaderMode)random.Next(4) : null,
                Value = "edge/1.0",
            }),
            ProofOfWork = Maybe(() => new ProofOfWorkSettings
            {
                // Random documents must not change the gate's answer (the compiled-behaviour test reads them as plain
                // requests): the switch stays off, while the values still exercise the canonical form and the validator.
                Enabled = false,
                Difficulty = Coin() ? random.Next(ProofOfWorkLimits.MinDifficulty, ProofOfWorkLimits.MaxDifficulty + 1) : null,
                TokenLifetimeHours = Coin() ? random.Next(ProofOfWorkLimits.MinTokenLifetimeHours, ProofOfWorkLimits.MaxTokenLifetimeHours + 1) : null,
            }),
        };

        ProfileConfig[] profiles =
        [
            .. Enumerable.Range(0, random.Next(4)).Select(i => new ProfileConfig(
                NewId<ProfileId>(),
                $"profile {i}",
                Version: random.Next(5),
                Settings: Settings(),
                Notes: Maybe(() => "notes \u2713 <b>&</b>"))),
        ];
        UpstreamConfig[] upstreams =
        [
            .. Enumerable.Range(0, 1 + random.Next(3)).Select(i => new UpstreamConfig(
                NewId<UpstreamId>(),
                $"upstream {i}",
                Version: random.Next(5),
                Destinations:
                [
                    .. Enumerable.Range(0, 1 + random.Next(3))
                        .Select(j => new DestinationConfig(NewId<DestinationId>(), $"http://app{i}-{j}:80{j}", Enabled: Coin())),
                ],
                LoadBalancing: Coin() ? (LoadBalancingPolicy)random.Next(5) : null,
                SessionAffinity: Coin(),
                PassiveHealth: Maybe(() => new PassiveHealthConfig { ReactivationPeriod = Coin() ? TimeSpan.FromSeconds(random.Next(1, 600)) : null }),
                ActiveHealth: Maybe(() => new ActiveHealthConfig("/health", Interval: TimeSpan.FromMilliseconds(random.Next(1, 100_000)))),
                MaxConnectionsPerServer: Coin() ? random.Next(1, 1000) : null,
                Tls: Maybe(() => Coin()
                    ? new UpstreamTlsConfig(UpstreamTlsTrust.Pinned, [Convert.ToHexStringLower(BitConverter.GetBytes(random.NextInt64())).PadLeft(64, '0')])
                    : new UpstreamTlsConfig(Coin() ? UpstreamTlsTrust.Any : UpstreamTlsTrust.System)))),
        ];
        RateLimitPolicyConfig[] policies =
        [
            .. Enumerable.Range(0, random.Next(3)).Select(i =>
            {
                var key = (RateLimitKey)random.Next(3);
                return new RateLimitPolicyConfig(
                    NewId<RateLimitPolicyId>(),
                    $"limit {i}",
                    Version: random.Next(5),
                    Requests: random.Next(1, 60_000),
                    Per: (RateLimitPeriod)random.Next(3),
                    Burst: random.Next(1, 10_000),
                    Key: key,
                    Header: key == RateLimitKey.Header ? "X-Api-Key" : null,
                    Notes: Maybe(() => "rate notes"));
            }),
        ];
        HostConfig[] hosts =
        [
            .. Enumerable.Range(0, random.Next(5)).Select(i =>
            {
                var kind = (HostKind)random.Next(3);
                return new HostConfig(
                    NewId<HostId>(),
                    [$"h{i}.example.com", .. Some([$"*.h{i}.example.com", $"alt{i}.example.org", $"b\u00fccher{i}.example"])],
                    Version: random.Next(5),
                    Kind: kind,
                    Enabled: Coin(),
                    ProfileId: profiles.Length > 0 && Coin() ? profiles[random.Next(profiles.Length)].Id : null,
                    Certificate: random.Next(3) switch
                    {
                        0 => new CertificateBinding(),
                        1 => new CertificateBinding { Mode = CertificateBindingMode.None },
                        _ => new CertificateBinding { Mode = CertificateBindingMode.Specific, CertificateId = NewId<CertificateId>() },
                    },
                    Settings: Settings(),
                    Routes: kind != HostKind.Proxy
                        ? []
                        :
                        [
                            .. Enumerable.Range(0, 1 + random.Next(4)).Select(j => new RouteConfig(
                                NewId<RouteId>(),
                                upstreams[random.Next(upstreams.Length)].Id,
                                Order: random.Next(3) * 10,
                                Match: (RouteMatchKind)random.Next(2),
                                Path: j == 0 ? "/" : $"/p{j}",
                                Settings: new RouteSettings
                                {
                                    IpAllowList = Maybe(AllowList),
                                    // As on the host: off, so a generated document can't turn the gate on for the
                                    // compiled-behaviour reference. Enabled route overrides have their own tests.
                                    ProofOfWork = Maybe(() => new ProofOfWorkSettings { Enabled = false }),
                                })),
                        ],
                    Redirect: kind == HostKind.Redirect ? new RedirectConfig("https://example.com", 308, PreservePath: Coin()) : null,
                    Respond: kind == HostKind.Respond ? new RespondConfig(200 + random.Next(300), "\"quoted\"\n", "text/html") : null,
                    Tags: Some(["media", "lan", "public", "k8s:prod"]),
                    Notes: Maybe(() => "\u00fcber"));
            }),
        ];
        return new ConfigDocument(
            Settings: new GlobalSettings(
                Version: random.Next(10),
                Defaults: Settings(),
                UnknownHost: (UnknownHostBehaviour)random.Next(3),
                DefaultCertificateId: Coin() ? NewId<CertificateId>() : null,
                TrustedProxies: Coin()
                    ? new TrustedProxySettings()
                    : new TrustedProxySettings(TrustedProxyMode.Generic, Some(addresses), Header: Maybe(() => "X-Real-IP"))),
            Profiles: profiles,
            Upstreams: upstreams,
            Hosts: hosts,
            RateLimitPolicies: policies);
    }

    /// <summary>
    /// The same config as <paramref name="document"/>, written differently: every list but the routes shuffled, sets with
    /// duplicates and addresses in other spellings,
    /// host names and tags re-spelled, empty TLS and HSTS layers, blank notes. It must canonicalise to the same bytes.
    /// </summary>
    public static ConfigDocument Respelled(ConfigDocument document, int seed)
    {
        Random random = new(seed);
        T[] Shuffle<T>(IEnumerable<T> items)
        {
            T[] array = [.. items];
            random.Shuffle(array);
            return array;
        }

        string[] WithDuplicates(IReadOnlyList<string> values) => Shuffle(values.Concat(values.Where(_ => random.Next(2) == 0)));
        IpAllowListSettings? AllowList(IpAllowListSettings? list) =>
            list is null ? null : list with { Addresses = WithDuplicates([.. list.Addresses.Select(Respell)]) };
        string Respell(string address) => random.Next(3) switch
        {
            0 => $" {address} ",
            1 => address.ToUpperInvariant(),
            _ => address.Contains('/', StringComparison.Ordinal) ? address : address + (address.Contains(':', StringComparison.Ordinal) ? "/128" : "/32"),
        };
        HostSettings Settings(HostSettings settings) => settings with
        {
            Tls = settings.Tls ?? (random.Next(2) == 0 ? new TlsSettings() : null),
            IpAllowList = AllowList(settings.IpAllowList),
            // A layer that sets nothing is no layer (canonical JSON writes no key for it).
            Hsts = settings.Hsts ?? (random.Next(2) == 0 ? new HstsSettings() : null),
            ServerHeader = settings.ServerHeader ?? (random.Next(2) == 0 ? new ServerHeaderSettings() : null),
            ProofOfWork = settings.ProofOfWork ?? (random.Next(2) == 0 ? new ProofOfWorkSettings() : null),
            RateLimit = settings.RateLimit ?? (random.Next(2) == 0 ? new RateLimitSettings() : null),
        };

        return document with
        {
            Settings = document.Settings with
            {
                Defaults = Settings(document.Settings.Defaults),
                TrustedProxies = document.Settings.TrustedProxies with
                {
                    Proxies = WithDuplicates([.. document.Settings.TrustedProxies.Proxies.Select(Respell)]),
                    Header = document.Settings.TrustedProxies.Header?.ToUpperInvariant(),
                },
            },
            Profiles = Shuffle(document.Profiles.Select(p => p with
            {
                Name = $"  {p.Name}\t",
                Settings = Settings(p.Settings),
                Notes = p.Notes ?? "   ",
            })),
            Upstreams = Shuffle(document.Upstreams.Select(u => u with { Destinations = Shuffle(u.Destinations) })),
            RateLimitPolicies = Shuffle(document.RateLimitPolicies.Select(p => p with
            {
                Name = $"  {p.Name}\t",
                Header = p.Header?.ToUpperInvariant(),
                Notes = p.Notes ?? "   ",
            })),
            Hosts = Shuffle(document.Hosts.Select(h => h with
            {
                Names = WithDuplicates([.. h.Names.Select(n => random.Next(2) == 0 ? $" {n.ToUpperInvariant()}. " : n)]),
                Settings = Settings(h.Settings),
                // Routes keep their listed order: canonicalisation only reorders routes that differ in kind, path
                // length or Order, and this list order is the final tiebreaker.
                Routes = [.. h.Routes.Select(r => r with { Settings = r.Settings with { IpAllowList = AllowList(r.Settings.IpAllowList) } })],
                Tags = WithDuplicates([.. h.Tags.Select(t => $" {t.ToUpperInvariant()}")]),
                Notes = h.Notes ?? string.Empty,
            })),
        };
    }
}
