using System.Globalization;

using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

namespace Paduraksa.Bench.Soak;

/// <summary>Where each upstream's destinations listen, and which of them the run stops.</summary>
internal sealed record UpstreamPlan(string Name, UpstreamId Id, IReadOnlyList<UpstreamNode> Nodes, UpstreamNode? Spare);

/// <summary>
/// The config the run publishes, and the handles the changes and the traffic need to find things in it again: about 50 hosts, seven upstreams of two or three
/// destinations, two profiles, and the features a homelab turns on (access log and analytics, rate limits, an access list, a rule set, custom error pages, forward
/// auth, proof of work, HSTS, custom headers, compression, a strip-prefix route).
/// </summary>
internal sealed class SoakConfig
{
    public const string Domain = "soak.test";

    public const int AppHosts = 30;

    public const int FlapHosts = 3;

    public const int LimitHosts = 5;

    // Every request of the soak comes from loopback, so one client IP is every client: the limited host's policy is the one that must refuse most of the traffic it
    // is sent, and the generous one (mid) must never refuse any.
    public const int LimitedPerSecond = 20;

    public const int MidPerSecond = 600;

    private static readonly CertificateBinding Placeholder = new()
    {
        Mode = CertificateBindingMode.Specific,
        CertificateId = CertificateId.FromValue(new Guid("0190a000-0000-7000-8000-70616c616365")),
    };

    public required ConfigDocument Document { get; init; }

    public required IReadOnlyList<UpstreamPlan> Upstreams { get; init; }

    public required UpstreamPlan Flappy { get; init; }

    public required UpstreamNode FlapNode { get; init; }

    public required IReadOnlyList<string> AppNames { get; init; }

    public required IReadOnlyList<string> FlapNames { get; init; }

    public required IReadOnlyList<string> LimitNames { get; init; }

    public required IReadOnlyList<string> AllHostNames { get; init; }

    public required RateLimitPolicyId MidPolicy { get; init; }

    public required AccessListId LanList { get; init; }

    public required HostId CanaryHost { get; init; }

    public required HostId HeadersHost { get; init; }

    public required IReadOnlyList<UpstreamNode> AllNodes { get; init; }

    public static string Name(string label) => label + "." + Domain;

