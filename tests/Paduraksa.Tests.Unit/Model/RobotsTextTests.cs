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
}
