using Paduraksa.Proxy.Honeypot;

namespace Paduraksa.Tests.Proxy.Honeypot;

public sealed class HoneypotWireNamesTests
{
    [Theory]
    [InlineData(HoneypotLinkKind.Direct, "direct")]
    [InlineData(HoneypotLinkKind.NoFollow, "nofollow")]
    [InlineData(HoneypotLinkKind.NoIndexPage, "noindexPage")]
    [InlineData(HoneypotLinkKind.Ugc, "ugc")]
    public void ALinkKindHasOneNameForTheApiAndTheSimulator(HoneypotLinkKind kind, string name) =>
        Assert.Equal(name, HoneypotWireNames.Link(kind));
}
