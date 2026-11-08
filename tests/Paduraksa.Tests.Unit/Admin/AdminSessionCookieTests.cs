using Microsoft.AspNetCore.Http;

using Paduraksa.Admin.Auth;

namespace Paduraksa.Tests.Unit.Admin;

public sealed class AdminSessionCookieTests
{
    private static (bool Present, bool Ambiguous, string? Value) Read(params string[] cookieHeaders)
    {
        DefaultHttpContext context = new();
        context.Request.Headers.Cookie = cookieHeaders;
        return AdminSessionCookie.Read(context.Request);
    }

    [Fact]
    public void NoCookieIsAbsent() => Assert.Equal((false, false, null), Read());

    [Fact]
    public void OtherCookiesAreNotTheSessionCookie() =>
        Assert.Equal((false, false, null), Read("theme=dark; paduraksa-session=abc; x__Host-paduraksa-session=abc"));

    [Fact]
    public void TheValueIsReadAmongOtherCookiesAndAcrossHeaders()
    {
        Assert.Equal((true, false, "abc"), Read("theme=dark; __Host-paduraksa-session=abc; other=1"));
        Assert.Equal((true, false, "abc"), Read("theme=dark", "__Host-paduraksa-session=abc"));
        Assert.Equal((true, false, "abc"), Read("__Host-paduraksa-session=\"abc\""));
    }

    [Fact]
    public void TheSameCookieTwiceIsAmbiguous()
    {
        Assert.True(Read("__Host-paduraksa-session=a; __Host-paduraksa-session=b").Ambiguous);
        Assert.True(Read("__Host-paduraksa-session=a", "__Host-paduraksa-session=a").Ambiguous);
    }

    [Fact]
    public void AnEmptyValueIsPresentButEmpty() => Assert.Equal((true, false, ""), Read("__Host-paduraksa-session="));

    [Fact]
    public void SetAndClearUseTheHostPrefixAttributes()
    {
        DefaultHttpContext context = new();

        AdminSessionCookie.Set(context.Response, "value", DateTimeOffset.UtcNow.AddHours(1));
        string set = context.Response.Headers.SetCookie.ToString();
        context.Response.Headers.Remove("Set-Cookie");
        AdminSessionCookie.Clear(context.Response);
        string cleared = context.Response.Headers.SetCookie.ToString();

        foreach (string line in new[] { set, cleared })
        {
            Assert.StartsWith("__Host-paduraksa-session=", line, StringComparison.Ordinal);
            Assert.Contains("secure", line, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("httponly", line, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("samesite=strict", line, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("path=/", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("domain", line, StringComparison.OrdinalIgnoreCase);
        }

        Assert.StartsWith("__Host-paduraksa-session=value;", set, StringComparison.Ordinal);
        Assert.StartsWith("__Host-paduraksa-session=;", cleared, StringComparison.Ordinal);
    }
}
