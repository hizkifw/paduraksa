using CsCheck;

using Paduraksa.Contracts.Config;
using Paduraksa.Core.Ids;
using Paduraksa.Core.Model;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Unit.Config.ConfigSamples;

namespace Paduraksa.Tests.Unit.Config;

/// <summary>
/// A generator of whole config documents for the property tests: every list, every setting layer, references that mostly resolve and
/// sometimes dangle, and values that are mostly ones a validator accepts and sometimes not. A document either validates or it doesn't; what matters
/// is what the rest of the pipeline does with the ones that do.
/// </summary>
internal static class ConfigGen
{
    private static readonly string[] HostNames = ["a.example.com", "*.example.com", "b.example.org", "localhost", "app.internal", "x.y.z.example.net", "UPPER.Example.COM"];

    private static readonly string[] BadHostNames = ["", "bad host", "*.*.x", "-x.com", "a..b", "a.example.com.", "http://x.com"];

    private static readonly string[] Paths = ["/", "/api", "/api/v1", "/static", "/a/b/c", "/Admin"];

    private static readonly string[] BadPaths = ["api", "/a//b", "/%41", "/a/./b", "", "/a b"];

    private static readonly string[] Networks = ["10.0.0.0/8", "192.168.1.0/24", "2001:db8::/32", "203.0.113.7", "::1", "172.16.0.0/12", "198.51.100.1/32"];

    private static readonly string[] BadNetworks = ["10.0.0.0/33", "not-an-ip", "", "1.2.3", "::g"];

    private static readonly string[] Destinations = ["http://app:8080", "https://app.internal", "http://10.0.0.5:9000", "http://[::1]:8080", "http://localhost"];

    private static readonly string[] BadDestinations = ["", "app:8080", "ftp://x", "http://", "http://a b"];

    private static readonly string[] Countries = ["US", "DE", "ID", "XX", "T1"];

    private static readonly string[] RulePatterns = ["/", "/admin", "/wp-*", "/**/.env", "/a/**/b", "/*.php"];

    private static readonly string[] BadRulePatterns = ["admin", "", "/\u00e9/*"];

    private static readonly string[] Regexes = [@"\.php$", "^/a/", "(a+)+$"];

    private static readonly string[] AuthUrls = ["http://auth:9091", "https://auth.example.com/verify", "http://authelia:9091/", "http://10.0.0.7:4180/oauth2/auth"];

    private static readonly string[] BadAuthUrls = ["", "authelia:9091", "ftp://x/", "http://user:pw@x/", "/relative"];

    private static readonly string[] HeaderNames = ["X-User", "X-Groups", "Remote-User", "Remote-Email", "X-Auth-*", "X-Tenant", "x-api-key"];

    private static readonly string[] RequestHeaderNames = ["X-Tenant", "x-api-key", "X-Org"];

    private static readonly string[] BadHeaderNames = ["Host", "X-Forwarded-For", "Cookie", "Content_Length", "a b", "*", "Au*"];

    /// <summary>How many of each kind of object a document has; references are drawn from these, plus one past the end that dangles.</summary>
    private sealed record Counts(int Upstreams, int Profiles, int Policies, int Lists, int RuleSets, int Providers, int Hosts, int Certificates);

    public static Gen<ConfigDocument> Documents { get; } =
        Gen.Select(
            Gen.Int[1, 3], Gen.Int[0, 2], Gen.Int[0, 2], Gen.Int[0, 2], Gen.Int[0, 2], Gen.Int[0, 2], Gen.Int[0, 4], Gen.Int[0, 2],
            static (u, pr, p, l, r, a, h, c) => new Counts(u, pr, p, l, r, a, h, c))
            .SelectMany(Build);

    private static Gen<ConfigDocument> Build(Counts n)
    {
        var settings = HostSettingsGen(n, route: false);
        return Gen.Select(
            Gen.Select(GlobalSettingsGen(n), Profiles(n), Upstreams(n), Hosts(n)),
            Gen.Select(Policies(n), AccessLists(n), RuleSets(n), Providers(n)),
            static (first, second) => new ConfigDocument(
                ConfigDocument.V1, first.Item1, first.Item2, first.Item3, first.Item4, second.Item1, second.Item2, second.Item3, second.Item4));
    }

    private static Gen<T?> Opt<T>(Gen<T> gen)
        where T : class =>
        Gen.Frequency((1, Gen.Const(static () => (T?)null)), (2, gen.Select(static x => (T?)x)));

