using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Config;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>What a set's usage says: how many hosts and routes, and the verb that agrees with it.</summary>
public sealed class RuleSetUsageTests
{
    private static readonly HostConfig Host = new(HostId.New(), ["app.example.com"]);
    private static readonly RuleSetRouteUse Route = new(Host, new RouteConfig(RouteId.New(), UpstreamId.New(), Path: "/admin"));

    [Theory]
    [InlineData(1, 0, "1 host", "uses")]
    [InlineData(0, 1, "1 route", "uses")]
    [InlineData(2, 0, "2 hosts", "use")]
    [InlineData(0, 2, "2 routes", "use")]
    [InlineData(1, 1, "1 host, 1 route", "use")]
    public void TheVerbAgreesWithTheSubjectTheCountTextBuilds(int hosts, int routes, string text, string verb)
    {
        RuleSetUsage usage = new([.. Enumerable.Repeat(Host, hosts)], [.. Enumerable.Repeat(Route, routes)]);

        Assert.Equal(text, usage.CountText);
        Assert.Equal(verb, usage.UseVerb);
    }
}
