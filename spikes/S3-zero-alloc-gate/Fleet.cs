namespace S3;

/// <summary>Builds the spike's snapshot: ~500 exact hosts + wildcards + IDN, each with 3 policies and 3 routes.</summary>
public static class Fleet
{
    public const int Hosts = 500;
    public const int Wildcards = 20;

    public static readonly string[] AiCrawlers =
    [
        "GPTBot", "ChatGPT-User", "OAI-SearchBot", "ClaudeBot", "Claude-Web", "anthropic-ai", "CCBot", "Google-Extended",
        "PerplexityBot", "Bytespider", "Amazonbot", "Applebot-Extended", "cohere-ai", "Diffbot", "FacebookBot",
        "ImagesiftBot", "Omgilibot", "Timpibot", "YouBot", "meta-externalagent", "sqlmap", "nikto", "masscan",
        "zgrab", "Nuclei", "WPScan", "dirbuster", "gobuster", "python-requests", "Go-http-client",
    ];

    public static readonly PrecomputedResponse Forbidden = PrecomputedResponse.Html(403,
        "<!doctype html><html><head><title>403 Forbidden</title></head><body><h1>403 Forbidden</h1><p>Blocked by Paduraksa.</p></body></html>");
    public static readonly PrecomputedResponse NotFound = PrecomputedResponse.Html(404,
        "<!doctype html><html><head><title>404</title></head><body><h1>Unknown host</h1></body></html>");
    public static readonly PrecomputedResponse Ok = PrecomputedResponse.Html(200, "ok from the gate");

    public static Snapshot Build(long generation = 1)
    {
        var rnd = new Random(42);
        var cidrs = new List<(UInt128, int)>();
        for (var i = 0; i < 1000; i++)
        {
            // random /24s in 11.0.0.0-99.255.255.255 (never 127/8, never 10/8) and some IPv6 /48s
            var v4 = (uint)rnd.Next(11 << 24, 99 << 24) & 0xFFFF_FF00u;
            cidrs.Add((Ip.FromV4(v4), 96 + 24));
            var v6 = ((UInt128)(ulong)rnd.NextInt64() << 64) & (UInt128.MaxValue << 80);
            cidrs.Add((v6 | ((UInt128)0x2a01UL << 112), 48));
        }
        var ipPolicy = new IpRangePolicy(cidrs, Forbidden);
        var uaPolicy = new UserAgentPolicy(AiCrawlers, denyEmpty: false, Forbidden);
        var pathPolicy = new PathPrefixPolicy(["/admin", "/.git", "/wp-login.php", "/.env"], Forbidden);

        var entries = new List<KeyValuePair<string, CompiledHost>>();
        CompiledHost Host(int i, string name) => new()
        {
            Index = i,
            Name = name,
            Policies = [ipPolicy, uaPolicy, pathPolicy],
            Routes =
            [
                new RouteEntry { Prefix = "/static", Respond = Ok },
                new RouteEntry { Prefix = "/old", Respond = PrecomputedResponse.Redirect(301, $"https://{name}/new") },
                new RouteEntry { Prefix = "/", Slot = i % Hosts },
            ],
        };
        for (var i = 0; i < Hosts; i++)
        {
            var name = i % 50 == 7 ? $"bücher{i}.test" : $"h{i}.test"; // every 50th host is an IDN name (stored as punycode)
            entries.Add(new(name, Host(i, name)));
        }
        for (var i = 0; i < Wildcards; i++) entries.Add(new($"*.w{i}.test", Host(Hosts + i, $"*.w{i}.test")));

        return new Snapshot
        {
            Generation = generation,
            Hosts = new HostTable<CompiledHost>(entries),
            UnknownHost = NotFound,
        };
    }
}