    private static Gen<T?> OptValue<T>(Gen<T> gen)
        where T : struct =>
        Gen.Frequency((1, Gen.Const(static () => (T?)null)), (2, gen.Select(static x => (T?)x)));

    private static Gen<T> Mostly<T>(Gen<T> good, Gen<T> bad) => Gen.Frequency((80, good), (1, bad));

    private static Gen<T> Enum<T>()
        where T : struct, Enum =>
        Property.Pick(System.Enum.GetValues<T>());

    // Null is the object's own name, which is unique by position; the rare other is one a validator may refuse or a twin of another's.
    private static Gen<string?> Name() => Gen.Frequency(
        (80, Gen.Const(static () => (string?)null)),
        (1, Property.Pick<string?>("", "  ", "tab\there", "x\ny", new string('n', 300), "dup")));

    private static string NameOf(string? chosen, string label, int index) => chosen ?? $"{label} {index}";

    private static Gen<IReadOnlyList<string>> Strings(string[] good, string[] bad, int max) =>
        Mostly(Property.Pick(good), Property.Pick(bad)).List[0, max].Select(static x => (IReadOnlyList<string>)x);

    // An ID by position: the objects of the document are 100, 101, ..., and now and then a reference is to one that isn't there.
    private static Gen<T?> Ref<T>(int count)
        where T : struct, ITypedId<T, Guid> =>
        count == 0
            ? Gen.Frequency((400, Gen.Const(static () => (T?)null)), (1, Gen.Const(static () => (T?)ConfigSamples.Id<T>(9_000))))
            : Gen.Frequency(
                (20, Gen.Const(static () => (T?)null)),
                (40, Gen.Int[0, count - 1].Select(static i => (T?)ConfigSamples.Id<T>(100 + i))),
                (1, Gen.Const(static () => (T?)ConfigSamples.Id<T>(9_000))));

    private static T Own<T>(int index)
        where T : struct, ITypedId<T, Guid> => ConfigSamples.Id<T>(100 + index);

    private static Gen<GlobalSettings> GlobalSettingsGen(Counts n) => Gen.Select(
        Gen.Long[0, 5],
        HostSettingsGen(n, route: false),
        Enum<UnknownHostBehaviour>(),
        Ref<CertificateId>(n.Certificates),
        TrustedProxies(),
        static (version, defaults, unknown, certificate, proxies) => new GlobalSettings(version, defaults, unknown, certificate, proxies));

    // Each mode has the options it accepts: none for off, proxies and a header for generic, the Cloudflare flags for the Cloudflare modes.
    private static Gen<TrustedProxySettings> TrustedProxies() => Gen.Select(
        Enum<TrustedProxyMode>(),
        Property.Pick(Networks).List[1, 3],
        Property.Pick<string?>(null, "X-Real-IP", "x-forwarded-for"),
        Gen.Bool,
        Gen.Bool,
        Gen.Bool,
        Gen.Int[0, 120],
        static (mode, proxies, header, trueClient, fallback, proto, broken) =>
        {
            var generic = mode == TrustedProxyMode.Generic;
            var cloudflare = mode is TrustedProxyMode.CloudflareProxy or TrustedProxyMode.CloudflareTunnel;
            IReadOnlyList<string> listed = mode is TrustedProxyMode.Generic or TrustedProxyMode.CloudflareTunnel ? [.. proxies.Distinct()] : [];
            return new TrustedProxySettings(mode, broken == 0 ? [.. listed, "not-an-ip"] : listed, generic ? header : null, cloudflare && trueClient, cloudflare && fallback, generic && proto);
        });

    private static Gen<HostSettings> HostSettingsGen(Counts n, bool route) => route ? RouteLikeSettings(n) : Gen.Select(
        Gen.Select(OptValue(Gen.Bool), Opt(Tls()), Opt(Hsts()), OptValue(Gen.Bool), OptValue(Enum<RequestIdPropagation>())),
        Gen.Select(Opt(ServerHeader()), Opt(ProofOfWork(route: false)), Opt(RateLimit(n)), Opt(Robots())),
        Gen.Select(Opt(Cloudflare()), Opt(AccessListRef(n)), Opt(RequestLimits(route: false)), Opt(RuleSetRef(n)), Opt(AuthProviderRef(n))),
        static (a, b, c) => new HostSettings
        {
            ForceHttps = a.Item1,
            Tls = a.Item2,
            Hsts = a.Item3,
            AccessLog = a.Item4,
            RequestIdPropagation = a.Item5,
            ServerHeader = b.Item1,
            ProofOfWork = b.Item2,
            RateLimit = b.Item3,
            Robots = b.Item4,
            Cloudflare = c.Item1,
            AccessList = c.Item2,
            RequestLimits = c.Item3,
            RuleSet = c.Item4,
            AuthProvider = c.Item5,
        });

