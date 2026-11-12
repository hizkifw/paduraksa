using Paduraksa.Core.Model;
using Paduraksa.Proxy.Analytics;

namespace Paduraksa.Tests.Proxy.Analytics;

/// <summary>Reading a served <c>robots.txt</c> back into rules, the way RFC 9309 says a crawler does.</summary>
public sealed class RobotsRulesTests
{
    [Fact]
    public void DisallowAllBlocksEveryCrawlerExceptTheRobotsFileItself()
    {
        var rules = RobotsRules.Parse(RobotsText.DisallowAll);

        Assert.False(rules.IsAllowed("Googlebot/2.1", "/"));
        Assert.False(rules.IsAllowed("anything", "/a/b"));
        Assert.True(rules.IsAllowed("Googlebot/2.1", "/robots.txt"));
    }

    [Fact]
    public void BlockAiCrawlersBlocksTheListedOnesAndNobodyElse()
    {
        var rules = RobotsText.BlockAiCrawlers is { } text ? RobotsRules.Parse(text) : null!;

        Assert.False(rules.IsAllowed("Mozilla/5.0 (compatible; GPTBot/1.2; +https://openai.com/gptbot)", "/page"));
        Assert.False(rules.IsAllowed("ClaudeBot/1.0", "/"));
        Assert.True(rules.IsAllowed("Mozilla/5.0 (compatible; Googlebot/2.1)", "/page"));
        Assert.True(rules.IsAllowed("Mozilla/5.0 (Windows NT 10.0) Chrome/131.0", "/page"));
        foreach (var crawler in RobotsText.AiCrawlers)
        {
            Assert.False(rules.IsAllowed(crawler + "/1.0", "/x"), crawler);
        }
    }

    [Fact]
    public void TheLongestMatchingRuleWinsAndAllowBeatsDisallowOnATie()
    {
        var rules = RobotsRules.Parse("User-agent: *\nDisallow: /private\nAllow: /private/open\nDisallow: /tie\nAllow: /tie\n");

        Assert.False(rules.IsAllowed("bot", "/private/secret"));
        Assert.True(rules.IsAllowed("bot", "/private/open/page"));
        Assert.True(rules.IsAllowed("bot", "/tie"));
        Assert.True(rules.IsAllowed("bot", "/public"));
    }

    [Fact]
    public void AGroupNamingTheCrawlerReplacesTheStarGroup()
    {
        var rules = RobotsRules.Parse("User-agent: *\nDisallow: /\n\nUser-agent: Googlebot\nDisallow: /admin\n");

        Assert.True(rules.IsAllowed("Mozilla/5.0 (compatible; Googlebot/2.1)", "/page"));
        Assert.False(rules.IsAllowed("Mozilla/5.0 (compatible; Googlebot/2.1)", "/admin/x"));
        Assert.False(rules.IsAllowed("somebot", "/page"));
    }

    [Fact]
    public void ConsecutiveAgentLinesShareTheRulesThatFollow()
    {
        var rules = RobotsRules.Parse("User-agent: AhrefsBot\nUser-agent: SemrushBot\nDisallow: /\n");

        Assert.False(rules.IsAllowed("SemrushBot/7", "/"));
        Assert.False(rules.IsAllowed("AhrefsBot/7", "/"));
        Assert.True(rules.IsAllowed("Googlebot", "/"));
    }

    [Theory]
    [InlineData("Disallow: /*.pdf$", "/files/a.pdf", false)]
    [InlineData("Disallow: /*.pdf$", "/files/a.pdf?x=1", true)]
    [InlineData("Disallow: /*.pdf$", "/files/a.pdfx", true)]
    [InlineData("Disallow: /a*b*c", "/aXXbYYc", false)]
    [InlineData("Disallow: /a*b*c", "/aXXcYYb", true)]
    [InlineData("Disallow: /tmp/*", "/tmp/", false)]
    [InlineData("Disallow: /exact$", "/exact", false)]
    [InlineData("Disallow: /exact$", "/exact/more", true)]
    [InlineData("Disallow:", "/anything", true)]
    public void PatternsUseStarAndTrailingDollar(string rule, string path, bool allowed)
    {
        var rules = RobotsRules.Parse("User-agent: *\n" + rule + "\n");

        Assert.Equal(allowed, rules.IsAllowed("bot", path));
    }

    [Fact]
    public void CommentsBlankLinesUnknownDirectivesAndCarriageReturnsAreSkipped()
    {
        var rules = RobotsRules.Parse("# a comment\r\nUser-agent: * # everyone\r\nCrawl-delay: 10\r\nSitemap: https://a.test/s.xml\r\n\r\nDisallow: /x # nope\r\n");

        Assert.False(rules.IsAllowed("bot", "/x/y"));
        Assert.True(rules.IsAllowed("bot", "/y"));
    }

    [Fact]
    public void TextWithoutGroupsDisallowsNothing()
    {
        Assert.True(RobotsRules.Parse("").IsAllowed("bot", "/x"));
        Assert.True(RobotsRules.Parse("Disallow: /x\n").IsAllowed("bot", "/x"));
        Assert.True(RobotsRules.Empty.IsAllowed("bot", "/x"));
    }

    [Fact]
    public void AnAgentsRulesAreResolvedOnceAndAskedAboutManyPaths()
    {
        var rules = RobotsRules.Parse("User-agent: *\nDisallow: /\n\nUser-agent: Googlebot\nDisallow: /admin\n");

        var google = rules.Resolve("Mozilla/5.0 (compatible; Googlebot/2.1)");
        var other = rules.Resolve("somebot");

        Assert.True(google.IsAllowed("/page"));
        Assert.False(google.IsAllowed("/admin/x"));
        Assert.False(other.IsAllowed("/page"));
        Assert.True(other.IsAllowed("/robots.txt"));
    }

    [Fact]
    public void ManyStarsInAPatternCostNothingExtra()
    {
        var rules = RobotsRules.Parse("User-agent: *\nDisallow: /" + string.Concat(Enumerable.Repeat("a*", 200)) + "b\n");

        // A path of only a's can never match the final b; a backtracking matcher would take exponential time on it.
        Assert.True(rules.IsAllowed("bot", "/" + new string('a', 250)));
    }
}
