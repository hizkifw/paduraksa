using System.Text;

using Paduraksa.Application.Certificates;
using Paduraksa.Application.Config;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

using static Paduraksa.Application.Config.ConfigErrorCodes;
using static Paduraksa.Tests.Unit.Config.ConfigSamples;

namespace Paduraksa.Tests.Unit.Config;

/// <summary>A route's path rewrite: what the validator refuses, what the mapping hands the compiler, and the canonical form.</summary>
public sealed class PathRewriteValidationTests
{
    [Fact]
    public void AStripAndAPrefixAreValidOnARouteWithAPath()
    {
        Assert.Empty(ConfigValidator.Validate(OnApiRoute(new PathRewriteConfig(StripPrefix: true, AddPrefix: "/v2"))));
        Assert.Empty(ConfigValidator.Validate(OnRootRoute(new PathRewriteConfig(AddPrefix: "/app"))));
    }

    [Fact]
    public void TheRootHasNothingToStripAndAPrefixIsAPlainPath()
    {
        AssertErrors(OnRootRoute(new PathRewriteConfig(StripPrefix: true)), ("/hosts/0/routes/0/rewrite/stripPrefix", RouteRewriteStripRoot));
        AssertErrors(OnApiRoute(new PathRewriteConfig(AddPrefix: "v2/")), ("/hosts/0/routes/1/rewrite/addPrefix", RouteRewriteAddPrefixInvalid));
        AssertErrors(OnApiRoute(new PathRewriteConfig(AddPrefix: "/a/../b")), ("/hosts/0/routes/1/rewrite/addPrefix", RouteRewriteAddPrefixInvalid));
        AssertErrors(OnApiRoute(new PathRewriteConfig(AddPrefix: "/a?b")), ("/hosts/0/routes/1/rewrite/addPrefix", RouteRewriteAddPrefixInvalid));
        AssertErrors(OnApiRoute(new PathRewriteConfig(AddPrefix: "/a/%2e%2e")), ("/hosts/0/routes/1/rewrite/addPrefix", RouteRewriteAddPrefixInvalid));
        AssertErrors(OnApiRoute(new PathRewriteConfig(AddPrefix: "/a/%252e")), ("/hosts/0/routes/1/rewrite/addPrefix", RouteRewriteAddPrefixInvalid));
    }

    [Fact]
    public void TheLocationSwitchGoesWithAStrip()
    {
        Assert.Empty(ConfigValidator.Validate(OnApiRoute(new PathRewriteConfig(StripPrefix: true, RewriteLocation: false))));
        AssertErrors(OnApiRoute(new PathRewriteConfig(AddPrefix: "/v2", RewriteLocation: false)), ("/hosts/0/routes/1/rewrite/rewriteLocation", RouteRewriteLocationUnused));

        var mapped = TestMapping.Map(OnApiRoute(new PathRewriteConfig(StripPrefix: true, RewriteLocation: false)), Id<ConfigRevisionId>(1), CertificateInventory.Empty);
        Assert.False(mapped.Model.Hosts.Single(h => h.Id == AppHost).Routes.Single(r => r.Id == ApiRoute).RewriteLocation);
    }

    [Fact]
    public void TheMappingHandsTheRewriteToTheRoute()
    {
        var mapped = TestMapping.Map(OnApiRoute(new PathRewriteConfig(StripPrefix: true, AddPrefix: "/v2")), Id<ConfigRevisionId>(1), CertificateInventory.Empty);

        var route = mapped.Model.Hosts.Single(h => h.Id == AppHost).Routes.Single(r => r.Id == ApiRoute);
        Assert.True(route.StripPrefix);
        Assert.Equal("/v2", route.AddPrefix);
    }

    [Fact]
    public void ARewriteThatDoesNothingIsNoKeyInTheCanonicalDocument()
    {
        var canonical = Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(OnApiRoute(new PathRewriteConfig())));
        var plain = Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(Valid()));

        Assert.Equal(plain, canonical);
        Assert.Contains("\"rewrite\":{\"stripPrefix\":true", Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(OnApiRoute(new PathRewriteConfig(StripPrefix: true)))), StringComparison.Ordinal);
    }

    private static ConfigDocument OnRootRoute(PathRewriteConfig rewrite) => On(0, rewrite);

    private static ConfigDocument OnApiRoute(PathRewriteConfig rewrite) => On(1, rewrite);

    private static ConfigDocument On(int index, PathRewriteConfig rewrite)
    {
        var document = Valid();
        var host = document.Hosts[0];
        RouteConfig[] routes = [.. host.Routes.Select((route, i) => i == index ? route with { Rewrite = rewrite } : route)];
        return document with { Hosts = [host with { Routes = routes }, .. document.Hosts.Skip(1)] };
    }

    private static void AssertErrors(ConfigDocument document, params (string Path, string Code)[] expected) =>
        Assert.Equal(expected, ConfigValidator.Validate(document).Select(e => (e.Path, e.Code)));
}