    // A route's own layer: the subset it may set, mostly.
    private static Gen<HostSettings> RouteLikeSettings(Counts n) => Gen.Const(static () => new HostSettings());

    private static Gen<RouteSettings> RouteSettings(Counts n) => Gen.Select(
        Opt(ProofOfWork(route: true)),
        Opt(RateLimit(n)),
        Opt(AccessListRef(n)),
        Opt(RequestLimits(route: true)),
        Opt(RuleSetRef(n)),
        Opt(AuthProviderRef(n)),
        static (pow, rate, list, limits, rules, auth) => new RouteSettings
        {
            ProofOfWork = pow,
            RateLimit = rate,
            AccessList = list,
            RequestLimits = limits,
            RuleSet = rules,
            AuthProvider = auth,
        });

    private static Gen<TlsSettings> Tls() => Gen.Select(
        OptValue(Enum<TlsMinimumVersion>()), OptValue(Enum<TcpAlpn>()), OptValue(Gen.Bool),
        static (min, alpn, h3) => new TlsSettings { MinimumVersion = min, TcpAlpn = alpn, Http3 = h3 });

    private static Gen<HstsSettings> Hsts() => Gen.Select(
        OptValue(Gen.Bool), OptValue(Mostly(Gen.Int[0, 63_072_000], Property.Pick(-1, 63_072_001))), OptValue(Gen.Bool), Gen.Int[0, 40],
        static (enabled, maxAge, sub, preload) => new HstsSettings { Enabled = enabled, MaxAge = maxAge, IncludeSubDomains = sub, Preload = preload == 0 ? true : null });

    private static Gen<ServerHeaderSettings> ServerHeader() => Gen.Select(
        OptValue(Enum<ServerHeaderMode>()), Mostly(Property.Pick("edge/1.0", "Paduraksa", "x y"), Property.Pick("", " lead", "trail ", "bad\tvalue", "\u00e9")),
        static (mode, value) => new ServerHeaderSettings { Mode = mode, Value = mode == ServerHeaderMode.Custom ? value : null });

    private static Gen<ProofOfWorkSettings> ProofOfWork(bool route) => route
        ? OptValue(Gen.Bool).Select(static enabled => new ProofOfWorkSettings { Enabled = enabled })
        : Gen.Select(
            OptValue(Gen.Bool),
            OptValue(Mostly(Gen.Int[ProofOfWorkLimits.MinDifficulty, ProofOfWorkLimits.MaxDifficulty], Property.Pick(0, 99))),
            OptValue(Mostly(Gen.Int[ProofOfWorkLimits.MinTokenLifetimeHours, ProofOfWorkLimits.MaxTokenLifetimeHours], Property.Pick(0, 100_000))),
            static (enabled, difficulty, hours) => new ProofOfWorkSettings { Enabled = enabled, Difficulty = difficulty, TokenLifetimeHours = hours });

    private static Gen<RateLimitSettings> RateLimit(Counts n) => Gen.Select(
        OptValue(Gen.Bool), Ref<RateLimitPolicyId>(n.Policies), static (enabled, policy) => new RateLimitSettings { Enabled = enabled, PolicyId = policy });

    private static Gen<RobotsSettings> Robots() => Gen.Select(
        OptValue(Enum<RobotsMode>()), Property.Pick("User-agent: *\nDisallow: /", "x\r\ny", "User-agent: Bot\nDisallow: /a"),
        static (mode, text) => new RobotsSettings { Mode = mode, CustomText = mode == RobotsMode.Custom ? text : null });

    private static Gen<CloudflareSettings> Cloudflare() => Gen.Select(
        OptValue(Enum<OriginLockdownMode>()), OptValue(Gen.Bool),
        static (lockdown, pulls) => new CloudflareSettings { OriginLockdown = lockdown, AuthenticatedOriginPulls = pulls });

