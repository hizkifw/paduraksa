using Paduraksa.Application.Commands;
using Paduraksa.Application.Config;
using Paduraksa.Application.Drafts;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Drafts;
using Paduraksa.Core.Model;
using static Paduraksa.Tests.Unit.Config.ConfigSamples;

namespace Paduraksa.Tests.Unit.Drafts;

/// <summary>
/// The merge of a draft on a head, with no database: entries replay in order on the document the earlier ones left, each checked against the
/// version of its object in the head as it is now; one that moved is blocked and left out, and the rest apply.
/// </summary>
public sealed class DraftReplayTests
{
    private static readonly RevisionRef HeadRevision = new(ConfigRevisionId.New(), 3);

    private static readonly DateTimeOffset Now = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private static ConfigDocument Head() => ConfigVersions.Stamp(Valid(), Valid(), ConfigDiff.Compare(new ConfigDocument(), Valid()), next: 3);

    private static DraftEntry Entry(int number, ConfigCommand command) =>
        new(number, DraftEntries.Of(command)!.Value.Action, DraftEntries.Of(command)!.Value.Target, command, Now);

    [Fact]
    public void EntriesAreAppliedInOrderOnTheHeadAndTheHeadIsNotChanged()
    {
        var head = Head();
        var upstream = new UpstreamConfig(UpstreamId.New(), "second", Destinations: [new DestinationConfig(DestinationId.New(), "http://two:80")]);
        var renamed = head.Upstreams[0] with { Name = "renamed" };

        var replayed = DraftReplay.Replay(head, HeadRevision, [Entry(1, new UpsertUpstreamCommand(upstream)), Entry(2, new UpsertUpstreamCommand(renamed))]);

        Assert.All(replayed.Entries, e => Assert.Equal(DraftEntryState.Ready, e.State));
        Assert.Equal(["renamed", "second"], replayed.Document.Upstreams.Select(u => u.Name).Order());
        Assert.Single(head.Upstreams);
        Assert.Equal("app", head.Upstreams[0].Name);
    }

    [Fact]
    public void VersionsStayTheHeadsSoAFormThatReadsTheDraftSendsAVersionTheEntryAcceptsAgain()
    {
        var head = Head();
        var edited = head.Hosts[0] with { Tags = ["changed"] };
        var first = DraftReplay.Replay(head, HeadRevision, [Entry(1, new UpsertHostCommand(edited))]);
        var shown = first.Document.Hosts.Single(h => h.Id == edited.Id);

        Assert.Equal(head.Hosts[0].Version, shown.Version);
        var again = DraftReplay.Replay(head, HeadRevision, [Entry(1, new UpsertHostCommand(shown with { Tags = ["changed twice"] }))]);

        Assert.Equal(DraftEntryState.Ready, Assert.Single(again.Entries).State);
    }

    [Fact]
    public void AnEntryWhoseObjectMovedIsBlockedAndTheOthersStillApply()
    {
        var head = Head();
        var stale = head.Upstreams[0] with { Name = "mine", Version = head.Upstreams[0].Version - 1 };
        var other = head.Profiles[0] with { Name = "also mine" };

        var replayed = DraftReplay.Replay(head, HeadRevision, [Entry(1, new UpsertUpstreamCommand(stale)), Entry(2, new UpsertProfileCommand(other))]);

        Assert.Equal([DraftEntryState.ChangedElsewhere, DraftEntryState.Ready], replayed.Entries.Select(e => e.State));
        Assert.Equal("app", replayed.Document.Upstreams[0].Name);
        Assert.Equal("also mine", replayed.Document.Profiles[0].Name);
        Assert.Equal([1], replayed.Blocked.Select(e => e.Number));
        Assert.Equal([2], replayed.Ready.Select(e => e.Number));
    }

    [Fact]
    public void AnObjectDeletedElsewhereAndOneCreatedTwiceAreBlocked()
    {
        var head = Head();
        var gone = Entry(1, new UpsertHostCommand(new HostConfig(HostId.New(), ["gone.example.com"], Kind: HostKind.Respond, Respond: new RespondConfig(200, "x"), Version: 2)));
        var clash = Entry(2, new UpsertHostCommand(head.Hosts[0] with { Version = 0 }));
        var deleted = Entry(3, new DeleteUpstreamCommand(UpstreamId.New(), 1));

        var replayed = DraftReplay.Replay(head, HeadRevision, [gone, clash, deleted]);

        Assert.All(replayed.Entries, e => Assert.Equal(DraftEntryState.ChangedElsewhere, e.State));
    }

