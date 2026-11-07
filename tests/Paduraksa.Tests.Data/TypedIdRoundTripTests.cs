using Microsoft.EntityFrameworkCore;
using Paduraksa.Tests.Data.Sample;
using static Paduraksa.Tests.Data.Sample.SampleDatabase;

namespace Paduraksa.Tests.Data;

/// <summary>Every ID kind survives a save and a fresh read as key, foreign key, nullable and JSON member.</summary>
public sealed class TypedIdRoundTripTests
{
    [Fact]
    public async Task UuidV7RoundTripsAsKeyForeignKeyAndNullable()
    {
        await using var db = await CreateAsync();
        var parent = new Host { Id = HostId.New(), Name = "parent" };
        var child = new Host { Id = HostId.New(), Name = "child", ParentId = parent.Id };
        var route = new Route { Id = RouteId.New(), HostId = child.Id, Origin = { CopiedFromRouteId = RouteId.New() } };
        await db.SaveAsync(parent, child, route);

        await using var context = db.NewContext();
        var loaded = await context.Hosts.Include(h => h.Routes).SingleAsync(h => h.Id == child.Id, Ct);
        Assert.Equal(child.Id, loaded.Id);
        Assert.Equal(parent.Id, loaded.ParentId);
        var loadedRoute = Assert.Single(loaded.Routes);
        Assert.Equal(route.Id, loadedRoute.Id);
        Assert.Equal(child.Id, loadedRoute.HostId);
        Assert.Equal(route.Origin.CopiedFromRouteId, loadedRoute.Origin.CopiedFromRouteId);
        Assert.Null((await context.Hosts.SingleAsync(h => h.Id == parent.Id, Ct)).ParentId);
    }

    [Fact]
    public async Task UuidV4RoundTripsAsKeyAndNullable()
    {
        await using var db = await CreateAsync();
        var host = new Host { Id = HostId.New(), Name = "h" };
        var challenge = new Challenge { Id = ChallengeId.New(), HostId = host.Id };
        var withChallenge = new Route { Id = RouteId.New(), HostId = host.Id, ChallengeId = challenge.Id };
        var withoutChallenge = new Route { Id = RouteId.New(), HostId = host.Id };
        await db.SaveAsync(host, challenge, withChallenge, withoutChallenge);

        await using var context = db.NewContext();
        var loaded = await context.Challenges.SingleAsync(Ct);
        Assert.Equal(challenge.Id, loaded.Id);
        Assert.Equal(host.Id, loaded.HostId);
        Assert.Equal(challenge.Id, (await context.Routes.SingleAsync(r => r.Id == withChallenge.Id, Ct)).ChallengeId);
        Assert.Null((await context.Routes.SingleAsync(r => r.Id == withoutChallenge.Id, Ct)).ChallengeId);
    }

    [Fact]
    public async Task Int64OrderedRoundTripsAsKeyForeignKeyAndNullable()
    {
        await using var db = await CreateAsync();
        var first = new AuditEvent { Id = AuditEventId.New(), Action = "create" };
        var second = new AuditEvent { Id = AuditEventId.New(), Action = "update", PreviousId = first.Id, TargetHostId = HostId.New() };
        var host = new Host { Id = HostId.New(), Name = "h" };
        var route = new Route { Id = RouteId.New(), HostId = host.Id, Origin = { CreatedByAuditId = second.Id } };
        await db.SaveAsync(first, second, host, route);

        await using var context = db.NewContext();
        var events = await context.AuditEvents.OrderBy(a => a.Id).ToListAsync(Ct);
        Assert.Equal([first.Id, second.Id], events.Select(e => e.Id));
        Assert.Null(events[0].PreviousId);
        Assert.Null(events[0].TargetHostId);
        Assert.Equal(first.Id, events[1].PreviousId);
        Assert.Equal(second.TargetHostId, events[1].TargetHostId);
        Assert.Equal(second.Id, (await context.Routes.SingleAsync(Ct)).Origin.CreatedByAuditId);
    }

    [Fact]
    public async Task EveryKindRoundTripsInsideJsonComplexType()
    {
        await using var db = await CreateAsync();
        var settings = new HostSettings
        {
            DefaultRouteId = RouteId.New(),
            FallbackHostId = HostId.New(),
            ChallengeId = ChallengeId.New(),
            LastAuditId = AuditEventId.New(),
            Challenge = { PinnedChallengeId = ChallengeId.New(), FirstAuditId = AuditEventId.New() },
            Aliases =
            {
                new HostAlias { TargetHostId = HostId.New(), AddedByAuditId = AuditEventId.New() },
                new HostAlias { TargetHostId = HostId.New() },
            },
        };
        var full = new Host { Id = HostId.New(), Name = "full", Settings = settings };
        var empty = new Host { Id = HostId.New(), Name = "empty" };
        await db.SaveAsync(full, empty);

        await using var context = db.NewContext();
        var loaded = (await context.Hosts.SingleAsync(h => h.Id == full.Id, Ct)).Settings;
        Assert.Equal(settings.DefaultRouteId, loaded.DefaultRouteId);
        Assert.Equal(settings.FallbackHostId, loaded.FallbackHostId);
        Assert.Equal(settings.ChallengeId, loaded.ChallengeId);
        Assert.Equal(settings.LastAuditId, loaded.LastAuditId);
        Assert.Equal(settings.Challenge.PinnedChallengeId, loaded.Challenge.PinnedChallengeId);
        Assert.Equal(settings.Challenge.FirstAuditId, loaded.Challenge.FirstAuditId);
        Assert.Equal(
            settings.Aliases.Select(a => (a.TargetHostId, a.AddedByAuditId)),
            loaded.Aliases.Select(a => (a.TargetHostId, a.AddedByAuditId)));

        var defaults = (await context.Hosts.SingleAsync(h => h.Id == empty.Id, Ct)).Settings;
        Assert.True(defaults.DefaultRouteId.IsEmpty);
        Assert.Null(defaults.FallbackHostId);
        Assert.Null(defaults.ChallengeId);
        Assert.Null(defaults.LastAuditId);
        Assert.Empty(defaults.Aliases);
    }

    [Fact]
    public async Task TrackedEntityKeepsIdentityAndUpdatesByTypedKey()
    {
        await using var db = await CreateAsync();
        var host = new Host { Id = HostId.New(), Name = "before" };
        await db.SaveAsync(host);

        await using (var context = db.NewContext())
        {
            var tracked = await context.Hosts.FindAsync([host.Id], Ct);
            Assert.NotNull(tracked);
            Assert.Same(tracked, await context.Hosts.SingleAsync(h => h.Id == host.Id, Ct));
            tracked.Name = "after";
            tracked.Settings.FallbackHostId = host.Id;
            await context.SaveChangesAsync(Ct);
        }

        await using var verify = db.NewContext();
        var loaded = await verify.Hosts.SingleAsync(Ct);
        Assert.Equal("after", loaded.Name);
        Assert.Equal(host.Id, loaded.Settings.FallbackHostId);
    }

    [Fact]
    public async Task ForeignKeysAreEnforcedOnTypedIdColumns()
    {
        await using var db = await CreateAsync();
        var orphan = new Route { Id = RouteId.New(), HostId = HostId.New() };

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveAsync(orphan));
    }
}