    private static Gen<RequestLimitsSettings> RequestLimits(bool route) => route
        ? OptValue(Mostly(Gen.Long[0, 1L << 31], Property.Pick(-1L, long.MaxValue))).Select(static size => new RequestLimitsSettings { MaxBodySize = size })
        : Gen.Select(
            OptValue(Mostly(Gen.Long[0, 1L << 31], Property.Pick(-1L, long.MaxValue))),
            OptValue(Mostly(Gen.Int[8, 128], Property.Pick(0, 1000))),
            OptValue(Mostly(Gen.Int[1024, 65_536], Property.Pick(0, 1 << 20))),
            OptValue(Mostly(Gen.Int[256, 16_384], Property.Pick(0, 1 << 20))),
            static (body, count, bytes, url) => new RequestLimitsSettings { MaxBodySize = body, MaxHeaderCount = count, MaxHeaderBytes = bytes, MaxUrlLength = url });

    private static Gen<AccessListSettings> AccessListRef(Counts n) => Gen.Select(
        OptValue(Gen.Bool), Ref<AccessListId>(n.Lists), static (enabled, id) => new AccessListSettings { Enabled = enabled, AccessListId = id });

    private static Gen<RuleSetSettings> RuleSetRef(Counts n) => Gen.Select(
        OptValue(Gen.Bool), Ref<RuleSetId>(n.RuleSets), static (enabled, id) => new RuleSetSettings { Enabled = enabled, RuleSetId = id });

    private static Gen<AuthProviderSettings> AuthProviderRef(Counts n) => Gen.Select(
        OptValue(Gen.Bool), Ref<AuthProviderId>(n.Providers), static (enabled, id) => new AuthProviderSettings { Enabled = enabled, AuthProviderId = id });

    private static Gen<IReadOnlyList<ProfileConfig>> Profiles(Counts n) =>
        Gen.Select(Name(), Gen.Long[0, 3], HostSettingsGen(n, route: false), Opt(Property.Pick("note", "  ", "")), static (name, version, settings, notes) => (name, version, settings, notes))
            .Array[n.Profiles]
            .Select(items => (IReadOnlyList<ProfileConfig>)[.. items.Select((p, i) => new ProfileConfig(Own<ProfileId>(i), NameOf(p.name, "profile", i), p.version, p.settings, p.notes))]);

    private static Gen<IReadOnlyList<UpstreamConfig>> Upstreams(Counts n) =>
        Gen.Select(
            Name(),
            Mostly(Property.Pick(Destinations), Property.Pick(BadDestinations)).Array[1, 3],
            OptValue(Enum<LoadBalancingPolicy>()),
            Gen.Bool,
            Opt(Gen.Select(OptValue(Gen.Int[1, 600]), static s => new PassiveHealthConfig { ReactivationPeriod = s is { } v ? TimeSpan.FromSeconds(v) : null })),
            Opt(Gen.Select(Property.Pick("/", "/health", "/status/ready"), Gen.Int[1, 60], static (p, seconds) => new ActiveHealthConfig(p, TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(1)))),
            OptValue(Mostly(Gen.Int[1, 1000], Property.Pick(0, -5))),
            Opt(Gen.Select(Enum<UpstreamTlsTrust>(), Property.Pick("0f", "ab").Array[1, 2], static (trust, pins) => trust == UpstreamTlsTrust.Pinned
                ? new UpstreamTlsConfig(trust, [.. pins.Select(static (p, i) => string.Concat(Enumerable.Repeat(p, 32)))])
                : new UpstreamTlsConfig(trust))),
            static (name, destinations, lb, affinity, passive, active, max, tls) => (name, destinations, lb, affinity, passive, active, max, tls))
            .Array[n.Upstreams]
            .Select(items => (IReadOnlyList<UpstreamConfig>)[.. items.Select((u, i) => new UpstreamConfig(
                Own<UpstreamId>(i),
                NameOf(u.name, "upstream", i),
                0,
                [.. u.destinations.Select((d, j) => new DestinationConfig(ConfigSamples.Id<DestinationId>((i * 10) + j + 1), d))],
                u.lb,
                u.affinity,
                u.passive,
                u.active,
                u.max,
                null,
                u.tls))]);

