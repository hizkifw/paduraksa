using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Tls;

namespace Paduraksa.Tests.Proxy.Snapshot;

public sealed class HostNamesTests
{
    [Theory]
    [InlineData(" App.Example.COM ", "app.example.com", "Exact")]
    [InlineData("app.example.com.", "app.example.com", "Exact")]
    [InlineData("*.Example.com", "*.example.com", "Wildcard")]
    [InlineData("bücher.test", "xn--bcher-kva.test", "Exact")]
    [InlineData("BÜCHER.Test", "xn--bcher-kva.test", "Exact")] // non-ASCII letters fold like ASCII ones
    [InlineData("Ελλάδα.test", "xn--hxakic4aa.test", "Exact")]
    [InlineData("*.bücher.test", "*.xn--bcher-kva.test", "Wildcard")]
    [InlineData("localhost", "localhost", "Exact")]
    [InlineData("_acme.internal", "_acme.internal", "Exact")]
    [InlineData("123.example.com", "123.example.com", "Exact")] // numeric labels are fine, just not the last one
    [InlineData("*.home.arpa", "*.home.arpa", "Wildcard")]
    [InlineData("192.0.2.1", "192.0.2.1", "Exact")]
    [InlineData("[2001:DB8:0:0::1]", "[2001:db8::1]", "IPv6Literal")]
    [InlineData("[::ffff:192.0.2.1]", "[::ffff:192.0.2.1]", "IPv6Literal")]
    public void ValidNamesAreNormalised(string input, string expected, string kind)
    {
        Assert.True(HostNames.TryNormalize(input, out string normalized, out HostNameKind actualKind, out string? error), error);
        Assert.Equal(expected, normalized);
        Assert.Equal(kind, actualKind.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a.*.test")]
    [InlineData("*test.com")]
    [InlineData("*")]
    [InlineData("a..test")]
    [InlineData("-a.test")]
    [InlineData("a-.test")]
    [InlineData("a.test:8080")]
    [InlineData("a b.test")]
    [InlineData("a/b.test")]
    [InlineData("192.0.2.256")]
    [InlineData("192.0.2.01")]
    [InlineData("1.2.3")]
    [InlineData("*.192.0.2.1")]
    [InlineData("host.123")] // numeric last label, but not an IPv4 literal
    [InlineData("a.b.c.d.0")]
    [InlineData("*.1.2")]
    [InlineData("*.localhost")] // a wildcard parent needs two labels, as for server names
    [InlineData("*.com")]
    [InlineData("*.lan.")]
    [InlineData("*.bücher")]
    [InlineData("[2001:db8::1")]
    [InlineData("[fe80::1%eth0]")]
    [InlineData("[192.0.2.1]")]
    [InlineData("[not-an-ip]")]
    [InlineData("bücher.test")] // NFD: u + combining diaeresis
    [InlineData("ｅｘａｍｐｌｅ.test")] // fullwidth "example"
    [InlineData("example．test")] // fullwidth full stop
    [InlineData("b‍cher.test")] // zero-width joiner
    [InlineData("b­cher.test")] // soft hyphen
    public void InvalidNamesAreRejected(string input)
    {
        Assert.False(HostNames.TryNormalize(input, out _, out _, out string? error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    // The certificate table gets every DNS name the host table accepts, so the two must agree (IP literals aside).
    [Theory]
    [InlineData("App.Example.COM.")]
    [InlineData("*.bücher.test")]
    [InlineData("localhost")]
    [InlineData("_acme.internal")]
    [InlineData("xn--zzzzzzzzzzzzzzzzzzzzzz.test")]
    public void AcceptedDnsNamesAreValidServerNames(string input)
    {
        Assert.True(HostNames.TryNormalize(input, out string normalized, out _, out string? error), error);
        string canonical = ServerNames.Normalize(normalized, out bool wildcard, out _);
        Assert.Equal(normalized, wildcard ? "*." + canonical : canonical);
    }

    [Fact]
    public void OverlongNamesAreRejected()
    {
        Assert.False(HostNames.TryNormalize(new string('a', 64) + ".test", out _, out _, out _));
        Assert.False(HostNames.TryNormalize(string.Join('.', Enumerable.Repeat(new string('a', 63), 4)) + ".test", out _, out _, out _));
        Assert.True(HostNames.TryNormalize(new string('a', 63) + ".test", out _, out _, out _));
    }
}
