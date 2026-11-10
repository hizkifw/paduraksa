using Paduraksa.Core.Model;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;

namespace Paduraksa.Tests.Proxy.Snapshot;

public sealed class RouteMatchingTests
{
    [Theory]
    [InlineData("/api", "/api", true)]
    [InlineData("/api", "/api/", true)]
    [InlineData("/api", "/api/v1/items", true)]
    [InlineData("/api", "/API/v1", true)]
    [InlineData("/api", "/apix", false)]
    [InlineData("/api", "/ap", false)]
    [InlineData("/api", "/", false)]
    [InlineData("/api/", "/api/x", true)]
    [InlineData("/api/", "/api/", true)]
    [InlineData("/api/", "/api", false)]
    [InlineData("/", "/", true)]
    [InlineData("/", "/anything/at/all", true)]
    [InlineData("/", "", true)]
    public void PrefixIsSegmentAwareAndIgnoresCase(string prefix, string path, bool expected) =>
        Assert.Equal(expected, Route(RouteMatchKind.Prefix, prefix).Matches(path));

    [Theory]
    [InlineData("/health", "/health", true)]
    [InlineData("/health", "/HEALTH", true)]
    [InlineData("/health", "/health/", false)]
    [InlineData("/health", "/healthz", false)]
    [InlineData("/", "", true)]
    public void ExactMatchesTheWholePath(string route, string path, bool expected) =>
        Assert.Equal(expected, Route(RouteMatchKind.Exact, route).Matches(path));

    [Fact]
    public void FirstMatchingRouteWinsInOrder()
    {
        var api = Route(RouteMatchKind.Prefix, "/api");
        var exact = Route(RouteMatchKind.Exact, "/api/health");
        var root = Route(RouteMatchKind.Prefix, "/");
        CompiledHost host = new()
        {
            Index = 0,
            Id = TestIds.Host("h"),
            Kind = HostKind.Proxy,
            Routes = [api, exact, root],
            Policies = [],
        };

        Assert.Same(api, host.Match("/api/health"));
        Assert.Same(root, host.Match("/other"));
        Assert.Null(new CompiledHost { Index = 0, Id = TestIds.Host("e"), Kind = HostKind.Proxy, Routes = [api], Policies = [] }.Match("/other"));
    }

    [Fact]
    public void MatchingDoesNotAllocate()
    {
        CompiledHost host = new()
        {
            Index = 0,
            Id = TestIds.Host("h"),
            Kind = HostKind.Proxy,
            Routes = [Route(RouteMatchKind.Exact, "/x"), Route(RouteMatchKind.Prefix, "/api"), Route(RouteMatchKind.Prefix, "/")],
            Policies = [],
        };
        AllocAssert.Zero(host, static h => _ = h.Match("/API/items/42"), "CompiledHost.Match");
    }

    private static CompiledRoute Route(RouteMatchKind kind, string path) => new()
    {
        Id = TestIds.Route("r"),
        MatchKind = kind,
        Path = path,
        Slot = 0,
        Policies = [],
    };
}