    private static Gen<IReadOnlyList<HostConfig>> Hosts(Counts n) =>
        Gen.Select(
            Gen.Select(
                Gen.Select(Mostly(Property.Pick("example.com", "example.org", "app.internal", "example.net"), Property.Pick("bad host", "-x.com", "a..b")), Gen.Bool, static (b, wildcard) => (b, wildcard)).List[1, 2],
                Gen.Frequency((6, Gen.Const(static () => HostKind.Proxy)), (1, Gen.Const(static () => HostKind.Redirect)), (1, Gen.Const(static () => HostKind.Respond))),
                Gen.Bool,
                Ref<ProfileId>(n.Profiles),
                Gen.Select(
                    Gen.Frequency((8, Gen.Const(static () => CertificateBindingMode.Auto)), (4, Gen.Const(static () => CertificateBindingMode.Specific)), (1, Gen.Const(static () => CertificateBindingMode.None))),
                    Ref<CertificateId>(n.Certificates),
                    Gen.Int[0, 50],
                    static (mode, id, broken) => mode switch
                {
                        CertificateBindingMode.Specific => new CertificateBinding { Mode = mode, CertificateId = broken == 0 ? null : id ?? ConfigSamples.Id<CertificateId>(100) },
                        _ => new CertificateBinding { Mode = mode },
                    })),
            Gen.Select(
                HostSettingsGen(n, route: false),
                Routes(n),
                Gen.Select(Mostly(Property.Pick("https://example.com", "https://a.b/x"), Property.Pick("ftp://x", "")), Property.Pick(301, 302, 307, 308), Gen.Bool, static (t, s, p) => new RedirectConfig(t, s, p)),
                Gen.Select(Gen.Int[200, 599], Property.Pick("", "hello", "line\r\nbreak"), Property.Pick("text/plain", "text/html; charset=utf-8"), static (s, b, c) => new RespondConfig(s, b, c)),
                Strings(["media", "lan", "Public"], ["", "bad tag", new string('t', 80)], 3)),
            static (a, b) => (a, b))
            .Array[n.Hosts]
            .Select(items => (IReadOnlyList<HostConfig>)[.. items.Select((h, i) => new HostConfig(
                Own<HostId>(i),
                [.. h.a.Item1.Select(name => name.wildcard ? $"*.h{i}.{name.b}" : $"h{i}.{name.b}").Distinct()],
                0,
                h.a.Item2,
                h.a.Item3,
                h.a.Item4,
                h.a.Item5,
                h.b.Item1,
                h.a.Item2 == HostKind.Proxy ? [.. h.b.Item2.Select((r, j) => r with { Id = ConfigSamples.Id<RouteId>(500 + (i * 10) + j) })] : [],
                h.a.Item2 == HostKind.Redirect ? h.b.Item3 : null,
                h.a.Item2 == HostKind.Respond ? h.b.Item4 : null,
                h.b.Item5))]);

    private static Gen<IReadOnlyList<RouteConfig>> Routes(Counts n) =>
        Gen.Select(
            Gen.Int[0, Math.Max(0, n.Upstreams - 1)],
            Gen.Int[0, 30],
            Enum<RouteMatchKind>(),
            Mostly(Property.Pick(Paths), Property.Pick(BadPaths)),
            RouteSettings(n),
            static (upstream, order, match, path, settings) => (upstream, order, match, path, settings))
            .List[1, 3]
            .Select(items => (IReadOnlyList<RouteConfig>)[.. items.Select((r, i) => new RouteConfig(
                ConfigSamples.Id<RouteId>(500 + i),
                ConfigSamples.Id<UpstreamId>(n.Upstreams == 0 ? 9_000 : 100 + r.upstream),
                r.order,
                r.match,
                r.path,
                r.settings)).DistinctBy(static r => (r.Match, r.Path))]);

    private static Gen<IReadOnlyList<RateLimitPolicyConfig>> Policies(Counts n) =>
        Gen.Select(
            Name(),
            Mostly(Gen.Int[1, 60_000], Property.Pick(0, -1)),
            Enum<RateLimitPeriod>(),
            Mostly(Gen.Int[1, 10_000], Property.Pick(0, 1_000_000)),
            Enum<RateLimitKey>(),
            static (name, requests, per, burst, key) => (name, requests, per, burst, key))
            .Array[n.Policies]
            .Select(items => (IReadOnlyList<RateLimitPolicyConfig>)[.. items.Select((p, i) => new RateLimitPolicyConfig(
                Own<RateLimitPolicyId>(i), NameOf(p.name, "limit", i), 0, p.requests, p.per, p.burst, p.key, p.key == RateLimitKey.Header ? "X-Api-Key" : null))]);

