using Microsoft.Extensions.Primitives;

using Paduraksa.Proxy.Http;
using Paduraksa.Tests.Proxy.Testing;

namespace Paduraksa.Tests.Proxy.Http;

/// <summary>How the proxy reads <c>Accept</c> for its own pages.</summary>
public sealed class MediaTypesTests
{
    [Theory]
    [InlineData("text/html", true)]
    [InlineData("TEXT/HTML", true)]
    [InlineData("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8", true)]
    [InlineData("text/html;level=1;q=0.5", true)]
    [InlineData("text/html;q=0.001", true)]
    [InlineData("text/html;q=abc", true)]
    [InlineData("text/html;q=0", false)]
    [InlineData("text/html;q=0.000", false)]
    [InlineData("text/html; q = 0.0", false)]
    [InlineData("text/html;q=0.0000,text/html;q=1", true)]
    [InlineData("text/html;q=0;level=1", false)]
    [InlineData("*/*", false)]
    [InlineData("application/json", false)]
    [InlineData("application/json, */*;q=0.8", false)]
    public void OneHeaderLineSelectsHtmlUnlessItsQvalueIsZero(string accept, bool expected) =>
        Assert.Equal(expected, MediaTypes.AcceptsHtml(new StringValues(accept)));

    [Fact]
    public void SeveralHeaderLinesAreAllRead()
    {
        Assert.True(MediaTypes.AcceptsHtml(new StringValues(["application/json", "text/html;q=0.5"])));
        Assert.False(MediaTypes.AcceptsHtml(new StringValues(["application/json", "text/plain"])));
        Assert.True(MediaTypes.AcceptsHtml(new StringValues(["text/html;q=0", "TEXT/HTML"])));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(";")]
    [InlineData(",")]
    [InlineData("text")]
    [InlineData("text/")]
    [InlineData("text/html/thing")]
    [InlineData("application/json;q=0")]
    public void JunkDoesNotSelectHtml(string accept) =>
        Assert.False(MediaTypes.AcceptsHtml(new StringValues(accept)));

    [Fact]
    public void ParsingAllocatesNothing()
    {
        var accept = new StringValues("text/html,application/xhtml+xml;q=0.9,text/plain;q=0,application/json");

        AllocAssert.Zero(accept, static value => _ = MediaTypes.AcceptsHtml(value), "MediaTypes.AcceptsHtml");
    }
}
