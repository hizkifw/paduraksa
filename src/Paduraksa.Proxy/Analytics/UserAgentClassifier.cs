using System.Buffers;
using System.Runtime.CompilerServices;

using Paduraksa.Core.Model;

namespace Paduraksa.Proxy.Analytics;

/// <summary>
/// Reads a <c>User-Agent</c> into a browser, an operating system, a device and, for an automated client, what kind of bot it is
/// (PLAN §7.4, analytics). <b>Our own rule tables rather than a library:</b> the questions are few, the answers are a handful of
/// families, and every rule sits in the one list below where it can be audited and extended in a line. A parsing library
/// would bring thousands of regexes (the very thing the rest of the proxy avoids), a dependency to track, and device models nobody
/// here asks about. The bot list is the part that goes stale, and like <see cref="RobotsText.AiCrawlers"/> it moves with a release.
/// </summary>
/// <remarks>
/// <para><b>Order is the rule.</b> A bot is looked for first (a crawler's agent names a browser too), in the order AI crawlers (the
/// list <c>robots.txt</c> blocks), search engines, social previews, monitors, scanners, libraries, then any agent that calls itself
/// a bot, crawler or spider. Browsers come after, from the most specific token (Edge's <c>Edg/</c> is in a Chrome agent) to the
/// least. Matching is a case-insensitive substring search on the first <see cref="MaxLength"/> characters, so an agent that is
/// padded to cost time costs the same as one that isn't.</para>
/// <para><b>Honest limits.</b> Brave and most Chromium forks send Chrome's agent and are counted as Chrome; iPadOS 13 and later sends
/// a Mac's, so an iPad is a desktop Mac; a scanner that sends a browser's agent is a browser. The classifier says what the header
/// claims, which is what an operator can act on.</para>
/// </remarks>
public static class UserAgentClassifier
{
    /// <summary>How much of an agent is read for a bot's token: bots name themselves late in a long agent.</summary>
    public const int MaxLength = 1_024;

    /// <summary>How much of an agent is read for its browser, system and device.</summary>
    public const int ClientReadLength = 400;

    private sealed record Rule(string Token, BotClass Class, string Name);

    // Search engines and the crawlers of the big platforms. Tokens are the product names the operators publish.
    private static readonly Rule[] SearchEngines =
    [
        new("Googlebot", BotClass.SearchEngine, "Googlebot"),
        new("Google-InspectionTool", BotClass.SearchEngine, "Google Search Console"),
        new("GoogleOther", BotClass.SearchEngine, "GoogleOther"),
        new("AdsBot-Google", BotClass.SearchEngine, "Google Ads"),
        new("Mediapartners-Google", BotClass.SearchEngine, "Google AdSense"),
        new("Storebot-Google", BotClass.SearchEngine, "Google Storebot"),
        new("FeedFetcher-Google", BotClass.SearchEngine, "Google Feedfetcher"),
        new("Google-Read-Aloud", BotClass.SearchEngine, "Google Read Aloud"),
        new("APIs-Google", BotClass.SearchEngine, "Google APIs"),
        new("bingbot", BotClass.SearchEngine, "Bingbot"),
        new("BingPreview", BotClass.SearchEngine, "Bing Preview"),
        new("msnbot", BotClass.SearchEngine, "MSNBot"),
        new("Slurp", BotClass.SearchEngine, "Yahoo Slurp"),
        new("DuckDuckBot", BotClass.SearchEngine, "DuckDuckBot"),
        new("Baiduspider", BotClass.SearchEngine, "Baiduspider"),
        new("YandexBot", BotClass.SearchEngine, "YandexBot"),
        new("YandexImages", BotClass.SearchEngine, "Yandex Images"),
        new("YandexMobileBot", BotClass.SearchEngine, "YandexMobileBot"),
        new("Applebot", BotClass.SearchEngine, "Applebot"),
        new("Sogou", BotClass.SearchEngine, "Sogou"),
        new("SeznamBot", BotClass.SearchEngine, "SeznamBot"),
        new("PetalBot", BotClass.SearchEngine, "PetalBot"),
        new("Qwantify", BotClass.SearchEngine, "Qwant"),
        new("Exabot", BotClass.SearchEngine, "Exabot"),
        new("MojeekBot", BotClass.SearchEngine, "MojeekBot"),
        new("coccocbot", BotClass.SearchEngine, "Coc Coc"),
        new("Yeti/", BotClass.SearchEngine, "Naver Yeti"),
        new("ia_archiver", BotClass.SearchEngine, "Internet Archive"),
        new("archive.org_bot", BotClass.SearchEngine, "Internet Archive"),
        new("360Spider", BotClass.SearchEngine, "360Spider"),
    ];

