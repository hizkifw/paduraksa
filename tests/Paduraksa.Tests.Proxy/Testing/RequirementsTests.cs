using Paduraksa.Tests.Shared;

namespace Paduraksa.Tests.Proxy.Testing;

public sealed class RequirementsTests
{
    [Theory]
    [InlineData("playwright,quic,pebble", "quic", true)]
    [InlineData("playwright, quic ;pebble", "pebble", true)]
    [InlineData("QUIC", "quic", true)]
    [InlineData("playwright", "quic", false)]
    [InlineData("quic-extra", "quic", false)]
    [InlineData("", "quic", false)]
    [InlineData(null, "quic", false)]
    public void IsRequired_ReadsTheCommaSeparatedList(string? setting, string capability, bool expected) =>
        Assert.Equal(expected, Requirements.IsRequired(setting, capability));
}
