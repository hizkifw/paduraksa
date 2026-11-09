using Paduraksa.Application.Identity;

namespace Paduraksa.Tests.Unit.Identity;

public sealed class SetupBannerTests
{
    [Theory]
    [InlineData("tok", "/data/paduraksa/setup-token")]
    [InlineData("0Yb3n2m8k5Qw1rT7vX4zA9cD6eF3gH8jK2mN5pQ7sT1uV4wX", "/srv/docker/paduraksa/data/setup-token")]
    public void TheBoxIsAsWideAsItsLongestLineAndTheBordersLineUp(string token, string path)
    {
        string banner = SetupBanner.Render(token, path, unicode: true);
        string[] lines = banner.Split('\n');

        Assert.Equal(10, lines.Length); // a top border, eight content lines, a bottom border
        Assert.All(lines, line => Assert.Equal(lines[0].Length, line.Length));
        Assert.Equal(('╔', '╗'), (lines[0][0], lines[0][^1]));
        Assert.All(lines[1..^1], line => Assert.Equal(('║', '║'), (line[0], line[^1])));
        Assert.Equal(('╚', '╝'), (lines[^1][0], lines[^1][^1]));
        Assert.Contains(token, banner, StringComparison.Ordinal);
        Assert.Contains(path, banner, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBoxGrowsWithTheTokenAndWithThePath()
    {
        string longToken = new('x', 120);
        string longPath = "/a/" + new string('d', 120) + "/setup-token";

        int narrow = Width(SetupBanner.Render("tok", "/a/setup-token", unicode: true));
        int tokenWidth = Width(SetupBanner.Render(longToken, "/a/setup-token", unicode: true));
        int pathWidth = Width(SetupBanner.Render("tok", longPath, unicode: true));

        // Six columns of frame around the longest line.
        Assert.Equal("Setup token:  ".Length + longToken.Length + 6, tokenWidth);
        Assert.Equal($"Also stored in {longPath}. Reprint with:".Length + 6, pathWidth);
        Assert.True(tokenWidth > narrow);
        Assert.True(pathWidth > narrow);
        foreach (string banner in (string[])[SetupBanner.Render("tok", "/a/setup-token", unicode: true), SetupBanner.Render(longToken, "/a/setup-token", unicode: true), SetupBanner.Render("tok", longPath, unicode: true)])
        {
            Assert.All(banner.Split('\n'), line => Assert.Equal(Width(banner), line.Length));
        }
    }

    [Fact]
    public void AConsoleThatIsNotUtf8GetsPlainAsciiBorders()
    {
        string banner = SetupBanner.Render("tok", "/a/setup-token", unicode: false);
        string[] lines = banner.Split('\n');

        Assert.DoesNotContain(lines, line => line.IndexOfAny(['╔', '╗', '╚', '╝', '║', '═']) >= 0);
        Assert.Equal(('+', '+'), (lines[0][0], lines[0][^1]));
        Assert.All(lines[0][1..^1], c => Assert.Equal('-', c));
        Assert.All(lines[1..^1], line => Assert.Equal(('|', '|'), (line[0], line[^1])));
        Assert.Equal(('+', '+'), (lines[^1][0], lines[^1][^1]));
        Assert.All(lines, line => Assert.Equal(lines[0].Length, line.Length));
        Assert.Contains("Setup token:  tok", banner, StringComparison.Ordinal);
    }

    [Fact]
    public void ItIsOneStringWithNoCarriageReturns()
    {
        string banner = SetupBanner.Render("tok", "/a/setup-token", unicode: true);

        Assert.DoesNotContain('\r', banner);
        Assert.Contains("paduraksa setup-token", banner, StringComparison.Ordinal);
        Assert.Contains("It works once and is useless after setup completes.", banner, StringComparison.Ordinal);
    }

    private static int Width(string banner) => banner.Split('\n')[0].Length;
}