    private static readonly Rule[] SocialPreviews =
    [
        new("facebookexternalhit", BotClass.SocialPreview, "Facebook"),
        new("Facebot", BotClass.SocialPreview, "Facebook"),
        // Telegram's own agent says "like TwitterBot", so it has to be tried first.
        new("TelegramBot", BotClass.SocialPreview, "Telegram"),
        new("Twitterbot", BotClass.SocialPreview, "Twitter"),
        new("LinkedInBot", BotClass.SocialPreview, "LinkedIn"),
        new("Slackbot", BotClass.SocialPreview, "Slack"),
        new("Slack-ImgProxy", BotClass.SocialPreview, "Slack"),
        new("Discordbot", BotClass.SocialPreview, "Discord"),
        new("WhatsApp", BotClass.SocialPreview, "WhatsApp"),
        new("Pinterestbot", BotClass.SocialPreview, "Pinterest"),
        new("Pinterest/", BotClass.SocialPreview, "Pinterest"),
        new("redditbot", BotClass.SocialPreview, "Reddit"),
        new("SkypeUriPreview", BotClass.SocialPreview, "Skype"),
        new("Mastodon", BotClass.SocialPreview, "Mastodon"),
        new("Bluesky Cardyb", BotClass.SocialPreview, "Bluesky"),
        new("Embedly", BotClass.SocialPreview, "Embedly"),
        new("vkShare", BotClass.SocialPreview, "VK"),
        new("Iframely", BotClass.SocialPreview, "Iframely"),
        new("Synapse (bot", BotClass.SocialPreview, "Matrix Synapse"),
    ];

    private static readonly Rule[] Monitors =
    [
        new("UptimeRobot", BotClass.Monitor, "UptimeRobot"),
        new("Pingdom", BotClass.Monitor, "Pingdom"),
        new("StatusCake", BotClass.Monitor, "StatusCake"),
        new("Site24x7", BotClass.Monitor, "Site24x7"),
        new("BetterUptime", BotClass.Monitor, "Better Stack"),
        new("Better Stack", BotClass.Monitor, "Better Stack"),
        new("Uptime-Kuma", BotClass.Monitor, "Uptime Kuma"),
        new("UptimeKuma", BotClass.Monitor, "Uptime Kuma"),
        new("Datadog", BotClass.Monitor, "Datadog"),
        new("NewRelicPinger", BotClass.Monitor, "New Relic"),
        new("ZabbixAgent", BotClass.Monitor, "Zabbix"),
        new("check_http", BotClass.Monitor, "Nagios"),
        new("Nagios", BotClass.Monitor, "Nagios"),
        new("Blackbox Exporter", BotClass.Monitor, "Prometheus Blackbox"),
        new("HetrixTools", BotClass.Monitor, "HetrixTools"),
        new("Freshping", BotClass.Monitor, "Freshping"),
        new("Healthchecks.io", BotClass.Monitor, "Healthchecks.io"),
        new("Gatus", BotClass.Monitor, "Gatus"),
        new("Checkly", BotClass.Monitor, "Checkly"),
        new("kube-probe", BotClass.Monitor, "Kubernetes probe"),
        new("ELB-HealthChecker", BotClass.Monitor, "AWS load balancer"),
        new("GoogleHC", BotClass.Monitor, "Google load balancer"),
        new("Cloudflare-Healthchecks", BotClass.Monitor, "Cloudflare"),
        new("Cloudflare-Traffic-Manager", BotClass.Monitor, "Cloudflare"),
        new("Chrome-Lighthouse", BotClass.Monitor, "Lighthouse"),
        new("GTmetrix", BotClass.Monitor, "GTmetrix"),
        new("WebPageTest", BotClass.Monitor, "WebPageTest"),
        new("HTTP-Monitor", BotClass.Monitor, "HTTP monitor"),
    ];

