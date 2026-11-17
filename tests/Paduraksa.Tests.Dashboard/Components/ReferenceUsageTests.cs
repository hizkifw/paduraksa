using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Config;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>What a usage says: how many hosts and routes, in the list column and in the "Used by" sentence.</summary>
public sealed class ReferenceUsageTests
{
    private static readonly HostConfig Host = new(HostId.New(), ["app.example.com"]);
    private static readonly RouteUse Route = new(Host, new RouteConfig(RouteId.New(), UpstreamId.New(), Path: "/admin"));

    [Theory]
    [InlineData(1, 0, "1 host", "Used by 1 host")]
    [InlineData(0, 1, "1 route", "Used by 1 route")]
    [InlineData(2, 0, "2 hosts", "Used by 2 hosts")]
    [InlineData(0, 2, "2 routes", "Used by 2 routes")]
    [InlineData(1, 1, "1 host, 1 route", "Used by 1 host and 1 route")]
    public void TheListColumnAndTheSentenceCountTheSameWay(int hosts, int routes, string column, string sentence)
    {
        ReferenceUsage usage = new([.. Enumerable.Repeat(Host, hosts)], [.. Enumerable.Repeat(Route, routes)]);

        Assert.Equal(column, usage.CountText);
        Assert.Equal(sentence, usage.UsedByText);
    }

    [Fact]
    public void AnUnusedObject_HasNoSentence()
    {
        Assert.Equal("No host or route", ReferenceUsage.None.CountText);
        Assert.Null(ReferenceUsage.None.UsedByText);
    }

    [Theory]
    [InlineData(1, 0, "Used by 1 host. Pick another access list (or none) for them first.")]
    [InlineData(0, 1, "Used by 1 route. Pick another access list (or none) for them first.")]
    [InlineData(2, 1, "Used by 2 hosts and 1 route. Pick another access list (or none) for them first.")]
    public void TheRefusalSaysByWhomWhateverTheCounts(int hosts, int routes, string expected)
    {
        ReferenceUsage usage = new([.. Enumerable.Repeat(Host, hosts)], [.. Enumerable.Repeat(Route, routes)]);

        Assert.Equal(expected, usage.Refusal("access list (or none)"));
        Assert.Null(ReferenceUsage.None.Refusal("access list (or none)"));
    }
}
