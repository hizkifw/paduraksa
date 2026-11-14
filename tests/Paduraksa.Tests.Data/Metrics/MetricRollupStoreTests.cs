using System.Data.Common;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

using Paduraksa.Application.Metrics;
using Paduraksa.Core.Model;
using Paduraksa.Core.Options;
using Paduraksa.Proxy.Metrics;
using Paduraksa.Tests.Data.Config;

namespace Paduraksa.Tests.Data.Metrics;

public sealed class MetricRollupStoreTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly HostId App = HostId.New();
    private static readonly UpstreamId Web = UpstreamId.New();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MetricSampler Sampler(MetricRollupBacklog queue, TimeProvider time) => new(new MetricsRegistry(processors: 2, timeProvider: time), time, queue);

    private static MetricRollup Minute(MetricRollupSubject subject, string id, DateTimeOffset at, long requests, int p95 = 2_000, long errors = 0, int covered = 60) => new(
        subject,
        id,
        new MetricPoint
        {
            Start = at.ToUnixTimeSeconds() / 60 * 60,
            Width = 60,
            CoveredSeconds = covered,
            Requests = requests,
            Status2xx = requests - errors,
            Status5xx = errors,
            Errors = errors,
            BytesOut = requests * 100,
            BytesIn = requests * 10,
            TotalMicrosSum = requests * 1_000,
            TotalP50 = p95 / 2,
            TotalP95 = p95,
            TotalP99 = p95 * 2,
            UpstreamTimed = requests,
            UpstreamP95 = p95 / 2,
        });

    [Fact]
    public async Task AMinuteIsStoredUnderTheTypedIdAndTheMinuteAndReadBackAsWritten()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        MetricRollupStore store = new(database.Factory);

        await store.WriteAsync([Minute(MetricRollupSubject.Host, App.ToString(), Noon, 120, errors: 6)], Ct);

        var row = Assert.Single(await database.QueryAsync("SELECT \"Subject\", \"SubjectKey\", \"Minute\", \"Requests\", \"Status5xx\", \"TotalP95\" FROM \"MetricRollups\""));
        Assert.Equal([1L, App.ToString(), Noon.ToUnixTimeSeconds() / 60, 120L, 6L, 2_000L], row);
        var read = Assert.Single(await store.ReadSeriesAsync(MetricRollupSubject.Host, App.ToString(), Noon.AddMinutes(-5), Noon.AddMinutes(5), 1, Ct));
        Assert.Equal(120, read.Requests);
        Assert.Equal(6, read.Status5xx);
        Assert.Equal(2_000, read.TotalP95);
        Assert.Equal(12_000, read.BytesOut);
        Assert.Equal(1_200, read.BytesIn);
        Assert.Equal(60, read.CoveredSeconds);
        Assert.Equal(60, read.Width);
    }

    [Fact]
    public async Task TheRowsHaveInt64OrderedIdsThatSortInWriteOrder()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        MetricRollupStore store = new(database.Factory);

        await store.WriteAsync([Minute(MetricRollupSubject.Total, string.Empty, Noon, 1), Minute(MetricRollupSubject.Total, string.Empty, Noon.AddMinutes(1), 2)], Ct);

        var ids = (await database.QueryAsync("SELECT \"Id\" FROM \"MetricRollups\" ORDER BY \"Minute\"")).Select(r => (long)r[0]!).ToArray();
        Assert.Equal(2, ids.Length);
        Assert.True(ids[0] < ids[1]);
    }

    [Fact]
    public async Task AMinuteWrittenTwiceIsMergedIntoOneRowThatKeepsEveryCountAndEverySecondObserved()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        MetricRollupStore store = new(database.Factory);

        // A stop at second 20 flushed what the minute held; the next process went on to fill the rest of it.
        await store.WriteAsync([Minute(MetricRollupSubject.Host, App.ToString(), Noon, 100, p95: 1_000, errors: 4, covered: 20)], Ct);
        await store.WriteAsync([Minute(MetricRollupSubject.Host, App.ToString(), Noon, 300, p95: 3_000, errors: 8, covered: 35), Minute(MetricRollupSubject.Upstream, Web.ToString(), Noon, 120)], Ct);

        Assert.Equal(2L, await database.ScalarAsync("SELECT COUNT(*) FROM \"MetricRollups\""));
        var row = Assert.Single(await store.ReadSeriesAsync(MetricRollupSubject.Host, App.ToString(), Noon.AddMinutes(-1), Noon.AddMinutes(1), 1, Ct));
        Assert.Equal(400, row.Requests);
        Assert.Equal(12, row.Status5xx);
        Assert.Equal(12, row.Errors);
        Assert.Equal(400 * 1_000, row.TotalMicrosSum);
        Assert.Equal(400 * 100, row.BytesOut);
        Assert.Equal(400 * 10, row.BytesIn);
        Assert.Equal(55, row.CoveredSeconds);
        Assert.Equal(2_500, row.TotalP95);
    }

    [Fact]
    public async Task TheRefusalsByKindAreStoredMergedOnACollisionAndSummedByTheRangeReads()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        MetricRollupStore store = new(database.Factory);
        var first = Minute(MetricRollupSubject.Host, App.ToString(), Noon, 10, covered: 20);
        first = first with { Point = first.Point with { Refused = 5, Refusals = new RefusalCounts { RateLimit = 3, ProofOfWork = 2, NoRoute = 1 } } };
        var second = Minute(MetricRollupSubject.Host, App.ToString(), Noon, 10, covered: 30);
        second = second with { Point = second.Point with { Refused = 4, Refusals = new RefusalCounts { RateLimit = 1, AccessList = 3, UnknownHost = 2 } } };
        var next = Minute(MetricRollupSubject.Host, App.ToString(), Noon.AddMinutes(1), 10);
        next = next with { Point = next.Point with { Refused = 1, Refusals = new RefusalCounts { Other = 1 } } };

        // The process stopped inside the minute, and the next one went on to fill it.
        await store.WriteAsync([first], Ct);
        await store.WriteAsync([second, next], Ct);

        var minute = Assert.Single(await store.ReadSeriesAsync(MetricRollupSubject.Host, App.ToString(), Noon, Noon, 1, Ct));
        Assert.Equal(new RefusalCounts { RateLimit = 4, ProofOfWork = 2, AccessList = 3, NoRoute = 1, UnknownHost = 2 }, minute.Refusals);
        Assert.Equal(9, minute.Refused);
        Assert.Equal(0, minute.UnknownRefused);
        var summary = (await store.SummariseAsync(MetricRollupSubject.Host, Noon, Noon.AddMinutes(1), Ct))[App.ToString()];
        Assert.Equal(new RefusalCounts { RateLimit = 4, ProofOfWork = 2, AccessList = 3, Other = 1, NoRoute = 1, UnknownHost = 2 }, summary.Refusals);
        Assert.Equal(10, summary.Refused);
        var step = Assert.Single(await store.ReadSeriesAsync(MetricRollupSubject.Host, App.ToString(), Noon, Noon.AddMinutes(1), 2, Ct));
        Assert.Equal(summary.Refusals, step.Refusals);
    }

    [Fact]
    public async Task AMergedMinuteNeverClaimsMoreThanSixtySeconds()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        MetricRollupStore store = new(database.Factory);

        await store.WriteAsync([Minute(MetricRollupSubject.Total, string.Empty, Noon, 1, covered: 40)], Ct);
        await store.WriteAsync([Minute(MetricRollupSubject.Total, string.Empty, Noon, 1, covered: 40)], Ct);

        Assert.Equal(60, Assert.Single(await store.ReadSeriesAsync(MetricRollupSubject.Total, string.Empty, Noon, Noon, 1, Ct)).CoveredSeconds);
    }

    [Fact]
    public async Task TheSameMinuteTwiceInOneWriteIsMergedToo()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        MetricRollupStore store = new(database.Factory);

        await store.WriteAsync(
        [
            Minute(MetricRollupSubject.Host, App.ToString(), Noon, 10, covered: 10),
            Minute(MetricRollupSubject.Host, App.ToString(), Noon, 5, covered: 10),
        ], Ct);

        var row = Assert.Single(await store.ReadSeriesAsync(MetricRollupSubject.Host, App.ToString(), Noon, Noon, 1, Ct));
        Assert.Equal(15, row.Requests);
        Assert.Equal(20, row.CoveredSeconds);
    }

    [Fact]
    public async Task AnIdleMinuteIsAStoredRowWithItsObservedSecondsAndNoRequests()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        MetricRollupStore store = new(database.Factory);

        await store.WriteAsync([Minute(MetricRollupSubject.Total, string.Empty, Noon, 0, covered: 30)], Ct);

        var row = Assert.Single(await store.ReadSeriesAsync(MetricRollupSubject.Total, string.Empty, Noon, Noon, 1, Ct));
        Assert.Equal(0, row.Requests);
        Assert.Equal(30, row.CoveredSeconds);
        Assert.Equal(0, row.RequestsPerSecond);
    }

    [Fact]
    public async Task TheStoreRefusesTwoRowsForOneObjectAndMinute()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        MetricRollupStore store = new(database.Factory);
        await store.WriteAsync([Minute(MetricRollupSubject.Host, App.ToString(), Noon, 1)], Ct);

        await Assert.ThrowsAnyAsync<Exception>(() => database.ExecuteAsync(
            $"INSERT INTO \"MetricRollups\" (\"Id\", \"Subject\", \"SubjectKey\", \"Minute\", \"Requests\", \"Status1xx\", \"Status2xx\", \"Status3xx\", \"Status4xx\", \"Status5xx\", \"NoResponse\", \"Refused\", \"Errors\", \"BytesOut\", \"TotalMicrosSum\", \"UpstreamMicrosSum\", \"UpstreamTimed\", \"TotalP50\", \"TotalP95\", \"TotalP99\", \"UpstreamP50\", \"UpstreamP95\", \"UpstreamP99\") "
            + $"VALUES (999999999999, 1, '{App}', {Noon.ToUnixTimeSeconds() / 60}, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)"));
    }

    [Fact]
    public async Task ASubjectOutsideTheEnumIsRefusedByTheSchema()
    {
        await using var database = await TestConfigDatabase.CreateAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => database.ExecuteAsync(
            "INSERT INTO \"MetricRollups\" (\"Id\", \"Subject\", \"SubjectKey\", \"Minute\", \"Requests\", \"Status1xx\", \"Status2xx\", \"Status3xx\", \"Status4xx\", \"Status5xx\", \"NoResponse\", \"Refused\", \"Errors\", \"BytesOut\", \"TotalMicrosSum\", \"UpstreamMicrosSum\", \"UpstreamTimed\", \"TotalP50\", \"TotalP95\", \"TotalP99\", \"UpstreamP50\", \"UpstreamP95\", \"UpstreamP99\") "
            + "VALUES (1, 7, '', 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)"));
    }

    [Fact]
    public async Task RetentionDeletesMinutesBeforeTheCutoffAcrossEverySeries()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        MetricRollupStore store = new(database.Factory);
        await store.WriteAsync(
        [
            Minute(MetricRollupSubject.Host, App.ToString(), Noon.AddDays(-20), 5),
            Minute(MetricRollupSubject.Upstream, Web.ToString(), Noon.AddDays(-15), 5),
            Minute(MetricRollupSubject.Total, string.Empty, Noon.AddDays(-14).AddMinutes(1), 5),
            Minute(MetricRollupSubject.Host, App.ToString(), Noon.AddDays(-1), 5),
        ], Ct);

        var removed = await store.PruneAsync(Noon.AddDays(-14), Ct);

        Assert.Equal(2, removed);
        Assert.Equal(2L, await database.ScalarAsync("SELECT COUNT(*) FROM \"MetricRollups\""));
    }

    [Fact]
    public async Task ASeriesIsGroupedIntoStepsAndCombinedWithRequestWeightedPercentiles()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        MetricRollupStore store = new(database.Factory);
        var start = new DateTimeOffset(2026, 11, 12, 11, 0, 0, TimeSpan.Zero);
        await store.WriteAsync(
        [
            Minute(MetricRollupSubject.Total, string.Empty, start, 100, p95: 1_000),
            Minute(MetricRollupSubject.Total, string.Empty, start.AddMinutes(1), 300, p95: 3_000, errors: 3),
            Minute(MetricRollupSubject.Total, string.Empty, start.AddMinutes(10), 50, p95: 8_000),
            Minute(MetricRollupSubject.Host, App.ToString(), start, 999),
        ], Ct);

        var steps = await store.ReadSeriesAsync(MetricRollupSubject.Total, string.Empty, start, start.AddHours(1), 6, Ct);

        Assert.Equal(2, steps.Count);
        Assert.Equal(400, steps[0].Requests);
        Assert.Equal(3, steps[0].Status5xx);
        Assert.Equal(2_500, steps[0].TotalP95);
        Assert.Equal(400 * 100, steps[0].BytesOut);
        Assert.Equal(400 * 10, steps[0].BytesIn);
        Assert.Equal(2 * 60, steps[0].CoveredSeconds);
        Assert.Equal(6 * 60, steps[0].Width);
        Assert.Equal(50, steps[1].Requests);
        Assert.Equal(8_000, steps[1].TotalP95);
        Assert.True(steps[0].Start < steps[1].Start);
    }

    [Fact]
    public async Task ASummaryIsOnePointPerObjectInTheRange()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        MetricRollupStore store = new(database.Factory);
        HostId other = HostId.New();
        await store.WriteAsync(
        [
            Minute(MetricRollupSubject.Host, App.ToString(), Noon, 100),
            Minute(MetricRollupSubject.Host, App.ToString(), Noon.AddMinutes(1), 50),
            Minute(MetricRollupSubject.Host, other.ToString(), Noon, 7),
            Minute(MetricRollupSubject.Host, string.Empty, Noon, 3),
            Minute(MetricRollupSubject.Host, App.ToString(), Noon.AddDays(-3), 1_000),
            Minute(MetricRollupSubject.Upstream, Web.ToString(), Noon, 9),
        ], Ct);

        var summary = await store.SummariseAsync(MetricRollupSubject.Host, Noon.AddHours(-1), Noon.AddHours(1), Ct);

        Assert.Equal(3, summary.Count);
        Assert.Equal(150, summary[App.ToString()].Requests);
        Assert.Equal(7, summary[other.ToString()].Requests);
        Assert.Equal(3, summary[string.Empty].Requests);
    }

    [Fact]
    public async Task TheAggregatesAreSummedInSqlAndWalkTheIndexNotTheTable()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        MetricRollupStore store = new(database.Factory);
        await store.WriteAsync(
        [
            Minute(MetricRollupSubject.Host, App.ToString(), Noon, 100, p95: 1_000),
            Minute(MetricRollupSubject.Host, App.ToString(), Noon.AddMinutes(1), 300, p95: 3_000),
        ], Ct);
        PlanCapture capture = new();
        var from = Noon.AddHours(-1).ToUnixTimeSeconds() / 60;
        var to = Noon.AddHours(1).ToUnixTimeSeconds() / 60;

        await using (var context = database.NewContext(capture))
        {
            var summary = await MetricRollupStore.SummaryQuery(context, MetricRollupSubject.Host, from, to).ToListAsync(Ct);
            var series = await MetricRollupStore.SeriesQuery(context, MetricRollupSubject.Host, App.ToString(), from, to, 6).ToListAsync(Ct);

            Assert.Equal(400, Assert.Single(summary).Requests);
            Assert.Equal(400, Assert.Single(series).Requests);
            Assert.Equal(2_500.0, Assert.Single(summary).WeightedTotalP95 / 400, 3);
        }

        Assert.Equal(2, capture.Statements.Count);
        // One grouped statement each, with the sums in SQL: no row of the range becomes an entity.
        Assert.All(capture.Statements, statement => Assert.Contains("GROUP BY", statement.Sql, StringComparison.Ordinal));
        Assert.All(capture.Statements, statement => Assert.Contains("SUM(", statement.Sql, StringComparison.Ordinal));
        // Both are answered from the (Subject, SubjectKey, Minute) or the Minute index, never a scan of the table.
        Assert.All(capture.Statements, statement => Assert.Contains("INDEX", statement.Plan, StringComparison.Ordinal));
        Assert.All(capture.Statements, statement => Assert.DoesNotContain("SCAN MetricRollups\n", statement.Plan + "\n", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheJobWritesWhatTheQueueHoldsAndRetainsOnlyTheConfiguredTime()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        ManualTimeProvider time = new(Noon);
        MetricRollupBacklog queue = new();
        MetricRollupStore store = new(database.Factory);
        MetricRollupJob job = new(Sampler(queue, time), store, new SecurityRollupStore(database.Factory), new MetricRollupOptions { Retention = TimeSpan.FromDays(7) }, time, NullLogger<MetricRollupJob>.Instance);
        queue.Enqueue(Minute(MetricRollupSubject.Host, App.ToString(), Noon.AddDays(-8), 10));
        queue.Enqueue(Minute(MetricRollupSubject.Host, App.ToString(), Noon.AddMinutes(-1), 20));

        await job.WriteAsync(Ct);
        Assert.Equal(0, queue.Count);
        Assert.Equal(2L, await database.ScalarAsync("SELECT COUNT(*) FROM \"MetricRollups\""));

        await job.PruneAsync(Ct);

        Assert.Equal([20L], (await database.QueryAsync("SELECT \"Requests\" FROM \"MetricRollups\"")).Select(r => (long)r[0]!));
    }

    [Fact]
    public async Task ARetentionChangedWhileTheJobRunsAppliesAtTheNextPrune()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        ManualTimeProvider time = new(Noon);
        MetricRollupBacklog queue = new();
        LiveOptions<MetricRollupOptions> options = new(new MetricRollupOptions { Retention = TimeSpan.FromDays(30) });
        MetricRollupJob job = new(Sampler(queue, time), new MetricRollupStore(database.Factory), new SecurityRollupStore(database.Factory), options, time, NullLogger<MetricRollupJob>.Instance);
        queue.Enqueue(Minute(MetricRollupSubject.Host, App.ToString(), Noon.AddDays(-8), 10));
        queue.Enqueue(Minute(MetricRollupSubject.Host, App.ToString(), Noon.AddMinutes(-1), 20));
        await job.WriteAsync(Ct);

        await job.PruneAsync(Ct);
        Assert.Equal(2L, await database.ScalarAsync("SELECT COUNT(*) FROM \"MetricRollups\""));

        options.Set(options.Current with { Retention = TimeSpan.FromDays(7) });
        await job.PruneAsync(Ct);
        Assert.Equal([20L], (await database.QueryAsync("SELECT \"Requests\" FROM \"MetricRollups\"")).Select(r => (long)r[0]!));
    }

    [Fact]
    public async Task AWriteThatFailsPutsTheMinutesBackForTheNextInterval()
    {
        await using var database = TestConfigDatabase.Empty();
        MetricRollupBacklog queue = new();
        ManualTimeProvider time = new(Noon);
        MetricRollupJob job = new(Sampler(queue, time), new MetricRollupStore(database.Factory), new SecurityRollupStore(database.Factory), new MetricRollupOptions(), time, NullLogger<MetricRollupJob>.Instance);
        queue.Enqueue(Minute(MetricRollupSubject.Host, App.ToString(), Noon, 10));

        await job.WriteAsync(Ct);

        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public void AQueueThatIsNotDrainedKeepsTheNewestMinutesAndCountsWhatItLost()
    {
        MetricRollupBacklog queue = new();

        for (var i = 0; i < MetricRollupBacklog.Capacity + 5; i++)
        {
            queue.Enqueue(Minute(MetricRollupSubject.Total, string.Empty, Noon.AddMinutes(i), i));
        }

        Assert.Equal(5, queue.Dropped);
        Assert.Equal(MetricRollupBacklog.Capacity, queue.Count);
        Assert.Equal(5, queue.Drain(1)[0].Point.Requests);
    }

    [Fact]
    public async Task EveryMinuteOfTheRangeIsReadBackWithItsSubjectAndCoverageForTheSeed()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        MetricRollupStore store = new(database.Factory);
        await store.WriteAsync(
        [
            Minute(MetricRollupSubject.Total, string.Empty, Noon.AddMinutes(-90), 1),
            Minute(MetricRollupSubject.Total, string.Empty, Noon.AddMinutes(-30), 5, covered: 40),
            Minute(MetricRollupSubject.Host, App.ToString(), Noon.AddMinutes(-30), 4),
            Minute(MetricRollupSubject.Upstream, Web.ToString(), Noon.AddMinutes(-2), 3),
            Minute(MetricRollupSubject.Total, string.Empty, Noon.AddMinutes(10), 9),
        ], Ct);

        var minutes = await store.ReadMinutesAsync(Noon.AddHours(-1), Noon, Ct);

        Assert.Equal(3, minutes.Count);
        var total = Assert.Single(minutes, m => m.Subject == MetricRollupSubject.Total);
        Assert.Equal(5, total.Point.Requests);
        Assert.Equal(40, total.Point.CoveredSeconds);
        Assert.Equal(60, total.Point.Width);
        Assert.Equal(Noon.AddMinutes(-30).ToUnixTimeSeconds(), total.Point.Start);
        Assert.Equal(App.ToString(), Assert.Single(minutes, m => m.Subject == MetricRollupSubject.Host).SubjectId);
    }

    [Fact]
    public async Task TheJobSeedsTheSamplerWithTheHourBeforeItStartsAndAnUnreadableDatabaseDoesNotStopIt()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        ManualTimeProvider time = new(Noon);
        MetricRollupStore store = new(database.Factory);
        await store.WriteAsync(
        [
            Minute(MetricRollupSubject.Total, string.Empty, Noon.AddMinutes(-20), 50),
            Minute(MetricRollupSubject.Host, App.ToString(), Noon.AddMinutes(-20), 40),
            Minute(MetricRollupSubject.Total, string.Empty, Noon.AddHours(-3), 70),
        ], Ct);
        var sampler = Sampler(new MetricRollupBacklog(), time);

        await new MetricRollupJob(sampler, store, new SecurityRollupStore(database.Factory), new MetricRollupOptions(), time, NullLogger<MetricRollupJob>.Instance).SeedAsync(Ct);

        Assert.Equal(50, Assert.Single(sampler.History(MetricRollupSubject.Total, Guid.Empty)).Requests);
        Assert.Equal(40, Assert.Single(sampler.History(MetricRollupSubject.Host, App.Value)).Requests);

        await using var down = TestConfigDatabase.Empty();
        var cold = Sampler(new MetricRollupBacklog(), time);
        await new MetricRollupJob(cold, new MetricRollupStore(down.Factory), new SecurityRollupStore(down.Factory), new MetricRollupOptions(), time, NullLogger<MetricRollupJob>.Instance).SeedAsync(Ct);
        Assert.Empty(cold.History(MetricRollupSubject.Total, Guid.Empty));
    }

    [Fact]
    public async Task TheSeedReadWalksTheMinuteIndexNotTheTable()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        MetricRollupStore store = new(database.Factory);
        await store.WriteAsync([Minute(MetricRollupSubject.Total, string.Empty, Noon, 1)], Ct);
        PlanCapture capture = new();

        await using (var context = database.NewContext(capture))
        {
            Assert.Single(await MetricRollupStore.MinutesQuery(context, Noon.ToUnixTimeSeconds() / 60 - 60, Noon.ToUnixTimeSeconds() / 60).ToListAsync(Ct));
        }

        var statement = Assert.Single(capture.Statements);
        Assert.Contains("USING INDEX IX_MetricRollups_Minute", statement.Plan, StringComparison.Ordinal);
    }

    /// <summary>Records each statement EF runs and the plan SQLite makes for it, asked on the same connection with the same parameters.</summary>
    internal sealed class PlanCapture : DbCommandInterceptor
    {
        public List<(string Sql, string Plan)> Statements { get; } = [];

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            using var explain = command.Connection!.CreateCommand();
            explain.CommandText = "EXPLAIN QUERY PLAN " + command.CommandText;
            foreach (DbParameter parameter in command.Parameters)
            {
                var copy = explain.CreateParameter();
                copy.ParameterName = parameter.ParameterName;
                copy.Value = parameter.Value;
                explain.Parameters.Add(copy);
            }

            List<string> lines = [];
            using (var reader = explain.ExecuteReader())
            {
                while (reader.Read())
                {
                    lines.Add(reader.GetString(reader.FieldCount - 1));
                }
            }

            Statements.Add((command.CommandText, string.Join("\n", lines)));
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ReaderExecuting(command, eventData, result));
    }
}