    public static SoakConfig Build(SoakOptions options)
    {
        var port = options.UpstreamBase;
        List<UpstreamNode> allNodes = [];
        UpstreamPlan MakePlan(string name, int destinations, bool spare = false)
        {
            List<UpstreamNode> nodes = [];
            for (var i = 0; i < destinations + (spare ? 1 : 0); i++)
            {
                var node = new UpstreamNode($"{name}-{i}", port++);
                allNodes.Add(node);
                nodes.Add(node);
            }

            return new UpstreamPlan(name, UpstreamId.New(), nodes.Take(destinations).ToList(), spare ? nodes[^1] : null);
        }

        var web = MakePlan("web-a", 3);
        var webB = MakePlan("web-b", 2, spare: true);
        var webC = MakePlan("web-c", 3);
        var flappy = MakePlan("flappy", 3);
        var api = MakePlan("api", 2);
        var upload = MakePlan("upload", 2);
        var ws = MakePlan("ws", 3);
        UpstreamPlan[] plans = [web, webB, webC, flappy, api, upload, ws];

        var interval = TimeSpan.FromSeconds(5);
        var upstreams = plans.Select(plan => new UpstreamConfig(
            plan.Id,
            plan.Name,
            Destinations: plan.Nodes.Select(node => new DestinationConfig(DestinationId.New(), $"http://127.0.0.1:{node.Port}")).ToList(),
            LoadBalancing: plan.Name == "web-b" ? LoadBalancingPolicy.RoundRobin : LoadBalancingPolicy.PowerOfTwoChoices,
            ActiveHealth: new ActiveHealthConfig("/health", plan.Name == "flappy" ? TimeSpan.FromSeconds(3) : interval, TimeSpan.FromSeconds(2)),
            PassiveHealth: new PassiveHealthConfig { ReactivationPeriod = TimeSpan.FromSeconds(10) },
            // The WebSocket upstream's idle timeout is what cuts a silent socket; the chatty ones talk well inside it.
            Timeouts: plan.Name == "ws" ? new UpstreamTimeoutsConfig(Idle: TimeSpan.FromSeconds(WebSocketIdleSeconds)) : null,
            Notes: "soak")).ToList();

        var midPolicy = RateLimitPolicyId.New();
        var limitedPolicy = RateLimitPolicyId.New();
        var lan = AccessListId.New();
        var denyList = AccessListId.New();
        var scanners = RuleSetId.New();
        var auth = AuthProviderId.New();
        var publicProfile = ProfileId.New();
        var internalProfile = ProfileId.New();

        RateLimitPolicyConfig[] policies =
        [
            new(midPolicy, "mid", Requests: MidPerSecond, Per: RateLimitPeriod.Second, Burst: MidPerSecond * 2, Key: RateLimitKey.ClientIp),
            new(limitedPolicy, "limited", Requests: LimitedPerSecond, Per: RateLimitPeriod.Second, Burst: LimitedPerSecond, Key: RateLimitKey.ClientIp),
        ];

        AccessListConfig[] accessLists =
        [
            new(lan, "lan", Ip: new AccessListIp(Allow: LanAllow(0))),
            new(denyList, "blocked", Ip: new AccessListIp(Deny: ["198.51.100.0/24", "203.0.113.0/24"])),
        ];

        RuleSetConfig[] ruleSets =
        [
            new(scanners, "scanners", Rules:
            [
                new RuleConfig(RuleAction.Deny, new RuleMatchConfig(Path: new RulePathConfig(RulePathKind.Prefix, "/wp-admin"))),
                new RuleConfig(RuleAction.Deny, new RuleMatchConfig(Path: new RulePathConfig(RulePathKind.Exact, "/.env"))),
                new RuleConfig(RuleAction.Deny, new RuleMatchConfig(UserAgent: new RuleUserAgentConfig(Contains: ["sqlmap"]))),
            ]),
        ];

        AuthProviderConfig[] authProviders =
        [
            new(auth, "sso", ForwardAuth: new ForwardAuthConfig($"http://127.0.0.1:{options.AuthPort}/auth", ForwardAuthPreset.Custom, TimeoutSeconds: 3, CacheSeconds: 30, CacheScope: ForwardAuthCacheScope.Request)),
        ];

        ErrorPagesSettings errorPages = new()
        {
            NotFound = new ErrorPageContent { Mode = ErrorPageMode.Message, Heading = "Nothing here", Text = "That page does not exist on this soak host." },
            BadGateway = new ErrorPageContent { Mode = ErrorPageMode.Message, Heading = "Upstream is down", Text = "The soak upstream did not answer." },
            TooManyRequests = new ErrorPageContent { Mode = ErrorPageMode.Message, Heading = "Slow down", Text = "Too many requests." },
        };

        ProfileConfig[] profiles =
        [
            new(publicProfile, "public", Settings: new HostSettings
            {
                Hsts = new HstsSettings { Enabled = true, MaxAge = 31_536_000 },
                Compression = new CompressionSettings { Enabled = true, MinimumSize = 1024 },
                ErrorPages = errorPages,
                Headers = new HeadersSettings
                {
                    Response = [new HeaderRuleConfig("X-Soak-Profile", HeaderOperation.Set, "public")],
                },
            }),
            new(internalProfile, "internal", Settings: new HostSettings
            {
                AccessList = new AccessListSettings { Enabled = true, AccessListId = lan },
                RateLimit = new RateLimitSettings { Enabled = true, PolicyId = midPolicy },
                ErrorPages = errorPages,
            }),
        ];

        List<HostConfig> hosts = [];
        List<string> appNames = [];
        List<string> flapNames = [];
        List<string> limitNames = [];
        UpstreamPlan[] appUpstreams = [web, webB, webC, api, web];
        for (var i = 1; i <= AppHosts; i++)
        {
            var name = Name($"app{i:00}");
            appNames.Add(name);
            hosts.Add(ProxyHost(name, appUpstreams[i % appUpstreams.Length], profile: i <= 20 ? publicProfile : internalProfile));
        }

        for (var i = 1; i <= FlapHosts; i++)
        {
            var name = Name($"flap{i:00}");
            flapNames.Add(name);
            hosts.Add(ProxyHost(name, flappy, profile: publicProfile));
        }

        for (var i = 1; i <= LimitHosts; i++)
        {
            var name = Name($"limit{i:00}");
            limitNames.Add(name);
            hosts.Add(ProxyHost(name, appUpstreams[i % appUpstreams.Length], settings: new HostSettings { RateLimit = new RateLimitSettings { Enabled = true, PolicyId = midPolicy } }));
        }

        hosts.Add(ProxyHost(Name("lan"), api, settings: new HostSettings { AccessList = new AccessListSettings { Enabled = true, AccessListId = lan } }));
        hosts.Add(ProxyHost(Name("rules"), web, settings: new HostSettings { RuleSet = new RuleSetSettings { Enabled = true, RuleSetId = scanners } }));
        hosts.Add(ProxyHost(Name("auth"), api, settings: new HostSettings { AuthProvider = new AuthProviderSettings { Enabled = true, AuthProviderId = auth } }));
        hosts.Add(ProxyHost(
            Name("pow"), webC, settings: new HostSettings { ProofOfWork = new ProofOfWorkSettings { Enabled = true, Difficulty = PowDifficulty, TokenLifetimeHours = 1 } }));
        var headersHost = ProxyHost(
            Name("headers"), web, settings: new HostSettings
            {
                Headers = new HeadersSettings
                {
                    Request =
                    [
                        new HeaderRuleConfig("X-Soak-Tenant", HeaderOperation.Set, "acme"),
                        new HeaderRuleConfig("X-Soak-Added", HeaderOperation.Add, "one"),
                        new HeaderRuleConfig("X-Soak-Drop", HeaderOperation.Remove),
                    ],
                    Response =
                    [
                        new HeaderRuleConfig("X-Soak-Policy", HeaderOperation.Set, "v1"),
                        new HeaderRuleConfig("X-Powered-By", HeaderOperation.Remove),
                    ],
                },
                Compression = new CompressionSettings { Enabled = true },
            });
        hosts.Add(headersHost);
        hosts.Add(new HostConfig(
            HostId.New(), [Name("rewrite")], Certificate: Placeholder, ProfileId: publicProfile,
            Routes: [new RouteConfig(RouteId.New(), web.Id, Path: "/app", Rewrite: new PathRewriteConfig(StripPrefix: true, AddPrefix: "/api"))]));
        hosts.Add(ProxyHost(Name("https"), webB, settings: new HostSettings { ForceHttps = true, Hsts = new HstsSettings { Enabled = true, MaxAge = 31_536_000 } }));
        hosts.Add(new HostConfig(HostId.New(), [Name("redirect")], Kind: HostKind.Redirect, Certificate: Placeholder, Redirect: new RedirectConfig("https://app01." + Domain, 301)));
        hosts.Add(new HostConfig(HostId.New(), [Name("respond")], Kind: HostKind.Respond, Certificate: Placeholder, Respond: new RespondConfig(200, "ok\n")));
        hosts.Add(ProxyHost(Name("ws"), ws));
        hosts.Add(ProxyHost(Name("limited"), webC, settings: new HostSettings { RateLimit = new RateLimitSettings { Enabled = true, PolicyId = limitedPolicy }, ErrorPages = errorPages }));
        var canary = ProxyHost(
            Name("canary"), web, settings: new HostSettings
            {
                Headers = new HeadersSettings { Response = [new HeaderRuleConfig(RevisionHeader, HeaderOperation.Set, "0")] },
            });
        hosts.Add(canary);
        hosts.Add(ProxyHost(Name("upload"), upload, settings: new HostSettings { RequestLimits = new RequestLimitsSettings { MaxBodySize = UploadLimitBytes } }));

        ConfigDocument document = new(
            Settings: new GlobalSettings(
                Defaults: new HostSettings { AccessLog = true, ForceHttps = false },
                Connections: new ConnectionSettings(MaxConnectionsPerIp: 20_000, DenyListId: denyList)),
            Profiles: profiles,
            Upstreams: upstreams,
            Hosts: hosts,
            RateLimitPolicies: policies,
            AccessLists: accessLists,
            RuleSets: ruleSets,
            AuthProviders: authProviders);

        return new SoakConfig
        {
            Document = document,
            Upstreams = plans,
            Flappy = flappy,
            FlapNode = flappy.Nodes[1],
            AppNames = appNames,
            FlapNames = flapNames,
            LimitNames = limitNames,
            AllHostNames = hosts.SelectMany(h => h.Names).ToList(),
            MidPolicy = midPolicy,
            LanList = lan,
            CanaryHost = canary.Id,
            HeadersHost = headersHost.Id,
            AllNodes = allNodes,
        };

        HostConfig ProxyHost(string name, UpstreamPlan upstream, ProfileId? profile = null, HostSettings? settings = null) => new(
            HostId.New(), [name], Certificate: Placeholder, ProfileId: profile, Settings: settings,
            Routes: [new RouteConfig(RouteId.New(), upstream.Id)]);
    }

    public const string RevisionHeader = "X-Soak-Rev";

    public const int PowDifficulty = 8;

    public const int UploadLimitBytes = 1_048_576;

    public const int WebSocketIdleSeconds = 120;

    /// <summary>The lan list's allowed ranges: loopback and private space always, plus the numbered extra range the access-list edit adds and removes.</summary>
    public static IReadOnlyList<string> LanAllow(int extra)
    {
        List<string> allow = ["127.0.0.0/8", "::1/128", "10.0.0.0/8", "192.168.0.0/16"];
        for (var i = 0; i < extra; i++)
        {
            allow.Add(string.Create(CultureInfo.InvariantCulture, $"172.16.{i}.0/24"));
        }

        return allow;
    }
}
