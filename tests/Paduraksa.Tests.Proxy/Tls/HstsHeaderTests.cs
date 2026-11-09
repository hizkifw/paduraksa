using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Tls;
using Paduraksa.Tests.Proxy.Testing;

namespace Paduraksa.Tests.Proxy.Tls;

/// <summary>The HSTS header itself (PLAN §14 M6): what a value looks like, when it is sent, and the names it never goes to.</summary>
public sealed class HstsHeaderTests
{
    [Fact]
    public void BuildsTheValueFromTheSpec()
    {
        Assert.Equal("max-age=31536000", HstsHeader.ValueOf(new HostHstsSpec { Enabled = true }).ToString());
        Assert.Equal("max-age=0", HstsHeader.ValueOf(new HostHstsSpec { Enabled = true, MaxAgeSeconds = 0 }).ToString());
        Assert.Equal(
            "max-age=63072000; includeSubDomains; preload",
            HstsHeader.ValueOf(new HostHstsSpec { Enabled = true, MaxAgeSeconds = 63_072_000, IncludeSubDomains = true, Preload = true }).ToString());
        Assert.Equal("max-age=86400; includeSubDomains", HstsHeader.ValueOf(new HostHstsSpec { Enabled = true, MaxAgeSeconds = 86_400, IncludeSubDomains = true }).ToString());

        // Off sends nothing at all, whatever the rest says.
        Assert.True(HstsHeader.ValueOf(HostHstsSpec.Off).Count == 0);
        Assert.True(HstsHeader.ValueOf(new HostHstsSpec { MaxAgeSeconds = 300, IncludeSubDomains = true }).Count == 0);
    }

    [Theory]
    [InlineData("app.example.com", false)]
    [InlineData("*.app.example.com", false)]
    [InlineData("xn--bcher-kva.example", false)]
    [InlineData("a1.test", false)]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("app.localhost", true)]
    [InlineData("deep.app.localhost", true)]
    [InlineData("localhost.test", false)] // not under .localhost
    [InlineData("mylocalhost", false)]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.0.0.1", true)] // every IP literal, not only a loopback one
    [InlineData("192.0.2.10", true)]
    [InlineData("127.1", true)] // the spellings a browser fills out
    [InlineData("127.0.1", true)]
    [InlineData("0177.0.0.1", true)]
    [InlineData("1.2.3", true)]
    [InlineData("1.2.3.4.5", true)]
    [InlineData("1234.0.0.1", true)]
    [InlineData("2130706433", true)]
    [InlineData("0x7f.1", true)]
    [InlineData("0X7F000001", true)]
    [InlineData("0x7f.0.0.1", true)]
    [InlineData("[::1]", true)]
    [InlineData("[2001:db8::10]", true)]
    [InlineData("::1", false)] // not a Host header's spelling: only the bracketed form is
    [InlineData("1.2.3.a", false)] // one label isn't a number: a DNS name
    [InlineData("example.123", false)]
    [InlineData("0x", false)]
    [InlineData("0xzz", false)]
    [InlineData("3com.com", false)]
    [InlineData("", true)]
    public void KnowsTheNamesHstsNeverGoesTo(string name, bool exempt) =>
        Assert.Equal(exempt, HstsHeader.IsExemptName(name));

    [Fact]
    public void SetsTheHeaderOnlyWhenEveryConditionHolds()
    {
        StringValues value = new("max-age=31536000");

        Assert.Equal(HstsDecision.Sent, Apply("app.example.com", https: true, value, out DefaultHttpContext sent));
        Assert.Equal("max-age=31536000", sent.Response.Headers.StrictTransportSecurity.ToString());

        Assert.Equal(HstsDecision.PlainHttp, Apply("app.example.com", https: false, value, out DefaultHttpContext plain));
        Assert.False(plain.Response.Headers.ContainsKey("Strict-Transport-Security"));

        Assert.Equal(HstsDecision.ExemptName, Apply("localhost", https: true, value, out DefaultHttpContext loopback));
        Assert.False(loopback.Response.Headers.ContainsKey("Strict-Transport-Security"));

        Assert.Equal(HstsDecision.ExemptName, Apply("127.0.0.1", https: true, value, out DefaultHttpContext literal));
        Assert.False(literal.Response.Headers.ContainsKey("Strict-Transport-Security"));

        // No host value (HSTS off): nothing of ours, whatever the request.
        DefaultHttpContext off = TestContexts.Create("app.example.com", "/", scheme: "https");
        Assert.Equal(HstsDecision.Off, HstsHeader.Apply(off, StringValues.Empty, true, "app.example.com"));
        Assert.False(off.Response.Headers.ContainsKey("Strict-Transport-Security"));
    }

    [Fact]
    public void ApplyingItAllocatesNothing() =>
        AllocAssert.Zero(
            TestContexts.Create("app.example.com", "/", scheme: "https"),
            static context => HstsHeader.Apply(context, new StringValues("max-age=31536000; includeSubDomains"), true, "app.example.com"),
            "HSTS apply");

    private static HstsDecision Apply(string name, bool https, StringValues value, out DefaultHttpContext context)
    {
        context = TestContexts.Create(name, "/", scheme: https ? "https" : "http");
        return HstsHeader.Apply(context, value, https, name);
    }
}