    [Fact]
    public void ADeleteIsCheckedAgainstTheVersionItWasMadeAgainst()
    {
        var head = Head();
        var upstream = head.Upstreams[0];

        var ready = DraftReplay.Replay(head, HeadRevision, [Entry(1, new DeleteUpstreamCommand(upstream.Id, upstream.Version))]);
        var stale = DraftReplay.Replay(head, HeadRevision, [Entry(1, new DeleteUpstreamCommand(upstream.Id, upstream.Version - 1))]);

        Assert.Empty(ready.Document.Upstreams);
        Assert.Equal(DraftEntryState.ChangedElsewhere, Assert.Single(stale.Entries).State);
        Assert.Single(stale.Document.Upstreams);
    }

    [Fact]
    public void TheSettingsAreAnEntryOfTheirOwnWithTheirOwnVersion()
    {
        var head = Head();
        var settings = head.Settings with { Defaults = new HostSettings { ForceHttps = false } };

        var ready = DraftReplay.Replay(head, HeadRevision, [Entry(1, new UpdateSettingsCommand(settings))]);
        var stale = DraftReplay.Replay(head, HeadRevision, [Entry(1, new UpdateSettingsCommand(settings with { Version = settings.Version + 7 }))]);

        Assert.Equal(DraftEntryState.Ready, Assert.Single(ready.Entries).State);
        Assert.Equal(false, ready.Document.Settings.Defaults.ForceHttps);
        Assert.Equal(DraftEntryState.ChangedElsewhere, Assert.Single(stale.Entries).State);
        Assert.Equal("settings", DraftEntries.Of(new UpdateSettingsCommand(settings))!.Value.Target);
    }

    [Fact]
    public void AnEmptyDraftIsTheHeadAsItIs()
    {
        var head = Head();

        var replayed = DraftReplay.Replay(head, HeadRevision, []);

        Assert.Empty(replayed.Entries);
        Assert.Equal(CanonicalConfigJson.Serialize(CanonicalConfigJson.Canonicalize(head)), CanonicalConfigJson.Serialize(replayed.Document));
    }

    [Fact]
    public void AnEntryIsAboutOneObjectAndTheActionSaysWhatItDoesToIt()
    {
        var host = Head().Hosts[0];

        Assert.Equal(($"hosts/{host.Id}", DraftEntryAction.Update), DraftEntries.Of(new UpsertHostCommand(host)));
        Assert.Equal(($"hosts/{host.Id}", DraftEntryAction.Create), DraftEntries.Of(new UpsertHostCommand(host with { Version = 0 })));
        Assert.Equal(($"hosts/{host.Id}", DraftEntryAction.Delete), DraftEntries.Of(new DeleteHostCommand(host.Id, 1)));
        Assert.Null(DraftEntries.Of(new ReplaceConfigCommand(new ConfigDocument())));
        Assert.Null(DraftEntries.Of(new RollbackConfigCommand(ConfigRevisionId.New(), ConfigRevisionId.New())));
    }

    [Fact]
    public void ATitleNamesTheObjectAndADeletedOnesNameComesFromTheHead()
    {
        var head = Head();
        var upstream = head.Upstreams[0];

        Assert.Equal("delete upstream app", DraftText.Title(Entry(1, new DeleteUpstreamCommand(upstream.Id, upstream.Version)), head));
        Assert.Equal("add upstream brand new", DraftText.Title(Entry(2, new UpsertUpstreamCommand(upstream with { Id = UpstreamId.New(), Name = "brand new", Version = 0 })), head));
        Assert.Equal("change host app.example.com", DraftText.Title(Entry(3, new UpsertHostCommand(head.Hosts[0])), head));
        Assert.Equal("change global settings", DraftText.Title(Entry(4, new UpdateSettingsCommand(head.Settings)), head));
    }

    [Fact]
    public void WhatARequestAddedIsNotPartOfAnEntry()
    {
        var command = new UpdateSettingsCommand(Head().Settings) { DryRun = true, Force = true, Message = "note" };

        var normalized = DraftEntries.Normalize(command);

        Assert.False(normalized.DryRun);
        Assert.False(normalized.Force);
        Assert.Null(normalized.Message);
    }

