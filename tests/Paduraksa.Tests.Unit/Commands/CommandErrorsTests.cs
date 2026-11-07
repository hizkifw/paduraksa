using Paduraksa.Application.Commands;
using Paduraksa.Contracts.Config;

using static Paduraksa.Tests.Unit.Config.ConfigSamples;

namespace Paduraksa.Tests.Unit.Commands;

public sealed class CommandErrorsTests
{
    [Fact]
    public void ErrorsAtOrBelowTheObjectBecomeRelativeAndTheRestAreMapped()
    {
        var canonical = Valid();
        // The app host moved to the end, as an upsert arranges it.
        var arranged = canonical with { Hosts = [.. canonical.Hosts.Where(h => h.Id != AppHost), canonical.Hosts.Single(h => h.Id == AppHost)] };
        var map = CommandErrors.IndexMap.Between(arranged, canonical);
        ConfigError[] errors =
        [
            new("/hosts/2/names/1", "a", "own field"),
            new("/hosts/2", "b", "own object"),
            new("/hosts/20/names/0", "c", "not the object: only a prefix match"),
            new("/hosts/0/routes/0/upstreamId", "d", "another host"),
            new("/upstreams/0/destinations", "e", "an upstream"),
            new("/settings/trustedProxies", "f", "the settings"),
        ];

        var (own, related) = CommandErrors.Split(errors, CommandErrors.PathOf(arranged, "hosts", AppHost.ToString()), map);

        Assert.Equal([("/names/1", "a"), (string.Empty, "b")], own.Select(e => (e.Path, e.Code)));
        Assert.Equal(
            [("/hosts/20/names/0", "c"), ("/hosts/1/routes/0/upstreamId", "d"), ("/upstreams/0/destinations", "e"), ("/settings/trustedProxies", "f")],
            related.Select(e => (e.Path, e.Code)));
    }

    [Fact]
    public void WithoutAnObjectEverythingIsRelatedAndUnmappedPathsStay()
    {
        ConfigError[] errors = [new("/hosts/1", "a", "x"), new(string.Empty, "b", "y"), new("/hosts/x", "c", "z")];

        var (own, related) = CommandErrors.Split(errors, ownPath: null, toCanonical: null);

        Assert.Empty(own);
        Assert.Equal(errors, related);
        var identity = CommandErrors.IndexMap.Between(Valid(), Valid());
        Assert.Equal(["/hosts/1", string.Empty, "/hosts/x", "/hosts/1/names"], new[] { "/hosts/1", string.Empty, "/hosts/x", "/hosts/1/names" }.Select(identity.Remap));
    }

    [Fact]
    public void PathOfFindsTheObjectByIdOrNothing()
    {
        Assert.Equal("/hosts/0", CommandErrors.PathOf(Valid(), "hosts", AppHost.ToString()));
        Assert.Equal("/upstreams/0", CommandErrors.PathOf(Valid(), "upstreams", AppUpstream.ToString()));
        Assert.Null(CommandErrors.PathOf(Valid(), "profiles", AppHost.ToString()));
    }
}
