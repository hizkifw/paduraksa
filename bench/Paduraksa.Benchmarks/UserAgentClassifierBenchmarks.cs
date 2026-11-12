using BenchmarkDotNet.Attributes;

using Paduraksa.Proxy.Analytics;

namespace Paduraksa.Benchmarks;

/// <summary>
/// <see cref="UserAgentClassifier.Classify"/> on what a cache miss costs: a browser's agent, a bot that matches early, an agent of 1 KiB with no
/// token in it (the worst case, and what a flood of unique agents sends) and one that is nothing but tokens. And a robots.txt of 1 000 wildcard
/// rules against a path at the analytics' cap, which is what a bot's entry costs on a host with a large custom file.
/// </summary>
[MemoryDiagnoser]
public class UserAgentClassifierBenchmarks
{
    private const string Chrome = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private readonly string _chrome = Chrome;
    private readonly string _early = "Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko; compatible; GPTBot/1.2; +https://openai.com/gptbot)";
    private string _noMatch = null!;
    private string _tokens = null!;
    private RobotsRules.Applicable _wildcards = null!;
    private string _path = null!;

    [GlobalSetup]
    public void Setup()
    {
        _noMatch = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Zqxv/1.0 ").PadRight(1_024, 'q');
        _tokens = string.Concat(Enumerable.Repeat("curlbot", 150));
        System.Text.StringBuilder file = new("User-agent: *\n");
        for (var i = 0; i < 1_000; i++)
        {
            file.Append("Disallow: /a*b").Append(i).Append("*c$\n");
        }

        _wildcards = RobotsRules.Parse(file.ToString()).Resolve("SemrushBot/7");
        _path = "/" + new string('a', TrafficBucket.MaxKeyLength - 1);
    }

    [Benchmark(Baseline = true)]
    public UserAgentInfo BrowserAgent() => UserAgentClassifier.Classify(_chrome);

    [Benchmark]
    public UserAgentInfo BotThatMatchesEarly() => UserAgentClassifier.Classify(_early);

    [Benchmark]
    public UserAgentInfo OneKiBWithNoToken() => UserAgentClassifier.Classify(_noMatch);

    [Benchmark]
    public UserAgentInfo OneKiBOfNothingButTokens() => UserAgentClassifier.Classify(_tokens);

    [Benchmark]
    public bool ThousandWildcardRulesOnACappedPath() => _wildcards.IsAllowed(_path);
}