    private static readonly Rule[] Scanners =
    [
        new("sqlmap", BotClass.Scanner, "sqlmap"),
        new("Nikto", BotClass.Scanner, "Nikto"),
        new("Nmap", BotClass.Scanner, "Nmap"),
        new("masscan", BotClass.Scanner, "masscan"),
        new("zgrab", BotClass.Scanner, "zgrab"),
        new("ZmEu", BotClass.Scanner, "ZmEu"),
        new("Nuclei", BotClass.Scanner, "Nuclei"),
        new("WPScan", BotClass.Scanner, "WPScan"),
        new("Acunetix", BotClass.Scanner, "Acunetix"),
        new("Nessus", BotClass.Scanner, "Nessus"),
        new("OpenVAS", BotClass.Scanner, "OpenVAS"),
        new("Qualys", BotClass.Scanner, "Qualys"),
        new("Burp", BotClass.Scanner, "Burp Suite"),
        new("dirbuster", BotClass.Scanner, "DirBuster"),
        new("gobuster", BotClass.Scanner, "gobuster"),
        new("feroxbuster", BotClass.Scanner, "feroxbuster"),
        new("Fuzz Faster U Fool", BotClass.Scanner, "ffuf"),
        new("Wfuzz", BotClass.Scanner, "Wfuzz"),
        new("Hydra", BotClass.Scanner, "Hydra"),
        new("Arachni", BotClass.Scanner, "Arachni"),
        new("w3af", BotClass.Scanner, "w3af"),
        new("CensysInspect", BotClass.Scanner, "Censys"),
        new("Expanse", BotClass.Scanner, "Expanse"),
        new("NetSystemsResearch", BotClass.Scanner, "NetSystems Research"),
        new("l9scan", BotClass.Scanner, "LeakIX"),
        new("l9explore", BotClass.Scanner, "LeakIX"),
        new("l9tcpid", BotClass.Scanner, "LeakIX"),
        new("InternetMeasurement", BotClass.Scanner, "Internet Measurement"),
        new("Palo Alto Networks", BotClass.Scanner, "Palo Alto Networks"),
        new("BinaryEdge", BotClass.Scanner, "BinaryEdge"),
        new("Netcraft", BotClass.Scanner, "Netcraft"),
    ];

    private static readonly Rule[] Libraries =
    [
        new("curl", BotClass.Library, "curl"),
        new("Wget", BotClass.Library, "Wget"),
        new("python-requests", BotClass.Library, "python-requests"),
        new("python-httpx", BotClass.Library, "python-httpx"),
        new("python-urllib", BotClass.Library, "python-urllib"),
        new("aiohttp", BotClass.Library, "aiohttp"),
        new("Go-http-client", BotClass.Library, "Go-http-client"),
        new("okhttp", BotClass.Library, "okhttp"),
        new("Java/", BotClass.Library, "Java"),
        new("Apache-HttpClient", BotClass.Library, "Apache HttpClient"),
        new("libwww-perl", BotClass.Library, "libwww-perl"),
        new("node-fetch", BotClass.Library, "node-fetch"),
        new("axios", BotClass.Library, "axios"),
        new("undici", BotClass.Library, "undici"),
        new("Dart/", BotClass.Library, "Dart"),
        new("Faraday", BotClass.Library, "Faraday"),
        new("RestSharp", BotClass.Library, "RestSharp"),
        new("PostmanRuntime", BotClass.Library, "Postman"),
        new("insomnia", BotClass.Library, "Insomnia"),
        new("HTTPie", BotClass.Library, "HTTPie"),
        new("reqwest", BotClass.Library, "reqwest"),
        new("Scrapy", BotClass.Library, "Scrapy"),
        new("GuzzleHttp", BotClass.Library, "Guzzle"),
        new("Deno/", BotClass.Library, "Deno"),
        new("Bun/", BotClass.Library, "Bun"),
        new("got (", BotClass.Library, "got"),
        new("superagent", BotClass.Library, "superagent"),
        new("Mechanize", BotClass.Library, "Mechanize"),
        new("PycURL", BotClass.Library, "PycURL"),
        new("WinHttp", BotClass.Library, "WinHTTP"),
    ];

    // Well-known crawlers that are none of the above; named because an operator recognises them, class Unknown because
    // nothing about them is a search engine, a monitor or a scanner.
    private static readonly Rule[] NamedOthers =
    [
        new("SemrushBot", BotClass.Unknown, "SemrushBot"),
        new("AhrefsBot", BotClass.Unknown, "AhrefsBot"),
        new("MJ12bot", BotClass.Unknown, "MJ12bot"),
        new("DotBot", BotClass.Unknown, "DotBot"),
        new("BLEXBot", BotClass.Unknown, "BLEXBot"),
        new("DataForSeoBot", BotClass.Unknown, "DataForSeoBot"),
        new("HeadlessChrome", BotClass.Unknown, "HeadlessChrome"),
        new("PhantomJS", BotClass.Unknown, "PhantomJS"),
    ];

