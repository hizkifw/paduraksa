using Paduraksa.Application.Revisions;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Revisions;
using Paduraksa.Core.Model;
using Paduraksa.Tests.Data.Commands;
using static Paduraksa.Tests.Data.Commands.Objects;

namespace Paduraksa.Tests.Data.Revisions;

/// <summary>The history timeline (the dashboard's): revisions newest first, each with what it changed, paged by cursor.</summary>
public sealed class RevisionTimelineTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheTimeline_ListsRevisionsNewestFirst_WithTheirChanges_InPages()
    {
        await using var h = await ConfigServiceHarness.CreateAsync();
        var upstream = UpstreamId.New();
        var host = HostId.New();
        var route = RouteId.New();
        await h.RunAsync(new UpsertUpstreamCommand(Upstream(upstream, "app")));
        await h.RunAsync(new UpsertHostCommand(ProxyHost(host, "app.example.com", upstream, route)) { Message = "First host" });
        await h.RunAsync(new UpsertHostCommand(ProxyHost(host, "app.example.com", upstream, route) with { Version = 2, Enabled = false }));
        RevisionQueryService revisions = new(h.Db.Factory);

        RevisionTimelinePage first = (await revisions.ListWithChangesAsync(2, null, Ct))!;
        RevisionTimelinePage second = (await revisions.ListWithChangesAsync(2, first.Next, Ct))!;

        Assert.Equal([3L, 2L], first.Items.Select(entry => entry.Revision.Seq));
        HostChange disabled = Assert.IsType<HostChange>(Assert.Single(first.Items[0].Changes));
        Assert.Equal(ConfigChangeKind.Modified, disabled.Kind);
        Assert.Equal(host, disabled.HostId);
        Assert.Equal("First host", first.Items[1].Revision.Message);
        Assert.Contains(first.Items[1].Changes, change => change is HostChange { Kind: ConfigChangeKind.Added });
        Assert.Equal(first.Items[1].Revision.Id, first.Next);
        UpstreamChange added = Assert.IsType<UpstreamChange>(Assert.Single(Assert.Single(second.Items).Changes));
        Assert.Equal(upstream, added.UpstreamId);
        Assert.Null(second.Next);
        Assert.Null(await revisions.ListWithChangesAsync(2, ConfigRevisionId.New(), Ct));
    }
}
