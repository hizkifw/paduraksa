using System.Data.Common;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

using Paduraksa.Application.Analytics;
using Paduraksa.Core.Model;
using Paduraksa.Data;
using Paduraksa.Proxy.Analytics;
using Paduraksa.Proxy.Logging;
using Paduraksa.Tests.Data.Config;

namespace Paduraksa.Tests.Data.Analytics;

/// <summary>The analytics' rollups in the config database: written once and merged on a second write, rolled up into hours, read back across tiers, pruned, and read through an index.</summary>
public sealed class AnalyticsRollupStoreTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly HostId App = HostId.New();
    private static readonly HostId Other = HostId.New();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AnalyticsRollup Coverage(DateTimeOffset at, int seconds = 300) =>
        new(AnalyticsSubject.Coverage, string.Empty, at.ToUnixTimeSeconds(), seconds, 0, 0, 0, null);

    /// <summary>A host's bucket with <paramref name="requests"/> requests from visitors <paramref name="firstVisitor"/> up.</summary>
    internal static AnalyticsRollup Host(HostId host, DateTimeOffset at, int requests, ulong firstVisitor = 1, string path = "/p", int covered = 300, int status = 200, long bytes = 100)
    {
        TrafficBucket bucket = new();
        for (var i = 0; i < requests; i++)
        {
            var entry = new AccessLogEntry { Path = path, Method = "GET", Protocol = "HTTP/1.1", StatusCode = status, ResponseBytes = bytes, Host = "app.test", UserAgent = "curl/8.5.0", TotalTicks = TimeSpan.TicksPerMillisecond * 3 };
            bucket.Record(in entry, MixOf(firstVisitor + (ulong)i), UserAgentClassifier.Classify(entry.UserAgent), false);
        }

        return new AnalyticsRollup(
            host.IsEmpty ? AnalyticsSubject.Unmatched : AnalyticsSubject.Host,
            host.IsEmpty ? string.Empty : host.ToString(),
            at.ToUnixTimeSeconds(),
            covered,
            bucket.Requests,
            bucket.BytesOut,
            bucket.ServerErrors,
            TrafficBucketCodec.Encode(bucket));
    }

    private static ulong MixOf(ulong index)
    {
        var z = index + 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private static AnalyticsRollupStore StoreOver(TestConfigDatabase database, AnalyticsBacklog? backlog = null) => new(database.Factory, backlog ?? new AnalyticsBacklog());

    [Fact]
    public async Task ABucketIsStoredUnderTheTypedHostIdAndItsStartAndReadBack()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        AnalyticsBacklog backlog = new();
        using var store = StoreOver(database, backlog);
        backlog.Add([Coverage(Noon), Host(App, Noon, 12, status: 502)]);

        await store.WritePendingAsync(Ct);

        var rows = await database.QueryAsync("SELECT \"Tier\", \"Subject\", \"HostKey\", \"Start\", \"CoveredSeconds\", \"Requests\", \"ServerErrors\", \"BytesOut\" FROM \"AnalyticsRollups\" ORDER BY \"Subject\"");
        Assert.Equal(2, rows.Count);
        Assert.Equal([0L, 0L, string.Empty, Noon.ToUnixTimeSeconds(), 300L, 0L, 0L, 0L], rows[0]);
        Assert.Equal([0L, 1L, App.ToString(), Noon.ToUnixTimeSeconds(), 300L, 12L, 12L, 1_200L], rows[1]);
        var read = await store.ReadAsync(AnalyticsScope.All, Noon.AddMinutes(-10), Noon.AddMinutes(10), Ct);
        Assert.Empty(read.Unwritten);
        var host = Assert.Single(read.Rows, static row => row.Subject == AnalyticsSubject.Host);
        Assert.Equal(12, TrafficBucketCodec.Decode(host.Bucket!)!.Requests);
        Assert.Equal(AnalyticsTier.Fine, host.Tier);
    }

    [Fact]
    public async Task TheRowsHaveInt64OrderedIdsThatSortInWriteOrder()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        AnalyticsBacklog backlog = new();
        using var store = StoreOver(database, backlog);
        backlog.Add([Host(App, Noon, 1), Host(App, Noon.AddMinutes(5), 1)]);

        await store.WritePendingAsync(Ct);

        var ids = (await database.QueryAsync("SELECT \"Id\" FROM \"AnalyticsRollups\" ORDER BY \"Start\"")).Select(static row => (long)row[0]!).ToArray();
        Assert.True(ids[0] < ids[1]);
    }

    [Fact]
    public async Task ABucketWrittenTwiceIsMergedIntoOneRowThatKeepsEveryCountAndEverySecondObserved()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        AnalyticsBacklog backlog = new();
        using var store = StoreOver(database, backlog);
        // A restart inside the bucket: the stop flushed 100 seconds of it, and the start went on to fill 150 more.
        backlog.Add([Coverage(Noon, 100), Host(App, Noon, 10, firstVisitor: 1, covered: 100)]);
        await store.WritePendingAsync(Ct);
        backlog.Add([Coverage(Noon, 150), Host(App, Noon, 6, firstVisitor: 6, path: "/q", covered: 150)]);

        await store.WritePendingAsync(Ct);

        Assert.Equal(2L, await database.ScalarAsync("SELECT COUNT(*) FROM \"AnalyticsRollups\""));
        var rows = (await store.ReadAsync(AnalyticsScope.All, Noon, Noon.AddMinutes(5), Ct)).Rows;
        Assert.Equal(250, Assert.Single(rows, static row => row.Subject == AnalyticsSubject.Coverage).CoveredSeconds);
        var host = Assert.Single(rows, static row => row.Subject == AnalyticsSubject.Host);
        Assert.Equal(16, host.Requests);
        var bucket = TrafficBucketCodec.Decode(host.Bucket!)!;
        Assert.Equal(16, bucket.Requests);
        Assert.Equal(["/p", "/q"], bucket.Paths.Top(5).Select(static hitter => hitter.Key).Order());
        // Visitors 1..10 and 6..11: eleven distinct, not sixteen.
        Assert.Equal(11, bucket.Visitors.Estimate());
    }

    [Fact]
    public async Task ACoverageRowNeverClaimsMoreThanTheBucketsWidth()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        AnalyticsBacklog backlog = new();
        using var store = StoreOver(database, backlog);
        backlog.Add([Coverage(Noon, 250)]);
        await store.WritePendingAsync(Ct);
        backlog.Add([Coverage(Noon, 250)]);

        await store.WritePendingAsync(Ct);

        Assert.Equal(300L, await database.ScalarAsync("SELECT \"CoveredSeconds\" FROM \"AnalyticsRollups\""));
    }

    [Fact]
    public async Task AClosedBucketIsInTheReadWhetherOrNotItsWriteHasHappened()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        AnalyticsBacklog backlog = new();
        using var store = StoreOver(database, backlog);
        backlog.Add([Host(App, Noon, 4)]);

        var before = await store.ReadAsync(AnalyticsScope.All, Noon, Noon.AddMinutes(5), Ct);
        await store.WritePendingAsync(Ct);
        var after = await store.ReadAsync(AnalyticsScope.All, Noon, Noon.AddMinutes(5), Ct);

        // Waiting, then stored: the same bucket once each time, never missing and never twice.
        Assert.Empty(before.Rows);
        Assert.Equal(4, Assert.Single(before.Unwritten).Requests);
        Assert.Equal(4, Assert.Single(after.Rows).Requests);
        Assert.Empty(after.Unwritten);
    }

    [Fact]
    public async Task AFailedWriteKeepsTheBucketsForTheNextTryAndTheyStayVisible()
    {
        await using var down = TestConfigDatabase.Empty();
        AnalyticsBacklog backlog = new();
        using var store = StoreOver(down, backlog);
        backlog.Add([Host(App, Noon, 4)]);

        await Assert.ThrowsAnyAsync<Exception>(() => store.WritePendingAsync(Ct));

        Assert.Equal(1, backlog.Count);
        Assert.Single(backlog.Unwritten());
    }

    [Fact]
    public async Task TheBacklogNeverGrowsPastItsBound()
    {
        AnalyticsBacklog backlog = new();
        for (var i = 0; i < AnalyticsBacklog.Capacity + 500; i++)
        {
            backlog.Add([Coverage(Noon.AddMinutes(5 * i))]);
        }

        Assert.Equal(AnalyticsBacklog.Capacity, backlog.Count);
        // The oldest went, the newest stayed.
        Assert.Equal(Noon.AddMinutes(5 * 500).ToUnixTimeSeconds(), backlog.Unwritten()[0].Start);
    }

    [Fact]
    public async Task ARangeIsReadForOneHostOrAllAndAlwaysWithCoverage()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        AnalyticsBacklog backlog = new();
        using var store = StoreOver(database, backlog);
        backlog.Add([Coverage(Noon), Host(App, Noon, 3), Host(Other, Noon, 5), Host(default, Noon, 7)]);
        await store.WritePendingAsync(Ct);

        var all = (await store.ReadAsync(AnalyticsScope.All, Noon, Noon.AddMinutes(5), Ct)).Rows;
        var app = (await store.ReadAsync(AnalyticsScope.Host(App.ToString()), Noon, Noon.AddMinutes(5), Ct)).Rows;
        var unmatched = (await store.ReadAsync(AnalyticsScope.Unmatched, Noon, Noon.AddMinutes(5), Ct)).Rows;

        Assert.Equal(4, all.Count);
        Assert.Equal([0L, 3L], app.Select(static row => row.Requests).Order());
        Assert.Equal([0L, 7L], unmatched.Select(static row => row.Requests).Order());
        Assert.All(new[] { app, unmatched }, rows => Assert.Contains(rows, static row => row.Subject == AnalyticsSubject.Coverage));
    }

    // Three hours of five-minute buckets for two hosts, every bucket observed, with distinct visitors per bucket.
    private static async Task<(TestConfigDatabase Database, AnalyticsRollupStore Store, AnalyticsBacklog Backlog)> ThreeHoursAsync()
    {
        var database = await TestConfigDatabase.CreateAsync();
        AnalyticsBacklog backlog = new();
        var store = StoreOver(database, backlog);
        var hourStart = new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);
        List<AnalyticsRollup> rollups = [];
        for (var bucket = 0; bucket < 36; bucket++)
        {
            var at = hourStart.AddMinutes(5 * bucket);
            rollups.Add(Coverage(at));
            rollups.Add(Host(App, at, 10, firstVisitor: (ulong)(bucket * 10) + 1));
            rollups.Add(Host(Other, at, 2, firstVisitor: 1_000_000 + (ulong)bucket));
        }

        backlog.Add(rollups);
        await store.WritePendingAsync(Ct);
        return (database, store, backlog);
    }

    [Fact]
    public async Task AnHourThatHasPassedIsMergedIntoOneRowPerHostWithItsCoverageAndItsVisitorsAsAUnion()
    {
        var (database, store, _) = await ThreeHoursAsync();
        using (store)
        await using (database)
        {
            // 12:00 is the end of the third hour; ten minutes later it is rolled up, and the 12:00 hour (not yet buckets) isn't there.
            var rolled = await store.RollUpAsync(Noon.AddMinutes(10), Ct);

            Assert.Equal(3, rolled);
            var hourly = await database.QueryAsync("SELECT \"Subject\", \"HostKey\", \"Start\", \"CoveredSeconds\", \"Requests\" FROM \"AnalyticsRollups\" WHERE \"Tier\" = 1 AND \"Start\" = " + Noon.AddHours(-3).ToUnixTimeSeconds() + " ORDER BY \"Subject\", \"HostKey\"");
            Assert.Equal(3, hourly.Count);
            Assert.Equal([0L, string.Empty, Noon.AddHours(-3).ToUnixTimeSeconds(), 3_600L, 0L], hourly[0]);
            var read = (await store.ReadAsync(AnalyticsScope.Host(App.ToString()), Noon.AddHours(-3), Noon, Ct)).Rows;
            var hostHour = Assert.Single(read, row => row.Subject == AnalyticsSubject.Host && row.Start == Noon.AddHours(-3).ToUnixTimeSeconds());
            Assert.Equal(AnalyticsTier.Hour, hostHour.Tier);
            Assert.Equal(120, hostHour.Requests);
            // Twelve buckets of ten visitors, all different: a hundred and twenty, not the twelve a single bucket has.
            Assert.InRange(TrafficBucketCodec.Decode(hostHour.Bucket!)!.Visitors.Estimate(), 114, 126);
        }
    }

    [Fact]
    public async Task RollingUpTwiceDoesNothingTheSecondTimeAndAHourStillOpenIsLeftAlone()
    {
        var (database, store, _) = await ThreeHoursAsync();
        using (store)
        await using (database)
        {
            Assert.Equal(2, await store.RollUpAsync(Noon.AddMinutes(-1), Ct));
            Assert.Equal(0, await store.RollUpAsync(Noon.AddMinutes(-1), Ct));
            Assert.Equal(1, await store.RollUpAsync(Noon.AddMinutes(10), Ct));
            Assert.Equal(0, await store.RollUpAsync(Noon.AddMinutes(10), Ct));
            // 3 hours x (coverage + 2 hosts) at the hourly tier.
            Assert.Equal(9L, await database.ScalarAsync("SELECT COUNT(*) FROM \"AnalyticsRollups\" WHERE \"Tier\" = 1"));
        }
    }

    [Fact]
    public async Task ARangeIsReadFromHourlyRowsWhereWholeHoursAreRolledUpAndFromFiveMinuteRowsAtTheEdges()
    {
        var (database, store, _) = await ThreeHoursAsync();
        using (store)
        await using (database)
        {
            await store.RollUpAsync(Noon.AddMinutes(10), Ct);

            // 09:20 to 11:40: 09:20-10:00 (8 buckets) and 11:00-11:40 (9, the one starting at 11:40 included) are five-minute rows,
            // 10:00-11:00 is one hourly row.
            var read = await store.ReadAsync(AnalyticsScope.Host(App.ToString()), Noon.AddMinutes(-160), Noon.AddMinutes(-20), Ct);

            var hosts = read.Rows.Where(static row => row.Subject == AnalyticsSubject.Host).ToList();
            Assert.Single(hosts, static row => row.Tier == AnalyticsTier.Hour);
            Assert.Equal(8 + 9, hosts.Count(static row => row.Tier == AnalyticsTier.Fine));
            // Nothing counted twice and nothing missed: the edges' 17 buckets and the hour's 12 are 29 buckets of 10.
            Assert.Equal(290, hosts.Sum(static row => row.Requests));
        }
    }

    [Fact]
    public async Task ARangeWithNoRolledUpHourIsAllFiveMinuteRows()
    {
        var (database, store, _) = await ThreeHoursAsync();
        using (store)
        await using (database)
        {
            var read = await store.ReadAsync(AnalyticsScope.Host(App.ToString()), Noon.AddHours(-3), Noon, Ct);

            Assert.All(read.Rows, static row => Assert.Equal(AnalyticsTier.Fine, row.Tier));
            Assert.Equal(360, read.Rows.Where(static row => row.Subject == AnalyticsSubject.Host).Sum(static row => row.Requests));
        }
    }

    // Two whole days (10 and 11 November) with one bucket an hour for two hosts, every bucket observed.
    private static readonly DateTimeOffset FirstDay = new(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);

    private static async Task<(TestConfigDatabase Database, AnalyticsRollupStore Store)> TwoDaysAsync()
    {
        var database = await TestConfigDatabase.CreateAsync();
        AnalyticsBacklog backlog = new();
        var store = StoreOver(database, backlog);
        List<AnalyticsRollup> rollups = [];
        for (var hour = 0; hour < 48; hour++)
        {
            var at = FirstDay.AddHours(hour);
            rollups.Add(Coverage(at));
            rollups.Add(Host(App, at, 10, firstVisitor: (ulong)(hour * 10) + 1));
            rollups.Add(Host(Other, at, 1, firstVisitor: 1_000_000 + (ulong)hour));
        }

        backlog.Add(rollups);
        await store.WritePendingAsync(Ct);
        return (database, store);
    }

    [Fact]
    public async Task ADayWhoseHoursAreAllRolledUpIsMergedIntoOneRowPerHostAndAnOpenDayIsNot()
    {
        var (database, store) = await TwoDaysAsync();
        using (store)
        await using (database)
        {
            // Noon on the 12th: every hour to 11:00 is rolled up (48 of them), and the 10th and the 11th are complete days.
            var rolled = await store.RollUpAsync(FirstDay.AddDays(2).AddHours(12), Ct);

            Assert.Equal(48 + 2, rolled);
            var daily = await database.QueryAsync("SELECT \"Subject\", \"HostKey\", \"Start\", \"CoveredSeconds\", \"Requests\" FROM \"AnalyticsRollups\" WHERE \"Tier\" = 2 AND \"Start\" = " + FirstDay.ToUnixTimeSeconds() + " ORDER BY \"Subject\", \"HostKey\"");
            Assert.Equal(3, daily.Count);
            // 24 observed buckets of 300 seconds.
            Assert.Equal([0L, string.Empty, FirstDay.ToUnixTimeSeconds(), 7_200L, 0L], daily[0]);
            var host = daily.Single(row => (string)row[1]! == App.ToString());
            Assert.Equal(240L, host[4]);
            // A day that has not ended is never rolled up, and rolling again does nothing.
            Assert.Equal(0, await store.RollUpAsync(FirstDay.AddDays(2).AddHours(12), Ct));
            Assert.Equal(6L, await database.ScalarAsync("SELECT COUNT(*) FROM \"AnalyticsRollups\" WHERE \"Tier\" = 2"));
        }
    }

    [Fact]
    public async Task ARangeIsReadFromDailyRowsOnlyWhenTheCallerCanDrawADayAndNeverCountsAnythingTwice()
    {
        var (database, store) = await TwoDaysAsync();
        using (store)
        await using (database)
        {
            await store.RollUpAsync(FirstDay.AddDays(2).AddHours(12), Ct);
            var from = FirstDay.AddHours(-6);
            var to = FirstDay.AddDays(2).AddHours(12);

            var withDays = await store.ReadAsync(AnalyticsScope.Host(App.ToString()), from, to, AnalyticsTier.Day, Ct);
            var withHours = await store.ReadAsync(AnalyticsScope.Host(App.ToString()), from, to, AnalyticsTier.Hour, Ct);

            // Two daily rows in the middle, the rest hourly or five-minute; the hourly read has no daily row at all.
            Assert.Equal(2, withDays.Rows.Count(row => row.Subject == AnalyticsSubject.Host && row.Tier == AnalyticsTier.Day));
            Assert.DoesNotContain(withHours.Rows, row => row.Tier == AnalyticsTier.Day);
            Assert.Equal(480, withDays.Rows.Where(row => row.Subject == AnalyticsSubject.Host).Sum(row => row.Requests));
            Assert.Equal(480, withHours.Rows.Where(row => row.Subject == AnalyticsSubject.Host).Sum(row => row.Requests));
            Assert.True(withDays.Rows.Count < withHours.Rows.Count);
        }
    }

    [Fact]
    public async Task RetentionDeletesOldRowsOfBothTiersAndFiveMinuteRowsOnlyOnceTheirHourIsRolledUp()
    {
        var (database, store, _) = await ThreeHoursAsync();
        using (store)
        await using (database)
        {
            // Before rolling up, nothing is deleted for being merely older than the fine retention.
            Assert.Equal(0, await store.PruneAsync(Noon.AddDays(-30), Noon.AddHours(-1), Ct));
            Assert.Equal(108L, await database.ScalarAsync("SELECT COUNT(*) FROM \"AnalyticsRollups\" WHERE \"Tier\" = 0"));

            await store.RollUpAsync(Noon.AddMinutes(10), Ct);
            // Rolled up: the five-minute rows before 11:00 go, the hour 11:00-12:00 stays, and no hourly row is touched.
            var removed = await store.PruneAsync(Noon.AddDays(-30), Noon.AddHours(-1), Ct);

            Assert.Equal(72, removed);
            Assert.Equal(36L, await database.ScalarAsync("SELECT COUNT(*) FROM \"AnalyticsRollups\" WHERE \"Tier\" = 0"));
            Assert.Equal(9L, await database.ScalarAsync("SELECT COUNT(*) FROM \"AnalyticsRollups\" WHERE \"Tier\" = 1"));

            // Past the retention, everything goes, whatever its tier.
            Assert.Equal(45, await store.PruneAsync(Noon.AddMinutes(1), Noon.AddMinutes(1), Ct));
            Assert.Equal(0L, await database.ScalarAsync("SELECT COUNT(*) FROM \"AnalyticsRollups\""));
        }
    }

    [Fact]
    public async Task TheJobWritesRollsUpAndPrunesAndAFailureOfOneDoesNotStopTheOthers()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        AnalyticsBacklog backlog = new();
        using var store = StoreOver(database, backlog);
        ManualTimeProvider time = new(Noon.AddHours(2));
        AnalyticsRollupJob job = new(store, new AnalyticsOptions { Retention = TimeSpan.FromDays(30) }, time, NullLogger<AnalyticsRollupJob>.Instance);
        backlog.Add([Coverage(Noon), Host(App, Noon, 3)]);

        await job.WriteAsync(Ct);
        await job.RollUpAsync(Ct);
        await job.PruneAsync(Ct);

        Assert.Equal(0, backlog.Count);
        // The bucket's coverage and host rows, and the hour they are in (it ended more than ten minutes before the job's clock), rolled up.
        Assert.Equal(4L, await database.ScalarAsync("SELECT COUNT(*) FROM \"AnalyticsRollups\""));

        await using var down = TestConfigDatabase.Empty();
        using var brokenStore = StoreOver(down, backlog);
        backlog.Add([Host(App, Noon.AddHours(1), 1)]);
        AnalyticsRollupJob broken = new(brokenStore, new AnalyticsOptions(), time, NullLogger<AnalyticsRollupJob>.Instance);
        await broken.WriteAsync(Ct);
        await broken.RollUpAsync(Ct);
        await broken.PruneAsync(Ct);
        Assert.Equal(1, backlog.Count);
    }

    [Fact]
    public async Task EveryReadWalksAnIndexNotTheTable()
    {
        var (database, store, _) = await ThreeHoursAsync();
        using (store)
        await using (database)
        {
            await store.RollUpAsync(Noon.AddMinutes(10), Ct);
            PlanCapture capture = new();
            var from = Noon.AddHours(-2).ToUnixTimeSeconds();
            var to = Noon.ToUnixTimeSeconds();

            await using (var context = database.NewContext(capture))
            {
                foreach (var scope in new[] { AnalyticsScope.All, AnalyticsScope.Host(App.ToString()), AnalyticsScope.Unmatched })
                {
                    foreach (var tier in new[] { AnalyticsTier.Fine, AnalyticsTier.Hour, AnalyticsTier.Day })
                    {
                        _ = await AnalyticsRollupStore.RangeQuery(context, scope, tier, from, to).ToListAsync(Ct);
                        if (scope.Subject is not null)
                        {
                            _ = await AnalyticsRollupStore.CoverageQuery(context, tier, from, to).ToListAsync(Ct);
                        }
                    }
                }

                _ = await AnalyticsRollupStore.RolledEndAsync(context, Ct);
            }

            // Three tiers. All hosts: one statement a tier; one host or the unmatched: its rows and the coverage rows. Then the roll-up's watermark.
            Assert.Equal((3 * (1 + 2 + 2)) + 1, capture.Statements.Count);
            foreach (var (sql, plan) in capture.Statements)
            {
                Assert.True(plan.Contains("USING INDEX IX_AnalyticsRollups_", StringComparison.Ordinal) || plan.Contains("USING COVERING INDEX IX_AnalyticsRollups_", StringComparison.Ordinal), $"{sql}\n{plan}");
                Assert.DoesNotContain("SCAN AnalyticsRollups", plan, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task AHostsRangeIsASeekOnTheHostIndex()
    {
        var (database, store, _) = await ThreeHoursAsync();
        using (store)
        await using (database)
        {
            PlanCapture capture = new();
            await using (var context = database.NewContext(capture))
            {
                _ = await AnalyticsRollupStore.RangeQuery(context, AnalyticsScope.Host(App.ToString()), AnalyticsTier.Fine, Noon.AddHours(-1).ToUnixTimeSeconds(), Noon.ToUnixTimeSeconds()).ToListAsync(Ct);
            }

            Assert.Contains("IX_AnalyticsRollups_Tier_Subject_HostKey_Start (Tier=? AND Subject=? AND HostKey=? AND Start>? AND Start<?)", Assert.Single(capture.Statements).Plan, StringComparison.Ordinal);
        }
    }

    /// <summary>Records each statement EF runs and the plan SQLite makes for it, asked on the same connection with the same parameters.</summary>
    private sealed class PlanCapture : DbCommandInterceptor
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