    private static Gen<IReadOnlyList<AccessListConfig>> AccessLists(Counts n) =>
        Gen.Select(
            Name(),
            Gen.Select(Strings(Networks, BadNetworks, 3), Strings(Networks, BadNetworks, 2), Gen.Bool, Opt(Gen.Select(Property.Pick(Countries).List[0, 2], Property.Pick(Countries).List[0, 2], static (a, d) => new AccessListCountries([.. a.Distinct()], [.. d.Distinct().Except(a)])))),
            OptValue(Gen.Select(Mostly(Property.Pick<string?>(null, "Members"), Property.Pick<string?>("bad\nrealm")), Gen.Int[1, 2], static (realm, users) => (realm, users))),
            Enum<AccessListSatisfy>(),
            Gen.Bool,
            static (name, ip, basic, satisfy, pass) => (name, ip, basic, satisfy, pass))
            .Array[n.Lists]
            .Select(items => (IReadOnlyList<AccessListConfig>)[.. items.Select((l, i) => new AccessListConfig(
                Own<AccessListId>(i),
                NameOf(l.name, "list", i),
                0,
                null,
                new AccessListIp(l.ip.Item1, l.ip.Item2, l.ip.Item3, l.ip.Item4),
                l.basic is null ? null : new AccessListBasicAuth(l.basic.Value.realm, [.. Enumerable.Range(0, l.basic.Value.users).Select(u => new AccessListUserConfig($"user{u}", Credential((i * 10) + u + 1)))]),
                l.satisfy,
                l.pass))]);

    private static Gen<IReadOnlyList<RuleSetConfig>> RuleSets(Counts n) =>
        Gen.Select(
            Name(),
            Gen.Select(
                Enum<RuleAction>(),
                Gen.Select(
                    Enum<RulePathKind>(),
                    Mostly(Property.Pick(RulePatterns), Property.Pick(BadRulePatterns)),
                    Property.Pick(Regexes),
                    static (kind, pattern, regex) => kind == RulePathKind.Regex ? new RulePathConfig(kind, regex) : new RulePathConfig(kind, pattern)),
                Gen.Bool,
                Property.Pick<string[]>(["GET"], ["GET", "post"]),
                Property.Pick<string[]>([], ["sqlmap"], ["nikto", "curl"]),
                Gen.Bool,
                static (action, path, hasPath, methods, agents, empty) => (action, path: hasPath ? path : null, methods, agents, empty)).List[0, 3],
            static (name, rules) => (name, rules))
            .Array[n.RuleSets]
            .Select(items => (IReadOnlyList<RuleSetConfig>)[.. items.Select((s, i) => new RuleSetConfig(
                Own<RuleSetId>(i),
                NameOf(s.name, "rules", i),
                0,
                null,
                [.. s.rules.Select(r => new RuleConfig(r.action, new RuleMatchConfig(
                    r.path,
                    r.path is null && r.agents.Length == 0 && !r.empty ? r.methods : [],
                    r.agents.Length > 0 || r.empty ? new RuleUserAgentConfig(r.agents, r.empty) : null)))]))]);

    private static Gen<IReadOnlyList<AuthProviderConfig>> Providers(Counts n) =>
        Gen.Select(
            Name(),
            Mostly(Property.Pick(AuthUrls), Property.Pick(BadAuthUrls)),
            OptValue(Enum<ForwardAuthPreset>()),
            OptValue(Mostly(Gen.Int[1, 30], Property.Pick(0, 31))),
            OptValue(Mostly(Gen.Int[0, 300], Property.Pick(-1, 301))),
            OptValue(Enum<ForwardAuthCacheScope>()),
            Gen.Select(
                Strings(RequestHeaderNames, BadHeaderNames, 2),
                Strings(HeaderNames, BadHeaderNames, 3),
                Opt(Gen.Select(Property.Pick("/oauth2/start", "https://login.example.com/start"), Property.Pick<string?>(null, "next", "rd"), static (u, p) => new ForwardAuthSignInConfig(u, p))),
                static (request, response, signIn) => (request, response, signIn)),
            static (name, url, preset, timeout, cache, scope, rest) => (name, url, preset, timeout, cache, scope, rest))
            .Array[n.Providers]
            .Select(items => (IReadOnlyList<AuthProviderConfig>)[.. items.Select((a, i) => new AuthProviderConfig(
                Own<AuthProviderId>(i),
                NameOf(a.name, "sso", i),
                0,
                null,
                new ForwardAuthConfig(a.url, a.preset, a.timeout, a.cache, a.scope, a.rest.request, a.rest.response, a.rest.signIn)))]);
}
