using Paduraksa.Core.Model;

namespace Paduraksa.Tests.Unit.Model;

/// <summary>The managed texts both the proxy and the dashboard use, and the rules the form and the validator share.</summary>
public sealed class RobotsTextTests
{
    [Fact]
    public void DisallowAllIsTheOneGroupThatDisallowsEverything()
    {
        Assert.Equal("User-agent: *\nDisallow: /\n", RobotsText.DisallowAll);
    }

    [Fact]
    public void BlockAiCrawlersDisallowsEveryListedCrawlerAndAllowsTheRest()
    {
        var text = RobotsText.BlockAiCrawlers;
        Assert.StartsWith("# AI crawlers disallowed;", text, StringComparison.Ordinal);
        Assert.Contains("Disallow: /\n\nUser-agent: *\nDisallow:\n", text, StringComparison.Ordinal);

        var groups = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, groups.Length);
        var agents = groups[0].Split('\n').Where(line => line.StartsWith("User-agent: ", StringComparison.Ordinal)).Select(line => line["User-agent: ".Length..]).ToArray();
        Assert.Equal(RobotsText.AiCrawlers, agents);
        Assert.Contains("omgilibot", agents);
        Assert.Contains("Ai2Bot-Dolma", agents);
        Assert.Contains("Meta-WebIndexer", agents);
        Assert.Equal(agents.Length, agents.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("User-agent: *\r\nDisallow: /\r\n", "User-agent: *\nDisallow: /\n")]
    [InlineData("User-agent: *\rDisallow: /", "User-agent: *\nDisallow: /\n")]
    [InlineData("User-agent: *\nDisallow: /", "User-agent: *\nDisallow: /\n")]
    [InlineData("", "\n")]
    public void NormalizeCustomUsesLineFeedsAndEndsWithOne(string stored, string served)
    {
        Assert.Equal(served, RobotsText.NormalizeCustom(stored));
    }

    [Theory]
    [InlineData("User-agent: *\nDisallow: /", true)]
    [InlineData("a\tb\r\nc", true)]
    [InlineData("a\0b", false)]
    [InlineData("a\u0001b", false)]
    [InlineData("a\u007Fb", false)]
    public void ControlCharactersAreRefusedExceptTabCarriageReturnAndLineFeed(string text, bool valid)
    {
        Assert.Equal(valid, RobotsText.HasNoControlCharacters(text));
    }

    [Fact]
    public void TheSizeLimitCountsUtf8Bytes()
    {
        Assert.True(RobotsText.IsWithinSizeLimit(new string('a', RobotsText.MaxCustomBytes)));
        Assert.False(RobotsText.IsWithinSizeLimit(new string('a', RobotsText.MaxCustomBytes + 1)));
        Assert.False(RobotsText.IsWithinSizeLimit(new string('\u00e9', RobotsText.MaxCustomBytes / 2 + 1)));
    }

    [Theory]
    [InlineData("User-agent: *\nDisallow: /\n", "User-agent: *\nDisallow: /\nDisallow: /t/\n")]
    [InlineData("User-agent: a\nUser-agent: b\nDisallow: /x\n", "User-agent: a\nUser-agent: b\nDisallow: /x\nDisallow: /t/\n")]
    [InlineData("User-agent: a\nDisallow: /x\n\nUser-agent: b\nAllow: /\n", "User-agent: a\nDisallow: /x\nDisallow: /t/\n\nUser-agent: b\nAllow: /\nDisallow: /t/\n")]
    [InlineData("User-agent: a\nUser-agent: b\n", "User-agent: a\nUser-agent: b\nDisallow: /t/\n")]
    [InlineData("# only a comment\nSitemap: https://e.test/s.xml\n", "# only a comment\nSitemap: https://e.test/s.xml\n\nUser-agent: *\nDisallow: /t/\n")]
    [InlineData("\n", "\nUser-agent: *\nDisallow: /t/\n")]
    [InlineData("User-agent: a\nDisallow: /x # why\nSitemap: https://e.test/s.xml\n", "User-agent: a\nDisallow: /x # why\nDisallow: /t/\nSitemap: https://e.test/s.xml\n")]
    [InlineData("USER-AGENT: a\ncrawl-delay: 5\n\n\nUSER-AGENT: b\nDISALLOW: /\n", "USER-AGENT: a\ncrawl-delay: 5\nDisallow: /t/\n\n\nUSER-AGENT: b\nDISALLOW: /\nDisallow: /t/\n")]
    public void WithDisallowEndsEveryGroupWithThePathAndGivesAFileWithNoGroupOne(string text, string expected)
    {
        Assert.Equal(expected, RobotsText.WithDisallow(text, "/t/"));
    }

    [Fact]
    public void WithDisallowOnThePresetsAddsTheLineToEachGroup()
    {
        Assert.Equal("User-agent: *\nDisallow: /\nDisallow: /t/\n", RobotsText.WithDisallow(RobotsText.DisallowAll, "/t/"));

        var ai = RobotsText.WithDisallow(RobotsText.BlockAiCrawlers, "/t/");
        Assert.Contains("Disallow: /\nDisallow: /t/\n\nUser-agent: *\nDisallow:\nDisallow: /t/\n", ai, StringComparison.Ordinal);
        Assert.Equal(2, ai.Split("Disallow: /t/").Length - 1);
    }
}
