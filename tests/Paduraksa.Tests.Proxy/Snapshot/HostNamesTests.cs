using Paduraksa.Proxy.Snapshot;

namespace Paduraksa.Tests.Proxy.Snapshot;

public sealed class HostNamesTests
{
    [Theory]
    [InlineData(" App.Example.COM ", "app.example.com", "Exact")]
    [InlineData("app.example.com.", "app.example.com", "Exact")]
    [InlineData("*.Example.com", "*.example.com", "Wildcard")]
    [InlineData("bücher.test", "xn--bcher-kva.test", "Exact")]
    [InlineData("*.bücher.test", "*.xn--bcher-kva.test", "Wildcard")]
    [InlineData("localhost", "localhost", "Exact")]
    [InlineData("_acme.internal", "_acme.internal", "Exact")]
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
    [InlineData("[2001:db8::1")]
    [InlineData("[fe80::1%eth0]")]
    [InlineData("[192.0.2.1]")]
    [InlineData("[not-an-ip]")]
    public void InvalidNamesAreRejected(string input)
    {
        Assert.False(HostNames.TryNormalize(input, out _, out _, out string? error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void OverlongNamesAreRejected()
    {
        Assert.False(HostNames.TryNormalize(new string('a', 64) + ".test", out _, out _, out _));
        Assert.False(HostNames.TryNormalize(string.Join('.', Enumerable.Repeat(new string('a', 63), 4)) + ".test", out _, out _, out _));
        Assert.True(HostNames.TryNormalize(new string('a', 63) + ".test", out _, out _, out _));
    }
}