    private static readonly string[] GenericBotWords = ["bot", "crawler", "spider", "scraper", "fetcher"];

    private static readonly Rule[] AiRules = AiCrawlerRules();

    private static readonly SearchValues<char> NameBreaks = SearchValues.Create(" ;()/,\"");

    private static Rule[] AiCrawlerRules()
    {
        var rules = new Rule[RobotsText.AiCrawlers.Count];
        for (var i = 0; i < rules.Length; i++)
        {
            rules[i] = new Rule(RobotsText.AiCrawlers[i], BotClass.AiCrawler, RobotsText.AiCrawlers[i]);
        }

        return rules;
    }

    /// <summary>Reads <paramref name="userAgent"/>; null or empty is a request that sent none.</summary>
    public static UserAgentInfo Classify(string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent))
        {
            return new UserAgentInfo("Unknown", 0, "Unknown", DeviceClass.Other, BotClass.None, null);
        }

        var ua = userAgent.AsSpan(0, Math.Min(userAgent.Length, MaxLength));
        if (FindBot(ua) is { } bot)
        {
            return new UserAgentInfo(string.Empty, 0, OperatingSystem(ua[..Math.Min(ua.Length, ClientReadLength)]).Name, DeviceClass.Bot, bot.Class, bot.Name) { BotKey = bot.Class + "|" + bot.Name };
        }

        // Browsers, systems and devices are in the first few hundred characters of any real agent (the longest the table knows is about 250);
        // padding past that is padding, and not scanning it keeps an agent of 1 KiB with nothing in it as cheap as a short one.
        ua = ua[..Math.Min(ua.Length, ClientReadLength)];
        var (browser, version) = Browser(ua);
        var (os, desktopOs) = OperatingSystem(ua);
        return new UserAgentInfo(browser, version, os, Device(ua, os, desktopOs), BotClass.None, null);
    }

    // Every rule of every table in the order they are tried, then the generic words: the first (lowest) index that occurs in an agent wins.
    private static readonly Rule[] AllBotRules = AllRules();

    private static readonly int FirstGenericRule = AllBotRules.Length - GenericBotWords.Length;

    // A bitmap of the first three characters (folded to lower case, hashed to 16 bits) of every token. A scan of the agent tests each position against
    // it in a few instructions and only a position that passes is compared with the rules that start there, so an agent with no token in it (the
    // cache's worst case, a flood of unique agents) costs about a nanosecond a character, where trying 150 tokens one by one took 400 microseconds
    // and a multi-string search about five nanoseconds a character. Every token has at least three characters, and they are all ASCII.
    private static readonly ulong[] PrefixBits = PrefixBitmap();

    private static ulong[] PrefixBitmap()
    {
        var bits = new ulong[1 << 10];
        foreach (var rule in AllBotRules)
        {
            var h = PrefixHash(Fold(rule.Token[0]), Fold(rule.Token[1]), Fold(rule.Token[2]));
            bits[h >> 6] |= 1UL << (h & 63);
        }

        return bits;
    }

    private static char Fold(char c) => (uint)(c - 'A') <= 'Z' - 'A' ? (char)(c + 32) : c;

    private static int PrefixHash(char a, char b, char c) => (((a * 131) + b) * 131 + c) & 0xFFFF;

    // The rules by the lower-case ASCII letter or character their token starts with, so a hit is resolved against a few rules and not all of them.
    private static readonly int[][] RulesByFirstChar = RulesByFirst();

    // Agents are attacker-chosen: past this many token hits in one agent the rest are not looked at (the best so far stands).
    private const int MaxTokenHits = 48;

    private static int[][] RulesByFirst()
    {
        var lists = new List<int>[128];
        for (var i = 0; i < AllBotRules.Length; i++)
        {
            var first = char.ToLowerInvariant(AllBotRules[i].Token[0]);
            (lists[first] ??= []).Add(i);
        }

        var byChar = new int[lists.Length][];
        for (var c = 0; c < lists.Length; c++)
        {
            byChar[c] = lists[c]?.ToArray() ?? [];
        }

        return byChar;
    }

    // The Proxy's rule: no LINQ, even off the request path.
    private static Rule[] AllRules()
    {
        List<Rule> rules = [.. AiRules, .. SearchEngines, .. SocialPreviews, .. Monitors, .. Scanners, .. NamedOthers, .. Libraries];
        foreach (var word in GenericBotWords)
        {
            rules.Add(new Rule(word, BotClass.Unknown, string.Empty));
        }

        return [.. rules];
    }

    private static string[] TokensOf(Rule[] rules)
    {
        var tokens = new string[rules.Length];
        for (var i = 0; i < tokens.Length; i++)
        {
            tokens[i] = rules[i].Token;
        }

        return tokens;
    }

    // The first rule in table order that occurs anywhere in the agent. Every position that starts a token is resolved to the rules that start
    // there; the lowest rule index wins, which is what trying the tables in order gave.
    private static (BotClass Class, string Name)? FindBot(ReadOnlySpan<char> ua)
    {
        var best = int.MaxValue;
        var bestAt = 0;
        var matches = 0;
        for (var i = 0; i + 2 < ua.Length && best > 0; i++)
        {
            if (!StartsAToken(ua, i))
            {
                continue;
            }

            var rest = ua[i..];
            foreach (var index in RulesFor(rest[0]))
            {
                if (rest.StartsWith(AllBotRules[index].Token, StringComparison.OrdinalIgnoreCase))
                {
                    // Agents are attacker-chosen: past this many matches the rest are not looked at (the best so far stands).
                    matches++;
                    if (index < best)
                    {
                        best = index;
                        bestAt = i;
                    }
                }
            }

            if (matches >= MaxTokenHits)
            {
                break;
            }
        }

        if (best == int.MaxValue)
        {
            return null;
        }

        var rule = AllBotRules[best];
        return best >= FirstGenericRule ? (BotClass.Unknown, ProductAround(ua, bestAt, rule.Token.Length)) : (rule.Class, rule.Name);
    }

    // The only gate in front of a rule comparison: a position that fails it costs no comparison, which is what makes an agent with no token a cheap miss.
    // Internal so a test can count the positions that pass instead of timing the scan.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool StartsAToken(ReadOnlySpan<char> ua, int i)
    {
        var h = PrefixHash(Fold(ua[i]), Fold(ua[i + 1]), Fold(ua[i + 2]));
        return (PrefixBits[h >> 6] & (1UL << (h & 63))) != 0;
    }

    private static ReadOnlySpan<int> RulesFor(char c)
    {
        var lower = char.ToLowerInvariant(c);
        return lower < RulesByFirstChar.Length ? RulesByFirstChar[lower] : [];
    }

    // The product token a generic word sits in ("SomeBot/1.2" -> "SomeBot"), so an unknown crawler is counted under its own name.
    private static string ProductAround(ReadOnlySpan<char> ua, int at, int length)
    {
        var start = at;
        while (start > 0 && !NameBreaks.Contains(ua[start - 1]))
        {
            start--;
        }

        var end = at + length;
        while (end < ua.Length && !NameBreaks.Contains(ua[end]))
        {
            end++;
        }

        var name = ua[start..end];
        if (name.Length > 40)
        {
            name = name[..40];
        }

        return name.Length == length ? "Unnamed bot" : name.ToString();
    }

    private static (string Name, int Version) Browser(ReadOnlySpan<char> ua)
    {
        // Most specific first: a Chromium fork's agent also says Chrome and Safari.
        if (First(VersionAfter(ua, "Edg/"), VersionAfter(ua, "EdgA/"), VersionAfter(ua, "EdgiOS/"), VersionAfter(ua, "Edge/")) is var edge and > 0)
        {
            return ("Edge", edge);
        }

        if (First(VersionAfter(ua, "OPR/"), VersionAfter(ua, "OPT/")) is var opera and > 0)
        {
            return ("Opera", opera);
        }

        if (ua.Contains("Opera", StringComparison.Ordinal))
        {
            return ("Opera", First(VersionAfter(ua, "Version/"), VersionAfter(ua, "Opera/")));
        }

        foreach (var (token, name) in Forks)
        {
            if (VersionAfter(ua, token) is { } found)
            {
                return (name, found);
            }
        }

        if (First(VersionAfter(ua, "Firefox/"), VersionAfter(ua, "FxiOS/")) is var firefox and > 0)
        {
            return ("Firefox", firefox);
        }

        if (VersionAfter(ua, "CriOS/") is { } chromeIos)
        {
            return ("Chrome", chromeIos);
        }

        if (VersionAfter(ua, "Chromium/") is { } chromium)
        {
            return ("Chromium", chromium);
        }

        if (VersionAfter(ua, "Chrome/") is { } chrome)
        {
            return (ua.Contains("; wv)", StringComparison.Ordinal) ? "Android WebView" : "Chrome", chrome);
        }

        if (ua.Contains("Safari/", StringComparison.Ordinal) && (ua.Contains("Version/", StringComparison.Ordinal) || ua.Contains("Mac OS X", StringComparison.Ordinal)))
        {
            return ("Safari", VersionAfter(ua, "Version/") ?? 0);
        }

        if (ua.Contains("MSIE ", StringComparison.Ordinal) || ua.Contains("Trident/", StringComparison.Ordinal))
        {
            return ("Internet Explorer", VersionAfter(ua, "MSIE ") ?? VersionAfter(ua, "rv:") ?? 0);
        }

        return ("Other", 0);
    }

    // Browsers whose agents are another engine's plus one token of their own.
    private static readonly (string Token, string Name)[] Forks =
    [
        ("SamsungBrowser/", "Samsung Internet"),
        ("Vivaldi/", "Vivaldi"),
        ("YaBrowser/", "Yandex Browser"),
        ("UCBrowser/", "UC Browser"),
        ("DuckDuckGo/", "DuckDuckGo"),
        ("Ddg/", "DuckDuckGo"),
    ];

    private static int First(params ReadOnlySpan<int?> versions)
    {
        foreach (var version in versions)
        {
            if (version is > 0)
            {
                return version.Value;
            }
        }

        return 0;
    }

    private static int? VersionAfter(ReadOnlySpan<char> ua, string token)
    {
        var at = ua.IndexOf(token, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var digits = ua[(at + token.Length)..];
        var length = 0;
        while (length < digits.Length && length < 9 && char.IsAsciiDigit(digits[length]))
        {
            length++;
        }

        return length == 0 ? 0 : int.Parse(digits[..length], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static (string Name, bool Desktop) OperatingSystem(ReadOnlySpan<char> ua)
    {
        if (ua.Contains("Windows Phone", StringComparison.Ordinal))
        {
            return ("Windows Phone", false);
        }

        if (ua.Contains("Windows", StringComparison.Ordinal))
        {
            return ("Windows", true);
        }

        if (ua.Contains("iPhone", StringComparison.Ordinal) || ua.Contains("iPod", StringComparison.Ordinal))
        {
            return ("iOS", false);
        }

        if (ua.Contains("iPad", StringComparison.Ordinal))
        {
            return ("iPadOS", false);
        }

        // Android's agent also says Linux, so it comes first.
        if (ua.Contains("Android", StringComparison.Ordinal))
        {
            return ("Android", false);
        }

        if (ua.Contains("CrOS", StringComparison.Ordinal))
        {
            return ("ChromeOS", true);
        }

        if (ua.Contains("Mac OS X", StringComparison.Ordinal) || ua.Contains("Macintosh", StringComparison.Ordinal))
        {
            return ("macOS", true);
        }

        if (ua.Contains("Linux", StringComparison.Ordinal) || ua.Contains("X11", StringComparison.Ordinal) || ua.Contains("Ubuntu", StringComparison.Ordinal))
        {
            return ("Linux", true);
        }

        if (ua.Contains("BSD", StringComparison.Ordinal))
        {
            return ("BSD", true);
        }

        return ("Other", false);
    }

    private static DeviceClass Device(ReadOnlySpan<char> ua, string os, bool desktopOs)
    {
        if (ua.ContainsAny(TelevisionsAndConsoles))
        {
            return DeviceClass.Other;
        }

        if (os == "iPadOS" || ua.Contains("Tablet", StringComparison.OrdinalIgnoreCase) || ua.Contains("Kindle", StringComparison.Ordinal) || ua.Contains("Silk/", StringComparison.Ordinal))
        {
            return DeviceClass.Tablet;
        }

        // An Android phone says Mobile, an Android tablet doesn't.
        if (os is "iOS" or "Windows Phone" || ua.Contains("Mobile", StringComparison.Ordinal) || ua.Contains("BlackBerry", StringComparison.Ordinal))
        {
            return DeviceClass.Mobile;
        }

        if (os == "Android")
        {
            return DeviceClass.Tablet;
        }

        return desktopOs ? DeviceClass.Desktop : DeviceClass.Other;
    }

    private static readonly SearchValues<string> TelevisionsAndConsoles = SearchValues.Create(
        ["SmartTV", "SMART-TV", "Tizen", "Web0S", "WebOS", "AppleTV", "PlayStation", "Xbox", "Nintendo", "CrKey", "Roku", "HbbTV", "BRAVIA", "AFTM", "AFTT"], StringComparison.OrdinalIgnoreCase);
}
