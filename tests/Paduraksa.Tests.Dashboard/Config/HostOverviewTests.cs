using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Config;

namespace Paduraksa.Tests.Dashboard.Config;

public sealed class HostOverviewTests
{
    [Fact]
    public void ListsRoutesInEvaluationOrder()
    {
        UpstreamConfig web = new(UpstreamId.New(), "web", Destinations: [new DestinationConfig(DestinationId.New(), "http://web:80")]);
        UpstreamConfig api = new(UpstreamId.New(), "api", Destinations: [new DestinationConfig(DestinationId.New(), "http://api:80")]);
        RouteConfig root = new(RouteId.New(), web.Id);
        RouteConfig exact = new(RouteId.New(), api.Id, Match: RouteMatchKind.Exact, Path: "/health");
        RouteConfig prefix = new(RouteId.New(), api.Id, Path: "/api");
        HostConfig host = new(HostId.New(), ["app.test"], Routes: [root, exact, prefix]);
        ConfigDocument document = new(Hosts: [host], Upstreams: [web, api]);

        var destinations = HostOverview.DestinationsOf(host, document);

        Assert.Equal(["= /health", "/api", "/"], destinations.Select(d => d.Path));
    }
}
