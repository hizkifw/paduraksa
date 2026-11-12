using Paduraksa.Application.Config;
using Paduraksa.Application.Drafts;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Drafts;
using Paduraksa.Core.Model;

namespace Paduraksa.Tests.Unit.Drafts;

/// <summary>
/// What a review's token covers (PLAN §12): the draft, what it writes, what it refers to and the warnings it changes; and what it leaves out, so that a commit
/// that doesn't touch any of that (another host, a shifted position in the document) doesn't make a review stale.
/// </summary>
public sealed class DraftReviewTokenTests
{
    private static readonly DraftId Draft = DraftId.New();

    private static readonly UpstreamConfig App = new(UpstreamId.New(), "app", Destinations: [new DestinationConfig(DestinationId.New(), "http://app:80")], Version: 2);

    private static readonly HostConfig Mine = new(HostId.New(), ["mine.example.com"], Routes: [new RouteConfig(RouteId.New(), App.Id)]);

    private static DraftReplay.Replayed Replayed(ConfigDocument head, HostConfig host)
    {
        DraftEntry entry = new(1, DraftEntryAction.Create, DraftTargets.Host(host.Id), new UpsertHostCommand(host), DateTimeOffset.UnixEpoch);
        return DraftReplay.Replay(head, new RevisionRef(ConfigRevisionId.New(), 3), [entry]);
    }

    private static string Token(DraftReplay.Replayed replayed, long version = 1, IReadOnlyList<ConfigWarning>? warnings = null, IReadOnlyList<ConfigWarning>? live = null) =>
        DraftReviewToken.Compute(Draft, version, replayed, warnings ?? [], live ?? []);

    private static ConfigDocument Head(params HostConfig[] others) => new(Upstreams: [App], Hosts: [.. others]);

    [Fact]
    public void ItIsTheDraftsVersionThenAHash_AndTheSameReviewGivesTheSameToken()
    {
        var token = Token(Replayed(Head(), Mine), version: 7);

        Assert.Matches("^7\\.[A-Za-z0-9_-]{22}$", token);
        Assert.Equal(token, Token(Replayed(Head(), Mine), version: 7));
    }

    [Fact]
    public void AnotherHostBeingCommittedLeavesTheTokenAsItWas()
    {
        var other = new HostConfig(HostId.New(), ["other.example.com"], Kind: HostKind.Respond, Respond: new RespondConfig(200, "x"), Version: 5);

        Assert.Equal(Token(Replayed(Head(), Mine)), Token(Replayed(Head(other), Mine)));
    }

    [Fact]
    public void AnythingTheDraftWritesOrRefersToOrItsVersionMovesTheToken()
    {
        var token = Token(Replayed(Head(), Mine));

        Assert.NotEqual(token, Token(Replayed(Head(), Mine), version: 2));
        Assert.NotEqual(token, Token(Replayed(Head(), Mine with { Tags = ["changed"] })));
        var moved = new ConfigDocument(Upstreams: [App with { Name = "app, moved" }]);
        Assert.NotEqual(token, Token(Replayed(moved, Mine)));
    }

    [Fact]
    public void OnlyTheWarningsTheDraftChangesCount()
    {
        var replayed = Replayed(Head(), Mine);
        ConfigWarning others = new("/hosts/0", "host.certificate.none", "other.example.com: no certificate covers it");
        ConfigWarning mine = new("/hosts/1", "host.certificate.none", "mine.example.com: no certificate covers it");
        var token = Token(replayed, warnings: [mine], live: []);

        // A warning the live config has as well is not the draft's; its position in either document is nobody's business.
        Assert.Equal(token, Token(replayed, warnings: [mine, others with { Path = "/hosts/9" }], live: [others]));
        Assert.NotEqual(token, Token(replayed, warnings: [], live: []));
        Assert.NotEqual(token, Token(replayed, warnings: [mine, mine with { Message = "mine.example.com: expired" }], live: []));
    }
}
