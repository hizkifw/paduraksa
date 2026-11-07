using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Paduraksa.Application.Audit;
using Paduraksa.Application.Revisions;
using Paduraksa.Core.Ids;
using Paduraksa.Core.Model;
using static Paduraksa.Tests.Data.Config.TestConfigDatabase;

namespace Paduraksa.Tests.Data.Config;

public sealed class AuditLogTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero).AddTicks(42);

    private readonly ManualTimeProvider _clock = new(Now);
    private TestConfigDatabase _db = null!;
    private AuditLog _log = null!;

    public async ValueTask InitializeAsync()
    {
        _db = await CreateAsync();
        _log = new AuditLog(_clock);
    }

    public async ValueTask DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task EachEventChainsToThePreviousOnesHash()
    {
        var first = await AppendAsync(Entry("config.host.create"));
        var second = await AppendAsync(Entry("config.host.update"));
        var third = await AppendAsync(Entry("config.host.delete"));

        Assert.Equal(AuditChain.GenesisPrevHash.ToArray(), first.PrevHash.ToArray());
        Assert.Equal(first.Hash.ToArray(), second.PrevHash.ToArray());
        Assert.Equal(second.Hash.ToArray(), third.PrevHash.ToArray());
        Assert.All(new[] { first, second, third }, r => Assert.Equal(AuditChain.ComputeHash(r), r.Hash.ToArray()));
        Assert.True(first.Id < second.Id && second.Id < third.Id);
    }

    [Fact]
    public async Task TheStoredRowIsTheRecordThatWasHashed()
    {
        var token = ApiTokenId.New();
        var host = HostId.New();
        var revision = await CommitRevisionAsync();
        var entry = new AuditEntry(Actor.ForToken(token), Channel.Mcp, "config.host.update", AuditOutcome.Success)
        {
            ClientIp = IPAddress.Parse("::ffff:192.0.2.7"),
            Details = """{ "fields": ["name"] }""",
            RevisionId = revision,
        }.WithTarget(host);

        var appended = await AppendAsync(entry);

        await using var context = _db.NewContext();
        var stored = AuditMapping.ToRecord(await context.AuditEvents.SingleAsync(Ct));
        Assert.Equal(AuditChain.Encode(appended), AuditChain.Encode(stored));
        Assert.Equal(appended.Hash.ToArray(), stored.Hash.ToArray());
        Assert.Equal(Now.UtcTicks, stored.Timestamp.UtcTicks);
        Assert.Equal(Actor.ForToken(token), stored.Actor);
        Assert.Equal(host.ToString(), stored.Target);
        Assert.Equal("192.0.2.7", stored.ClientIp); // IPv4-mapped addresses are stored as IPv4
        Assert.Equal("""{ "fields": ["name"] }""", stored.Details); // as given, not re-serialised
        Assert.Equal(revision, stored.RevisionId);
    }

    [Fact]
    public async Task AppendNeedsTheCallersTransaction()
    {
        await using var context = _db.NewContext();

        await Assert.ThrowsAsync<InvalidOperationException>(() => _log.AppendAsync(context, Entry("config.host.update"), Ct));
    }

    [Fact]
    public async Task AppendIsPartOfTheCallersTransactionAndRollsBackWithIt()
    {
        await using (var context = _db.NewContext())
        {
            await using var transaction = await context.Database.BeginTransactionAsync(Ct);
            await _log.AppendAsync(context, Entry("config.host.update"), Ct);
            await transaction.RollbackAsync(Ct);
        }

        Assert.Equal(0L, await _db.ScalarAsync("SELECT count(*) FROM AuditEvents"));
        var next = await AppendAsync(Entry("config.host.update"));
        Assert.Equal(AuditChain.GenesisPrevHash.ToArray(), next.PrevHash.ToArray());
    }

    [Fact]
    public async Task AClockBehindTheLogContinuesAboveTheLatestEventWithAWarning()
    {
        // A restart on a clock an hour behind the last run: the fresh generator would issue IDs below the latest.
        var latest = await AppendAsync(Entry("config.host.update"));
        var logger = new ListLogger();
        var behind = new AuditLog(new ManualTimeProvider(Now.AddHours(-1)), logger);

        await using (var context = _db.NewContext())
        {
            await using var transaction = await context.Database.BeginTransactionAsync(Ct);
            var appended = await behind.AppendAsync(context, Entry("config.host.update"), Ct);
            await transaction.CommitAsync(Ct);
            Assert.True(appended.Id > latest.Id);
            Assert.Equal(Now.AddHours(-1), appended.Timestamp); // the timestamp says what the clock said
        }

        Assert.Equal(LogLevel.Warning, Assert.Single(logger.Entries).Level);
        await using var verify = _db.NewContext();
        Assert.True((await AuditChainVerifier.VerifyAsync(verify, Ct)).IsIntact);
    }

    [Fact]
    public async Task NonAsciiDetailsRoundTripAndVerify()
    {
        var details = """{"name":"café.example","note":"Paduraksa ✓ 😀","quote":"\"x\""}""";

        var appended = await AppendAsync(Entry("config.host.update") with { Details = details });

        Assert.Equal(details, await _db.ScalarAsync("SELECT Details FROM AuditEvents"));
        await using var context = _db.NewContext();
        var stored = AuditMapping.ToRecord(await context.AuditEvents.SingleAsync(Ct));
        Assert.Equal(AuditChain.Encode(appended), AuditChain.Encode(stored));
        Assert.True((await AuditChainVerifier.VerifyAsync(context, Ct)).IsIntact);
    }

    private sealed class ListLogger : ILogger<AuditLog>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public async Task ConcurrentAppendsFormOneUnbrokenChain()
    {
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => AppendAsync(Entry("config.host.update")), Ct)));

        await using var context = _db.NewContext();
        var report = await AuditChainVerifier.VerifyAsync(context, Ct);
        Assert.True(report.IsIntact, report.Break?.Description);
        Assert.Equal(16, report.VerifiedEvents);
    }

    [Fact]
    public async Task AWriterThatMissedTheLatestEventCantForkTheChain()
    {
        // What the unique PrevHash index is for: two writers that both chained to the same event. (With BEGIN
        // IMMEDIATE they can't both read it in time; this replays the stale read directly.)
        var first = await AppendAsync(Entry("config.host.update"));
        await AppendAsync(Entry("config.host.update"));
        var forked = first with { Id = AuditEventId.New(), PrevHash = first.Hash };
        forked = forked with { Hash = AuditChain.ComputeHash(forked) };

        await using var context = _db.NewContext();
        context.AuditEvents.Add(AuditMapping.ToEntity(forked));
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(Ct));

        Assert.Contains("UNIQUE constraint failed: AuditEvents.PrevHash", error.InnerException?.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string> InvalidActions =>
    [
        "", "config", "Config.host", "config..host", ".config.host", "config.host.", "config.host update", "config.hôst",
        "config." + new string('a', AuditLog.MaxActionLength),
    ];

    [Theory]
    [MemberData(nameof(InvalidActions))]
    public async Task ActionsMustBeDottedLowercaseNames(string action) =>
        await Assert.ThrowsAsync<ArgumentException>(() => AppendAsync(Entry(action)));

    [Theory]
    [InlineData("host")]
    [InlineData("host_")]
    [InlineData("_0123456789012345678901")]
    [InlineData("Host_0123456789012345678901")]
    [InlineData("host_012345678901234567890")] // 21 digits
    [InlineData("host_01234567890123456789-1")]
    [InlineData("host_0123456789012345678901 ")]
    public async Task TargetsMustBeTypedIdStrings(string target) =>
        await Assert.ThrowsAsync<ArgumentException>(() => AppendAsync(Entry("config.host.update") with { Target = target }));

    [Theory]
    [InlineData("aud_00000000001")] // Int64Ordered width
    [InlineData("authp_0123456789ABCDEFabcdef")] // a prefix this assembly doesn't know
    public async Task TargetsMayNameAnyTypedId(string target)
    {
        var record = await AppendAsync(Entry("config.host.update") with { Target = target });
        Assert.Equal(target, record.Target);
    }

    [Theory]
    [InlineData("[1, 2]")]
    [InlineData("\"text\"")]
    [InlineData("{")]
    public async Task DetailsMustBeAJsonObject(string details) =>
        await Assert.ThrowsAsync<ArgumentException>(() => AppendAsync(Entry("config.host.update") with { Details = details }));

    [Fact]
    public async Task DetailsWithALoneSurrogateAreRejected()
    {
        // SQLite text is UTF-8: it would store U+FFFD and the hash would no longer match. (Built at run time: an
        // attribute argument can't carry a lone surrogate.)
        var details = "{\"a\": \"" + (char)0xD800 + "\"}";

        await Assert.ThrowsAsync<ArgumentException>(() => AppendAsync(Entry("config.host.update") with { Details = details }));
    }

    [Fact]
    public async Task UndefinedVocabularyIsRejected()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => AppendAsync(Entry("config.host.update") with { Actor = default }));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => AppendAsync(Entry("config.host.update") with { Channel = (Channel)0 }));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => AppendAsync(Entry("config.host.update") with { Outcome = (AuditOutcome)9 }));
    }

    private async Task<AuditRecord> AppendAsync(AuditEntry entry)
    {
        await using var context = _db.NewContext();
        await using var transaction = await context.Database.BeginTransactionAsync(Ct);
        var record = await _log.AppendAsync(context, entry, Ct);
        await transaction.CommitAsync(Ct);
        return record;
    }

    private async Task<ConfigRevisionId> CommitRevisionAsync()
    {
        await using var context = _db.NewContext();
        await using var transaction = await context.Database.BeginTransactionAsync(Ct);
        var info = await new ConfigRevisionStore(_clock).AppendAsync(context, new NewRevision("{}"u8.ToArray(), "[]", Actor.Cli, Channel.Cli), Ct);
        await transaction.CommitAsync(Ct);
        return info.Id;
    }

    private static AuditEntry Entry(string action) => new(Actor.ForUser(UserId.New()), Channel.Dashboard, action, AuditOutcome.Success);
}
