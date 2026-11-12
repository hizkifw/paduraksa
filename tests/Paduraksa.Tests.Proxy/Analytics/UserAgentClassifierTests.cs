using Paduraksa.Proxy.Analytics;

namespace Paduraksa.Tests.Proxy.Analytics;

/// <summary>The classifier against real user agents: browsers, systems, devices and every kind of bot.</summary>
public sealed class UserAgentClassifierTests
{
    private const string Chrome131Windows = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    public static TheoryData<string, string, int, string, DeviceClass> Browsers => new()
    {
        { Chrome131Windows, "Chrome", 131, "Windows", DeviceClass.Desktop },
        { "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36", "Chrome", 130, "macOS", DeviceClass.Desktop },
        { "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Safari/537.36", "Chrome", 129, "Linux", DeviceClass.Desktop },
        { "Mozilla/5.0 (X11; CrOS x86_64 14541.0.0) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36", "Chrome", 128, "ChromeOS", DeviceClass.Desktop },
        { "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:133.0) Gecko/20100101 Firefox/133.0", "Firefox", 133, "Windows", DeviceClass.Desktop },
        { "Mozilla/5.0 (X11; Ubuntu; Linux x86_64; rv:132.0) Gecko/20100101 Firefox/132.0", "Firefox", 132, "Linux", DeviceClass.Desktop },
        { "Mozilla/5.0 (Macintosh; Intel Mac OS X 10.15; rv:131.0) Gecko/20100101 Firefox/131.0", "Firefox", 131, "macOS", DeviceClass.Desktop },
        { "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.6 Safari/605.1.15", "Safari", 17, "macOS", DeviceClass.Desktop },
        { "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1", "Safari", 17, "iOS", DeviceClass.Mobile },
        { "Mozilla/5.0 (iPad; CPU OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1", "Safari", 17, "iPadOS", DeviceClass.Tablet },
        { "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/126.0.6478.153 Mobile/15E148 Safari/604.1", "Chrome", 126, "iOS", DeviceClass.Mobile },
        { "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) FxiOS/127.0 Mobile/15E148 Safari/605.1.15", "Firefox", 127, "iOS", DeviceClass.Mobile },
        { "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.2903.51", "Edge", 131, "Windows", DeviceClass.Desktop },
        { "Mozilla/5.0 (Linux; Android 10; K) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Mobile Safari/537.36 EdgA/131.0.0.0", "Edge", 131, "Android", DeviceClass.Mobile },
        { "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 OPR/115.0.0.0", "Opera", 115, "Windows", DeviceClass.Desktop },
        { "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Vivaldi/7.0.3495.18", "Vivaldi", 7, "Windows", DeviceClass.Desktop },
        { "Mozilla/5.0 (Linux; Android 14; SAMSUNG SM-S918B) AppleWebKit/537.36 (KHTML, like Gecko) SamsungBrowser/26.0 Chrome/122.0.0.0 Mobile Safari/537.36", "Samsung Internet", 26, "Android", DeviceClass.Mobile },
        { "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.6778.39 Mobile Safari/537.36", "Chrome", 131, "Android", DeviceClass.Mobile },
        { "Mozilla/5.0 (Linux; Android 13; SM-X700) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36", "Chrome", 130, "Android", DeviceClass.Tablet },
        { "Mozilla/5.0 (Linux; Android 12; SM-G991B Build/SP1A.210812.016; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/108.0.5359.128 Mobile Safari/537.36", "Android WebView", 108, "Android", DeviceClass.Mobile },
        { "Mozilla/5.0 (Windows NT 6.1; WOW64; Trident/7.0; AS; rv:11.0) like Gecko", "Internet Explorer", 11, "Windows", DeviceClass.Desktop },
        { "Mozilla/5.0 (compatible; MSIE 10.0; Windows NT 6.2; Trident/6.0)", "Internet Explorer", 10, "Windows", DeviceClass.Desktop },
        { "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/70.0.3538.102 Safari/537.36 Edge/18.19582", "Edge", 18, "Windows", DeviceClass.Desktop },
        { "Mozilla/5.0 (Linux; Android 13; SM-A546B) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 YaBrowser/24.10.0 Mobile Safari/537.36", "Yandex Browser", 24, "Android", DeviceClass.Mobile },
        { "Mozilla/5.0 (Linux; U; Android 13; en-US; V2250 Build/TP1A.220624.014) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/100.0.4896.58 UCBrowser/15.5.2.1 Mobile Safari/537.36", "UC Browser", 15, "Android", DeviceClass.Mobile },
        { "Mozilla/5.0 (Linux; Android 11; Mobile) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Mobile Safari/537.36 DuckDuckGo/5", "DuckDuckGo", 5, "Android", DeviceClass.Mobile },
        { "Mozilla/5.0 (SMART-TV; Linux; Tizen 7.0) AppleWebKit/537.36 (KHTML, like Gecko) 108.0.5359.1/7.0 TV Safari/537.36", "Other", 0, "Linux", DeviceClass.Other },
        { "Mozilla/5.0 (PlayStation; PlayStation 5/2.26) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/13.0 Safari/605.1.15", "Safari", 13, "Other", DeviceClass.Other },
        { "Mozilla/5.0 (Windows Phone 10.0; Android 6.0.1; Microsoft; Lumia 950) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/52.0.2743.116 Mobile Safari/537.36 Edge/15.15254", "Edge", 15, "Windows Phone", DeviceClass.Mobile },
        { "Mozilla/5.0 (Linux; Android 9; AFTMM Build/PS7233) AppleWebKit/537.36 (KHTML, like Gecko) Silk/118.5.1 like Chrome/118.0.0.0 Safari/537.36", "Chrome", 118, "Android", DeviceClass.Other },
    };

