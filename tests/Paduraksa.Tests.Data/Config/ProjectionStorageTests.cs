using Microsoft.EntityFrameworkCore;
using Paduraksa.Core.Model;
using Paduraksa.Data.Projection;
using Paduraksa.Data.Revisions;
using static Paduraksa.Tests.Data.Config.TestConfigDatabase;

namespace Paduraksa.Tests.Data.Config;

public sealed class ProjectionStorageTests
{
    [Fact]
    public async Task EnabledHostsCantShareANameInAnyCase()
    {
        await using var db = await CreateAsync();
        var (first, second) = (Host(enabled: true), Host(enabled: true));
        await SaveAsync(db, first, second, Name(first, "app.example.com"));

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(db, Name(second, "APP.example.com")));

        Assert.Contains("UNIQUE constraint failed: HostNames.Name", error.InnerException?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADisabledHostMayShareAnEnabledHostsName()
    {
        await using var db = await CreateAsync();
        var (live, staged, alsoStaged) = (Host(enabled: true), Host(enabled: false), Host(enabled: false));

        await SaveAsync(db, live, staged, alsoStaged, Name(live, "app.example.com"), Name(staged, "app.example.com"), Name(alsoStaged, "app.example.com"));

        Assert.Equal(3L, await db.ScalarAsync("SELECT count(*) FROM HostNames WHERE Name = 'APP.EXAMPLE.COM'"));
    }

    [Fact]
    public async Task AHostCantListANameTwice()
    {
        await using var db = await CreateAsync();
        var host = Host(enabled: false);
        await SaveAsync(db, host, Name(host, "app.example.com"));

        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(db, Name(host, "App.Example.Com")));
    }

    [Fact]
    public async Task ProfileAndUpstreamNamesAreUniqueIgnoringCase()
    {
        await using var db = await CreateAsync();
        await SaveAsync(db, new ProfileRow { Id = ProfileId.New(), Name = "LAN only" }, new UpstreamRow { Id = UpstreamId.New(), Name = "app" });

        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(db, new ProfileRow { Id = ProfileId.New(), Name = "lan ONLY" }));
        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(db, new UpstreamRow { Id = UpstreamId.New(), Name = "APP" }));
    }

    [Fact]
    public async Task ReferencesMustResolve()
    {
        await using var db = await CreateAsync();
        var host = Host(enabled: true);
        await SaveAsync(db, host);

        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(db, Host(enabled: true, profile: ProfileId.New())));
        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(db, Route(host, UpstreamId.New())));
        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(db, Name(Host(enabled: true), "orphan.example.com")));
        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(db, new DestinationRow { Id = DestinationId.New(), UpstreamId = UpstreamId.New(), Address = "http://x" }));
    }

    [Fact]
    public async Task AnUpstreamInUseCantBeDeletedButAHostTakesItsNamesAndRoutesWithIt()
    {
        await using var db = await CreateAsync();
        var upstream = new UpstreamRow { Id = UpstreamId.New(), Name = "app" };
        var host = Host(enabled: true);
        await SaveAsync(db, upstream, new DestinationRow { Id = DestinationId.New(), UpstreamId = upstream.Id, Address = "http://app" }, host, Name(host, "app.example.com"), Route(host, upstream.Id));

        var error = await Assert.ThrowsAnyAsync<Exception>(() => db.ExecuteAsync("DELETE FROM Upstreams"));
        Assert.Contains("FOREIGN KEY", error.Message, StringComparison.Ordinal);

        await db.ExecuteAsync("DELETE FROM Hosts");
        Assert.Equal(0L, await db.ScalarAsync("SELECT (SELECT count(*) FROM HostNames) + (SELECT count(*) FROM Routes)"));
        await db.ExecuteAsync("DELETE FROM Upstreams");
        Assert.Equal(0L, await db.ScalarAsync("SELECT count(*) FROM Destinations"));
    }

    [Fact]
    public async Task DeferredForeignKeysLetATransactionRewriteRowsInAnyOrder()
    {
        // The pipeline rewrites an object's rows (delete, then insert) with the checks deferred to the commit.
        await using var db = await CreateAsync();
        var upstream = new UpstreamRow { Id = UpstreamId.New(), Name = "app" };
        var host = Host(enabled: true);
        await SaveAsync(db, upstream, host, Route(host, upstream.Id));

        await using var context = db.NewContext();
        await using (var transaction = await context.Database.BeginTransactionAsync(Ct))
        {
            await context.Database.ExecuteSqlRawAsync("PRAGMA defer_foreign_keys = ON", Ct);
            await context.Upstreams.ExecuteDeleteAsync(Ct);
            context.Upstreams.Add(new UpstreamRow { Id = upstream.Id, Name = "app (renamed)", Version = 2 });
            await context.SaveChangesAsync(Ct);
            await transaction.CommitAsync(Ct);
        }

        Assert.Equal("app (renamed)", await db.ScalarAsync("SELECT Name FROM Upstreams"));
    }

    [Fact]
    public async Task GlobalSettingsAreASingleRowNamingARealRevision()
    {
        await using var db = await CreateAsync();
        var revision = new ConfigRevision
        {
            Id = ConfigRevisionId.New(),
            Seq = 1,
            ActorKind = ActorKind.System,
            Channel = Channel.System,
            Document = [1],
            DocumentHash = new byte[32],
            Summary = "[]",
        };
        await SaveAsync(db, revision, new GlobalSettingsRow { RevisionId = revision.Id });

        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(db, new GlobalSettingsRow { RevisionId = revision.Id }));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => db.ExecuteAsync("UPDATE GlobalSettings SET Key = 2"));
        Assert.Contains("CHECK constraint failed: CK_GlobalSettings_Singleton", error.Message, StringComparison.Ordinal);
        error = await Assert.ThrowsAnyAsync<Exception>(() => db.ExecuteAsync("UPDATE GlobalSettings SET RevisionId = randomblob(16)"));
        Assert.Contains("FOREIGN KEY", error.Message, StringComparison.Ordinal);
    }

    private static HostRow Host(bool enabled, ProfileId? profile = null) => new()
    {
        Id = HostId.New(),
        Kind = "proxy",
        Enabled = enabled,
        ProfileId = profile,
        CertificateMode = "auto",
    };

    private static HostNameRow Name(HostRow host, string name) => new() { HostId = host.Id, Name = name, HostEnabled = host.Enabled };

    private static RouteRow Route(HostRow host, UpstreamId upstream) =>
        new() { Id = RouteId.New(), HostId = host.Id, UpstreamId = upstream, Match = "prefix", Path = "/" };

    private static async Task SaveAsync(TestConfigDatabase db, params object[] entities)
    {
        await using var context = db.NewContext();
        context.AddRange(entities);
        await context.SaveChangesAsync(Ct);
    }
}
