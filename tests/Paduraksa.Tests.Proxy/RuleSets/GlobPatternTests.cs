using Paduraksa.Core.Model;
using Paduraksa.Proxy.RuleSets;

namespace Paduraksa.Tests.Proxy.RuleSets;

public sealed class GlobPatternTests
{
    [Theory]
    [InlineData("/wp-admin", "/wp-admin", true)]
    [InlineData("/wp-admin", "/WP-Admin", true)]
    [InlineData("/wp-admin", "/wp-admin/", false)]
    [InlineData("/wp-admin", "/wp-admin/x", false)]
    [InlineData("/*.php", "/index.php", true)]
    [InlineData("/*.php", "/INDEX.PHP", true)]
    [InlineData("/*.php", "/a/index.php", false)]
    [InlineData("/*.php", "/.php", true)]
    [InlineData("/*.php", "/index.phpx", false)]
    [InlineData("/**.php", "/a/b/index.php", true)]
    [InlineData("/**.php", "/index.php", true)]
    [InlineData("/wp-*/**", "/wp-content/plugins/x.php", true)]
    [InlineData("/wp-*/**", "/wp-content/", true)]
    [InlineData("/wp-*/**", "/wp-content", false)]
    [InlineData("/wp-*/**", "/wp-/a", true)]
    [InlineData("/wp-*/**", "/other/a", false)]
    [InlineData("/**", "/", true)]
    [InlineData("/**", "/a/b/c", true)]
    [InlineData("/**", "/a/b/c/", true)]
    [InlineData("/*", "/", true)]
    [InlineData("/*", "/a", true)]
    [InlineData("/*", "/a/", false)]
    [InlineData("/*/", "/a/", true)]
    [InlineData("/*/*", "/a/b", true)]
    [InlineData("/*/*", "/a/b/c", false)]
    [InlineData("/a/*/c", "/a/b/c", true)]
    [InlineData("/a/*/c", "/a//c", true)]
    [InlineData("/a/*/c", "/a/b/d/c", false)]
    public void GlobsMatchTheWholePathIgnoringCase(string pattern, string path, bool expected) =>
        Assert.Equal(expected, Compile(pattern).IsMatch(path));

    [Theory]
    [InlineData("/**/.env", "/.env", true)]
    [InlineData("/**/.env", "/a/.env", true)]
    [InlineData("/**/.env", "/a/b/.env", true)]
    [InlineData("/**/.env", "/a/b/.envx", false)]
    [InlineData("/**/.env", "/a.env", false)]
    [InlineData("/**/.env", "/.ENV", true)]
    [InlineData("/a/**/b", "/a/b", true)]
    [InlineData("/a/**/b", "/a/x/y/b", true)]
    [InlineData("/a/**/b", "/ab", false)]
    [InlineData("/a/**/b", "/a/xb", false)]
    [InlineData("/**/*.php", "/x.php", true)]
    [InlineData("/**/*.php", "/a/b/x.php", true)]
    [InlineData("/**/*.php", "/a/b/x.txt", false)]
    [InlineData("/**/*/x", "/a/x", true)]
    [InlineData("/**/**/x", "/x", true)]
    [InlineData("/**/**/x", "/a/x", true)]
    [InlineData("/**/**/x", "/a/b/x", true)]
    [InlineData("/**/**/x", "/ax", false)]
    [InlineData("/**/**/**/x", "/x", true)]
    [InlineData("/**/**/**/x", "/a/b/c/x", true)]
    [InlineData("/**/**/**/x", "/a/b/c/y", false)]
    [InlineData("/a/**/**/**/x", "/a/x", true)]
    public void ASlashDoubleStarSlashSegmentMatchesNoneOrMoreSegments(string pattern, string path, bool expected) =>
        Assert.Equal(expected, Compile(pattern).IsMatch(path));

    [Fact]
    public void ACharacterAboveAsciiMatchesOnlyAWildcard()
    {
        Assert.True(Compile("/*").IsMatch("/café"));
        Assert.True(Compile("/**").IsMatch("/é/è"));
        Assert.False(Compile("/caf").IsMatch("/café"));
        Assert.False(Compile("/a*b").IsMatch("/aé"));
    }

    [Fact]
    public void ARunOfStarsIsOneWildcardAndTheEmptyPathMatchesNothingButARootGlob()
    {
        Assert.True(Compile("/a***b").IsMatch("/a/x/b"));
        Assert.False(Compile("/a").IsMatch(string.Empty));
        Assert.False(Compile("/**").IsMatch(string.Empty));
    }

    [Fact]
    public void APathShapedToBacktrackCostsNoMoreThanAnyOther()
    {
        // A backtracking matcher takes quadratic time or worse on this; the automaton reads each character once.
        var glob = Compile("/*a*a*a*a*a*a*a*a*a*a*a*a*b");
        var path = "/" + new string('a', 8000);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 100; i++)
        {
            Assert.False(glob.IsMatch(path));
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"took {watch.Elapsed}");
    }

    [Theory]
    [InlineData("", "starts with '/'")]
    [InlineData("admin", "starts with '/'")]
    [InlineData("/café", "ASCII")]
    [InlineData("/a\tb", "ASCII")]
    public void ABadGlobSaysWhy(string pattern, string reason)
    {
        Assert.False(GlobPattern.TryCompile(pattern, out var glob, out var error));
        Assert.Null(glob);
        Assert.Contains(reason, error, StringComparison.Ordinal);
    }

    [Fact]
    public void AGlobHasAtMostTheLimitOfTokens()
    {
        Assert.True(GlobPattern.TryCompile("/" + new string('a', RuleSetLimits.MaxGlobTokens - 1), out _, out _));
        Assert.False(GlobPattern.TryCompile("/" + new string('a', RuleSetLimits.MaxGlobTokens), out _, out var error));
        Assert.Contains(RuleSetLimits.MaxGlobTokens.ToString(System.Globalization.CultureInfo.InvariantCulture), error, StringComparison.Ordinal);

        // The last token's state is the 127th bit: the longest glob still matches its own text.
        var longest = "/" + new string('a', RuleSetLimits.MaxGlobTokens - 1);
        Assert.True(Compile(longest).IsMatch(longest));
    }

    private static GlobPattern Compile(string pattern)
    {
        Assert.True(GlobPattern.TryCompile(pattern, out var glob, out var error), error);
        return glob!;
    }
}