    [Theory]
    [MemberData(nameof(Browsers))]
    public void ABrowserIsReadAsItsFamilySystemAndDevice(string userAgent, string browser, int version, string os, DeviceClass device)
    {
        var info = UserAgentClassifier.Classify(userAgent);

        Assert.Equal((browser, version, os, device, BotClass.None, (string?)null), (info.Browser, info.BrowserVersion, info.Os, info.Device, info.Bot, info.BotName));
        Assert.False(info.IsBot);
    }

    private static readonly (string UserAgent, BotClass Kind, string Name)[] BotTable =
    [
        // AI crawlers: the names robots.txt blocks.
        ("Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko; compatible; GPTBot/1.2; +https://openai.com/gptbot)", BotClass.AiCrawler, "GPTBot"),
        ("Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko; compatible; ClaudeBot/1.0; +claudebot@anthropic.com)", BotClass.AiCrawler, "ClaudeBot"),
        ("Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko; compatible; PerplexityBot/1.0; +https://perplexity.ai/perplexitybot)", BotClass.AiCrawler, "PerplexityBot"),
        ("Mozilla/5.0 (Linux; Android 5.0) AppleWebKit/537.36 (KHTML, like Gecko) Mobile Safari/537.36 (compatible; Bytespider; spider-feedback@bytedance.com)", BotClass.AiCrawler, "Bytespider"),
        ("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_5) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/13.1.1 Safari/605.1.15 (Applebot/0.1; +http://www.apple.com/go/applebot) Amazonbot/0.1", BotClass.AiCrawler, "Amazonbot"),
        ("CCBot/2.0 (https://commoncrawl.org/faq/)", BotClass.AiCrawler, "CCBot"),
        ("meta-externalagent/1.1 (+https://developers.facebook.com/docs/sharing/webmasters/crawler)", BotClass.AiCrawler, "Meta-ExternalAgent"),
        // Search engines.
        ("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)", BotClass.SearchEngine, "Googlebot"),
        ("Mozilla/5.0 (Linux; Android 6.0.1; Nexus 5X Build/MMB29P) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Mobile Safari/537.36 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)", BotClass.SearchEngine, "Googlebot"),
        ("Mozilla/5.0 (compatible; bingbot/2.0; +http://www.bing.com/bingbot.htm)", BotClass.SearchEngine, "Bingbot"),
        ("Mozilla/5.0 (compatible; Yahoo! Slurp; http://help.yahoo.com/help/us/ysearch/slurp)", BotClass.SearchEngine, "Yahoo Slurp"),
        ("Mozilla/5.0 (compatible; DuckDuckBot-Https/1.1; https://duckduckgo.com/duckduckbot)", BotClass.SearchEngine, "DuckDuckBot"),
        ("Mozilla/5.0 (compatible; Baiduspider/2.0; +http://www.baidu.com/search/spider.html)", BotClass.SearchEngine, "Baiduspider"),
        ("Mozilla/5.0 (compatible; YandexBot/3.0; +http://yandex.com/bots)", BotClass.SearchEngine, "YandexBot"),
        ("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_5) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/13.1.1 Safari/605.1.15 (Applebot/0.1; +http://www.apple.com/go/applebot)", BotClass.SearchEngine, "Applebot"),
        ("Mozilla/5.0 (compatible; SeznamBot/4.0; +https://o-seznam.cz/napoveda/vyhledavani/en/seznambot-crawler/)", BotClass.SearchEngine, "SeznamBot"),
        ("Mozilla/5.0 (Linux; Android 7.0;) AppleWebKit/537.36 (KHTML, like Gecko) Mobile Safari/537.36 (compatible; PetalBot;+https://webmaster.petalsearch.com/site/petalbot)", BotClass.SearchEngine, "PetalBot"),
        // Social previews.
        ("facebookexternalhit/1.1 (+http://www.facebook.com/externalhit_uatext.php)", BotClass.SocialPreview, "Facebook"),
        ("Twitterbot/1.0", BotClass.SocialPreview, "Twitter"),
        ("LinkedInBot/1.0 (compatible; Mozilla/5.0; Apache-HttpClient +http://www.linkedin.com)", BotClass.SocialPreview, "LinkedIn"),
        ("Mozilla/5.0 (compatible; Discordbot/2.0; +https://discordapp.com)", BotClass.SocialPreview, "Discord"),
        ("Slackbot-LinkExpanding 1.0 (+https://api.slack.com/robots)", BotClass.SocialPreview, "Slack"),
        ("TelegramBot (like TwitterBot)", BotClass.SocialPreview, "Telegram"),
        ("WhatsApp/2.23.20.0 A", BotClass.SocialPreview, "WhatsApp"),
        ("http.rb/5.1.1 (Mastodon/4.3.1; +https://mastodon.social/)", BotClass.SocialPreview, "Mastodon"),
        // Monitors.
        ("Mozilla/5.0 (compatible; UptimeRobot/2.0; http://www.uptimerobot.com/)", BotClass.Monitor, "UptimeRobot"),
        ("Mozilla/5.0 (compatible; Pingdom.com_bot_version_1.4_(http://www.pingdom.com/))", BotClass.Monitor, "Pingdom"),
        ("Uptime-Kuma/1.23.13", BotClass.Monitor, "Uptime Kuma"),
        ("Blackbox Exporter/0.25.0", BotClass.Monitor, "Prometheus Blackbox"),
        ("kube-probe/1.30", BotClass.Monitor, "Kubernetes probe"),
        ("ELB-HealthChecker/2.0", BotClass.Monitor, "AWS load balancer"),
        ("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/109.0.0.0 Safari/537.36 Chrome-Lighthouse", BotClass.Monitor, "Lighthouse"),
        ("check_http/v2.3.3 (nagios-plugins 2.3.3)", BotClass.Monitor, "Nagios"),
        // Scanners.
        ("sqlmap/1.8.4#stable (https://sqlmap.org)", BotClass.Scanner, "sqlmap"),
        ("Mozilla/5.00 (Nikto/2.5.0) (Evasions:None) (Test:Port Check)", BotClass.Scanner, "Nikto"),
        ("Mozilla/5.0 (compatible; Nmap Scripting Engine; https://nmap.org/book/nse.html)", BotClass.Scanner, "Nmap"),
        ("Mozilla/5.0 zgrab/0.x", BotClass.Scanner, "zgrab"),
        ("Mozilla/5.0 (compatible; CensysInspect/1.1; +https://about.censys.io/)", BotClass.Scanner, "Censys"),
        ("Mozilla/5.0 (l9scan/2.0.4313e2437313e22353e2e313; +https://leakix.net)", BotClass.Scanner, "LeakIX"),
        ("Fuzz Faster U Fool v2.1.0", BotClass.Scanner, "ffuf"),
        ("Mozilla/5.0 (compatible; Nuclei - Open-source project (github.com/projectdiscovery/nuclei))", BotClass.Scanner, "Nuclei"),
        // Libraries.
        ("curl/8.5.0", BotClass.Library, "curl"),
        ("Wget/1.21.4", BotClass.Library, "Wget"),
        ("python-requests/2.32.3", BotClass.Library, "python-requests"),
        ("Go-http-client/1.1", BotClass.Library, "Go-http-client"),
        ("Go-http-client/2.0", BotClass.Library, "Go-http-client"),
        ("okhttp/4.12.0", BotClass.Library, "okhttp"),
        ("Java/17.0.9", BotClass.Library, "Java"),
        ("node-fetch/1.0 (+https://github.com/bitinn/node-fetch)", BotClass.Library, "node-fetch"),
        ("PostmanRuntime/7.43.0", BotClass.Library, "Postman"),
        ("aiohttp/3.10.5", BotClass.Library, "aiohttp"),
        // Anything else that says what it is.
        ("Mozilla/5.0 (compatible; SemrushBot/7~bl; +http://www.semrush.com/bot.html)", BotClass.Unknown, "SemrushBot"),
        ("Mozilla/5.0 (compatible; AhrefsBot/7.0; +http://ahrefs.com/robot/)", BotClass.Unknown, "AhrefsBot"),
        ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) HeadlessChrome/131.0.0.0 Safari/537.36", BotClass.Unknown, "HeadlessChrome"),
        ("Mozilla/5.0 (compatible; ExampleCrawler/1.4; +https://example.test/crawler)", BotClass.Unknown, "ExampleCrawler"),
        ("mybot/2.1", BotClass.Unknown, "mybot"),
        ("Mozilla/5.0 (compatible; bot)", BotClass.Unknown, "Unnamed bot"),
    ];

    public static TheoryData<string, BotClass, string> Bots
    {
        get
        {
            TheoryData<string, BotClass, string> data = [];
            foreach (var (userAgent, kind, name) in BotTable)
            {
                data.Add(userAgent, kind, name);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Bots))]
    public void ABotIsReadAsItsKindAndName(string userAgent, BotClass kind, string name)
    {
        var info = UserAgentClassifier.Classify(userAgent);

        Assert.Equal((kind, name, DeviceClass.Bot), (info.Bot, info.BotName, info.Device));
        Assert.True(info.IsBot);
    }

    [Fact]
    public void EveryKindOfBotIsInTheTable()
    {
        var covered = BotTable.Select(static row => row.Kind).Distinct().Order();

        Assert.Equal(Enum.GetValues<BotClass>().Where(static kind => kind != BotClass.None), covered);
    }

    [Fact]
    public void EveryAiCrawlerRobotsTxtBlocksIsAnAiCrawlerHere()
    {
        foreach (var crawler in Paduraksa.Core.Model.RobotsText.AiCrawlers)
        {
            var info = UserAgentClassifier.Classify($"Mozilla/5.0 (compatible; {crawler}/1.0)");

            Assert.Equal(BotClass.AiCrawler, info.Bot);
        }
    }

    [Fact]
    public void ABotKeyNamesItsKindAndItsNameAndParsesBack()
    {
        var info = UserAgentClassifier.Classify("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)");

        Assert.Equal("SearchEngine|Googlebot", info.BotKey);
        Assert.Equal((BotClass.SearchEngine, "Googlebot"), UserAgentInfo.ParseBotKey(info.BotKey!));
        Assert.Equal((BotClass.Unknown, "stray"), UserAgentInfo.ParseBotKey("stray"));
        Assert.Equal((BotClass.Unknown, "None|x"), UserAgentInfo.ParseBotKey("None|x"));
        Assert.Null(UserAgentClassifier.Classify("Mozilla/5.0 (Windows NT 10.0) Chrome/131.0").BotKey);
    }

    [Fact]
    public void ARequestWithNoUserAgentIsUnknownAndNotABot()
    {
        foreach (var missing in new string?[] { null, "" })
        {
            var info = UserAgentClassifier.Classify(missing);

            Assert.Equal(("Unknown", "Unknown", DeviceClass.Other, BotClass.None), (info.Browser, info.Os, info.Device, info.Bot));
        }
    }

    [Fact]
    public void ABrowserLabelCarriesTheMajorVersion()
    {
        Assert.Equal("Chrome 131", UserAgentClassifier.Classify(Chrome131Windows).BrowserLabel);
        Assert.Equal("Other", UserAgentClassifier.Classify("Mozilla/5.0 (X11; Linux) Konqueror").BrowserLabel);
    }

    [Fact]
    public void AnAgentIsReadOnlyUpToItsCap()
    {
        // A bot token past the cap is never looked at, so padding an agent doesn't change what it is or what it costs.
        var padded = Chrome131Windows + new string(' ', UserAgentClassifier.MaxLength) + " Googlebot/2.1";

        Assert.False(UserAgentClassifier.Classify(padded).IsBot);
    }

    [Theory]
    [InlineData("Mozilla/5.0 (compatible; Googlebot/2.1) GPTBot/1.2 curl/8", BotClass.AiCrawler, "GPTBot")]
    [InlineData("curl/8.5.0 Googlebot/2.1", BotClass.SearchEngine, "Googlebot")]
    [InlineData("SomeSpider/1.0 curl/8.5.0", BotClass.Library, "curl")]
    [InlineData("Mozilla/5.0 (X11) Abot/1.0 nikto", BotClass.Scanner, "Nikto")]
    public void TheFirstRuleInTableOrderWinsWhereverItsTokenSits(string userAgent, BotClass kind, string name)
    {
        var info = UserAgentClassifier.Classify(userAgent);

        Assert.Equal((kind, name), (info.Bot, info.BotName));
    }

    [Fact]
    public void AnAgentMadeOfTokensCostsABoundedAmountAndStillClassifies()
    {
        // Every character starts a token: the hits are capped, so this is as cheap as any other agent and the best rule found still wins.
        var info = UserAgentClassifier.Classify(string.Concat(Enumerable.Repeat("curlbot", 150)));

        Assert.Equal(BotClass.Library, info.Bot);
    }

    [Fact]
    public void AnAgentWithNoTokenAtAllReachesAlmostNoRuleComparison()
    {
        var agent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " + string.Concat(Enumerable.Range(0, 80).Select(static i => "Zq" + i + "xv/1.0 ")).PadRight(1_024, 'q');

        // A rule is compared only at a position the prefix filter passes, so an agent without a token costs a filter test a character and
        // a few collisions ("Win" in Windows starts WinHttp), however slow the machine. The nanoseconds a character are measured in the
        // benchmark project; this fixes the count, which the 150-rule scan it replaced would have made one per rule and position.
        Assert.InRange(PositionsAtWhichATokenCouldStart(agent), 0, agent.Length / 100);
        Assert.False(UserAgentClassifier.Classify(agent).IsBot);
        Assert.True(PositionsAtWhichATokenCouldStart("Mozilla/5.0 Googlebot/2.1") > 0);
    }

    private static int PositionsAtWhichATokenCouldStart(string agent)
    {
        var span = agent.AsSpan(0, Math.Min(agent.Length, UserAgentClassifier.MaxLength));
        var count = 0;
        for (var i = 0; i + 2 < span.Length; i++)
        {
            if (UserAgentClassifier.StartsAToken(span, i))
            {
                count++;
            }
        }

        return count;
    }

    [Fact]
    public void AnAgentPaddedPastTheCapPinsOnlyTheCapInTheCache()
    {
        UserAgentCache cache = new(capacity: 8);
        var head = "Mozilla/5.0 (Windows NT 10.0) Chrome/131.0.0.0 Safari/537.36 ";
        var first = head + new string('a', 1_000) + new string('x', 40_000);
        var second = head + new string('a', 1_000) + new string('y', 40_000);

        var one = cache.Get(first);
        var two = cache.Get(second);

        // The classifier reads 1 024 characters, so two agents that differ only past them are one entry (and the key is the capped copy).
        Assert.Same(one, two);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void WhenTheDrainIsBehindAnUnknownAgentIsNotClassifiedNorCachedButAKnownOneIs()
    {
        UserAgentCache cache = new(capacity: 8);
        var known = cache.Get("curl/8.5.0");

        var fresh = cache.Get("Mozilla/5.0 Chrome/131.0.0.0 Safari/537.36", classify: false);
        var again = cache.Get("curl/8.5.0", classify: false);

        Assert.Same(UserAgentInfo.Unclassified, fresh);
        Assert.Same(known, again);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void TheCacheKeepsTheMostRecentlyUsedAndNeverGrowsPastItsSize()
    {
        UserAgentCache cache = new(capacity: 3);

        var first = cache.Get("curl/1");
        cache.Get("curl/2");
        cache.Get("curl/3");
        cache.Get("curl/1");
        cache.Get("curl/4");

        Assert.Equal(3, cache.Count);
        Assert.Same(first, cache.Get("curl/1"));
        // curl/2 was the least recently used and went; asking again classifies it afresh.
        Assert.Equal(3, cache.Count);
        for (var i = 0; i < 1_000; i++)
        {
            cache.Get("agent/" + i);
        }

        Assert.Equal(3, cache.Count);
    }
}