    [Fact]
    public void AnEntryThatRefersToWhatABlockedEntryWouldHaveWrittenIsBlockedWithAReasonNamingIt()
    {
        var head = Head();
        var upstream = new UpstreamConfig(UpstreamId.New(), "mine", Destinations: [new DestinationConfig(DestinationId.New(), "http://two:80")]);
        var host = new HostConfig(HostId.New(), ["new.example.com"], Routes: [new RouteConfig(RouteId.New(), upstream.Id)]);
        // The upstream is created at version 0, but the head has an object with that ID already: someone else took it.
        var theirs = head with { Upstreams = [.. head.Upstreams, upstream with { Version = 2, Name = "theirs" }] };

        var replayed = DraftReplay.Replay(theirs, HeadRevision, [Entry(1, new UpsertUpstreamCommand(upstream)), Entry(2, new UpsertHostCommand(host))]);

        Assert.All(replayed.Entries, e => Assert.Equal(DraftEntryState.ChangedElsewhere, e.State));
        Assert.Contains("add upstream mine", replayed.Entries[1].Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(replayed.Document.Hosts, h => h.Id == host.Id);
        Assert.Equal("theirs", replayed.Document.Upstreams.Single(u => u.Id == upstream.Id).Name);
    }

    [Fact]
    public void BlockingFollowsTheChainAndWorksWhateverTheOrderOfTheEntries()
    {
        var head = Head();
        var upstream = new UpstreamConfig(UpstreamId.New(), "mine", Destinations: [new DestinationConfig(DestinationId.New(), "http://two:80")]);
        var profile = new ProfileConfig(ProfileId.New(), "uses a list", Settings: new HostSettings { RateLimit = new RateLimitSettings { PolicyId = RateLimitPolicyId.New() } });
        var host = new HostConfig(HostId.New(), ["new.example.com"], ProfileId: profile.Id, Routes: [new RouteConfig(RouteId.New(), upstream.Id)]);
        var theirs = head with { Upstreams = [.. head.Upstreams, upstream with { Version = 2 }] };

        // The host comes first in the draft (it was edited to use the upstream after the upstream was added).
        var replayed = DraftReplay.Replay(theirs, HeadRevision,
            [Entry(1, new UpsertHostCommand(host)), Entry(2, new UpsertUpstreamCommand(upstream)), Entry(3, new UpsertProfileCommand(profile))]);

        Assert.Equal([DraftEntryState.ChangedElsewhere, DraftEntryState.ChangedElsewhere, DraftEntryState.Ready], replayed.Entries.Select(e => e.State));
        Assert.Single(replayed.Document.Profiles, p => p.Id == profile.Id);
    }

    [Fact]
    public void ABlockedDeleteBlocksNothingThatRefersToIt()
    {
        var head = Head();
        var upstream = head.Upstreams[0];
        var stale = new DeleteUpstreamCommand(upstream.Id, upstream.Version - 1);
        var host = head.Hosts[0] with { Tags = ["still points at it"] };

        var replayed = DraftReplay.Replay(head, HeadRevision, [Entry(1, stale), Entry(2, new UpsertHostCommand(host))]);

        Assert.Equal([DraftEntryState.ChangedElsewhere, DraftEntryState.Ready], replayed.Entries.Select(e => e.State));
        Assert.Null(replayed.Entries[1].Reason);
    }

    [Fact]
    public void WhatAnEntryRefersToIsReadFromItsObjectAndItsRoutes()
    {
        var head = Head();
        var rate = RateLimitPolicyId.New();
        var host = head.Hosts[0] with
        {
            Routes = [new RouteConfig(RouteId.New(), head.Upstreams[0].Id, Settings: new RouteSettings { RateLimit = new RateLimitSettings { PolicyId = rate } })],
        };

        var refers = DraftReferences.Of(new UpsertHostCommand(host));

        Assert.Contains(DraftTargets.Upstream(head.Upstreams[0].Id), refers);
        Assert.Contains(DraftTargets.RateLimit(rate), refers);
        Assert.Contains(DraftTargets.Profile(head.Profiles[0].Id), refers);
        Assert.Empty(DraftReferences.Of(new DeleteHostCommand(host.Id, 1)));
        Assert.Empty(DraftReferences.Of(new UpsertUpstreamCommand(head.Upstreams[0])));
    }
}
