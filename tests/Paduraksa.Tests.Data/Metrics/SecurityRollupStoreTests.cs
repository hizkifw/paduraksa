using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Paduraksa.Application.Metrics;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Metrics;
using Paduraksa.Tests.Data.Config;

namespace Paduraksa.Tests.Data.Metrics;

public sealed class SecurityRollupStoreTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly AuthProviderId Provider = AuthProviderId.New();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SecurityRollup Minute(SecurityRollupSubject subject, string id, DateTimeOffset at, int covered, params (SecurityCounter Counter, long Value)[] counts)
    {
        var values = new long[SecurityPoint.Count];
        foreach (var (counter, value) in counts)
        {
            values[(int)counter] = value;
        }

        return new SecurityRollup(subject, id, new SecurityPoint(at.ToUnixTimeSeconds() / 60 * 60, 60, covered, values));
    }

    [Fact]
    public async Task AMinuteWrittenTwiceIsMergedIntoOneRowAndEveryCounterIsReadBack()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        SecurityRollupStore store = new(database.Factory);
        var everything = new long[SecurityPoint.Count];
        for (var i = 0; i < everything.Length; i++)
        {
            everything[i] = i + 1;
        }

        // A stop at second 20 wrote what the minute held; the next process filled the rest of it.
        await store.WriteAsync([new SecurityRollup(SecurityRollupSubject.Total, string.Empty, new SecurityPoint(Noon.ToUnixTimeSeconds(), 60, 20, everything))], Ct);
        await store.WriteAsync([Minute(SecurityRollupSubject.Total, string.Empty, Noon, 50, (SecurityCounter.RateLimited, 100))], Ct);

        Assert.Equal(1L, await database.ScalarAsync("SELECT COUNT(*) FROM \"SecurityRollups\""));
        var minute = Assert.Single(await store.ReadMinutesAsync(Noon, Noon, Ct));
        Assert.Equal(60, minute.Point.CoveredSeconds);
        for (var i = 0; i < SecurityPoint.Count; i++)
        {
            Assert.Equal(i + 1 + ((SecurityCounter)i == SecurityCounter.RateLimited ? 100 : 0), minute.Point.Counts[i]);
        }
    }

    [Fact]
    public async Task ASummaryIsOnePointPerObjectSummedInSqlOverTheSubjectsIndex()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        SecurityRollupStore store = new(database.Factory);
        await store.WriteAsync(
        [
            Minute(SecurityRollupSubject.Total, string.Empty, Noon, 60, (SecurityCounter.ChallengesServed, 4)),
            Minute(SecurityRollupSubject.Total, string.Empty, Noon.AddMinutes(1), 60, (SecurityCounter.ChallengesServed, 6), (SecurityCounter.SignInDenied, 1)),
            Minute(SecurityRollupSubject.Total, string.Empty, Noon.AddDays(-2), 60, (SecurityCounter.ChallengesServed, 1_000)),
            Minute(SecurityRollupSubject.AuthProvider, Provider.ToString(), Noon.AddMinutes(1), 60, (SecurityCounter.SignInDenied, 1)),
        ], Ct);
        MetricRollupStoreTests.PlanCapture capture = new();

        var summary = await store.SummariseAsync(SecurityRollupSubject.Total, Noon.AddHours(-1), Noon.AddHours(1), Ct);
        await using (var context = database.NewContext(capture))
        {
            Assert.Single(await SecurityRollupStore.SummaryQuery(context, SecurityRollupSubject.AuthProvider, Noon.ToUnixTimeSeconds() / 60, Noon.ToUnixTimeSeconds() / 60 + 5).ToListAsync(Ct));
        }

        var total = Assert.Single(summary).Value;
        Assert.Equal(10, total[SecurityCounter.ChallengesServed]);
        Assert.Equal(1, total[SecurityCounter.SignInDenied]);
        Assert.Equal(120, total.CoveredSeconds);
        var statement = Assert.Single(capture.Statements);
        Assert.Contains("GROUP BY", statement.Sql, StringComparison.Ordinal);
        Assert.Contains("INDEX", statement.Plan, StringComparison.Ordinal);
        Assert.DoesNotContain("SCAN SecurityRollups\n", statement.Plan + "\n", StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheJobWritesSeedsAndPrunesTheSecurityMinutesWithTheRequestOnes()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        ManualTimeProvider time = new(Noon);
        SecurityRollupStore security = new(database.Factory);
        MetricSampler sampler = new(new MetricsRegistry(processors: 2, timeProvider: time), time, new MetricRollupBacklog());
        MetricRollupJob job = new(sampler, new MetricRollupStore(database.Factory), security, new MetricRollupOptions { Retention = TimeSpan.FromDays(1) }, time, NullLogger<MetricRollupJob>.Instance);
        sampler.SecurityRollups.Enqueue(Minute(SecurityRollupSubject.Total, string.Empty, Noon.AddMinutes(-10), 60, (SecurityCounter.RateLimited, 3)));
        sampler.SecurityRollups.Enqueue(Minute(SecurityRollupSubject.AuthProvider, Provider.ToString(), Noon.AddMinutes(-10), 60, (SecurityCounter.SignInAllowed, 2)));
        await security.WriteAsync([Minute(SecurityRollupSubject.Total, string.Empty, Noon.AddDays(-3), 60, (SecurityCounter.RateLimited, 9))], Ct);

        await job.WriteAsync(Ct);
        await job.PruneAsync(Ct);

        Assert.Equal(0, sampler.SecurityRollups.Count);
        Assert.Equal(2L, await database.ScalarAsync("SELECT COUNT(*) FROM \"SecurityRollups\""));

        // A restart: the new sampler starts from the hour before.
        MetricSampler next = new(new MetricsRegistry(processors: 2, timeProvider: time), time, new MetricRollupBacklog(), new SecuritySources(new(), new(), new()));
        await new MetricRollupJob(next, new MetricRollupStore(database.Factory), security, new MetricRollupOptions(), time, NullLogger<MetricRollupJob>.Instance).SeedAsync(Ct);

        Assert.Equal(3, next.SummariseSecurity(SecurityRollupSubject.Total, TimeSpan.FromHours(1))[Guid.Empty][SecurityCounter.RateLimited]);
        Assert.Equal(2, next.SummariseSecurity(SecurityRollupSubject.AuthProvider, TimeSpan.FromHours(1))[Provider.Value][SecurityCounter.SignInAllowed]);
    }

    [Fact]
    public async Task ASubjectOutsideTheEnumIsRefusedByTheSchema()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        await new SecurityRollupStore(database.Factory).WriteAsync([Minute(SecurityRollupSubject.Total, string.Empty, Noon, 60)], Ct);

        await Assert.ThrowsAnyAsync<Exception>(() => database.ExecuteAsync("UPDATE \"SecurityRollups\" SET \"Subject\" = 7"));
    }
}
