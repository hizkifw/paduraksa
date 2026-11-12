using Paduraksa.Contracts.Metrics;
using Paduraksa.Core.Model;
using Paduraksa.Tests.Data.Config;

namespace Paduraksa.Tests.Data.Metrics;

/// <summary>The sampler, the rollup job and the store as a process lives and dies: what survives a stop, a restart, a crash.</summary>
public sealed class MetricTimelineTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid HostA = new("00000000-0000-7000-8000-00000000000a");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AGracefulStopWritesTheMinuteInProgressWithTheSecondsItObserved()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        ManualTimeProvider clock = new(Noon.AddSeconds(20.4));
        MetricProcess process = new(database, clock);
        var host = process.Host(HostA);
        await process.StartAsync();

        process.Request(host, 7);
        await process.RunUntilAsync(Noon.AddSeconds(45.4));
        await process.StopAsync();

        var total = Assert.Single(await database.QueryAsync("SELECT \"Minute\", \"CoveredSeconds\", \"Requests\" FROM \"MetricRollups\" WHERE \"Subject\" = 0"));
        Assert.Equal([Noon.ToUnixTimeSeconds() / 60, 25L, 7L], total);
        var hostRow = Assert.Single(await database.QueryAsync("SELECT \"Requests\" FROM \"MetricRollups\" WHERE \"Subject\" = 1"));
        Assert.Equal(7L, hostRow[0]);
    }

    [Fact]
    public async Task TheSamplerTicksOnTheWholeSecondsOfTheClock()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        ManualTimeProvider clock = new(Noon.AddSeconds(20.4));
        MetricProcess process = new(database, clock);
        await process.StartAsync();

        await process.RunUntilAsync(Noon.AddSeconds(30));
        await process.StopAsync();

        // From 20.4 s to 30 s the ticks are at 21, 22, ... 30: the window that closed at 30 s holds all 9.6 s of the stretch.
        var view = process.Sampler.Read(MetricRollupSubject.Total, Guid.Empty)!.Value;
        var bucket = Assert.Single(view.Points, point => point.Start == Noon.ToUnixTimeSeconds() + 20);
        Assert.Equal(10, bucket.CoveredSeconds);
    }

    [Fact]
    public async Task ARestartInsideAMinuteKeepsEveryCountOnceAndEverySecondObservedOnce()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        ManualTimeProvider clock = new(Noon.AddSeconds(20.4));
        MetricProcess first = new(database, clock);
        await first.StartAsync();
        first.Request(first.Host(HostA), 100);
        await first.RunUntilAsync(Noon.AddSeconds(40.4));
        await first.StopAsync();

        clock.Now = Noon.AddSeconds(50.2);
        MetricProcess second = new(database, clock);
        await second.StartAsync();
        second.Request(second.Host(HostA), 40);
        await second.RunUntilAsync(Noon.AddSeconds(55));

        // The hour so far: the first run's half of the minute from the database, this run's from memory, neither twice.
        var hour = await second.OverviewAsync(MetricsRange.Hour);
        Assert.Equal(140, hour.Series.Sum(point => point.RequestsPerSecond * point.CoveredSeconds), 6);
        Assert.Equal(140, Assert.Single(hour.Hosts).Requests);

        await second.RunUntilAsync(Noon.AddSeconds(75));
        await second.StopAsync();

        var totals = await database.QueryAsync("SELECT \"Minute\", \"Requests\", \"CoveredSeconds\" FROM \"MetricRollups\" WHERE \"Subject\" = 0 ORDER BY \"Minute\"");
        var minute = Noon.ToUnixTimeSeconds() / 60;
        // 20 s before the stop and 9.8 s of the same minute after the start; the 4.4 s between were nobody's.
        Assert.Equal([[minute, 140L, 30L], [minute + 1, 0L, 15L]], totals.Select(row => row.Select(value => (long)value!).ToArray()));
        Assert.Equal(140L, await database.ScalarAsync("SELECT SUM(\"Requests\") FROM \"MetricRollups\" WHERE \"Subject\" = 1"));
    }

    [Fact]
    public async Task AnOutageInsideTheHourIsAGapOfUnobservedMinutesBetweenTheHistoryAndTheNewRing()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        ManualTimeProvider clock = new(Noon);
        MetricProcess first = new(database, clock);
        await first.StartAsync();
        first.Request(first.Host(HostA), 600);
        await first.RunForAsync(TimeSpan.FromMinutes(10));
        await first.StopAsync();

        clock.Now = Noon.AddMinutes(40);
        MetricProcess second = new(database, clock);
        await second.StartAsync();
        await second.RunForAsync(TimeSpan.FromMinutes(5));

        var series = (await second.OverviewAsync(MetricsRange.Hour)).Series;

        var gap = series.Where(point => point.CoveredSeconds == 0).ToArray();
        Assert.Equal(30, gap.Length);
        Assert.All(gap, point => Assert.Equal(60, point.WidthSeconds));
        Assert.Equal(Noon.AddMinutes(10), gap[0].Start);
        Assert.Equal(Noon.AddMinutes(39), gap[^1].Start);
        Assert.Equal(10, series.TakeWhile(point => point.CoveredSeconds > 0).Count());
        Assert.All(series.TakeWhile(point => point.CoveredSeconds > 0), point => Assert.Equal(60, point.CoveredSeconds));
        Assert.True(series.SkipWhile(point => point.CoveredSeconds > 0).SkipWhile(point => point.CoveredSeconds == 0).All(point => point.CoveredSeconds > 0));
        // Idle time is covered, not missing: the second run saw five quiet minutes.
        Assert.Equal(300, series.Where(point => point.Start >= Noon.AddMinutes(40)).Sum(point => point.CoveredSeconds));
    }

    [Fact]
    public async Task AnHourOfDowntimeIsAnHourOfBucketsWithNoDataInTheDayView()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        ManualTimeProvider clock = new(Noon);
        MetricProcess first = new(database, clock);
        await first.StartAsync();
        first.Request(first.Host(HostA), 300);
        await first.RunForAsync(TimeSpan.FromMinutes(30));
        await first.StopAsync();

        clock.Now = Noon.AddMinutes(90);
        MetricProcess second = new(database, clock);
        await second.StartAsync();
        await second.RunForAsync(TimeSpan.FromMinutes(6));
        await second.StopAsync();

        var overview = await second.OverviewAsync(MetricsRange.Day);

        // Six-minute steps: five that the first run saw, ten for the hour nobody did, one for the second run.
        Assert.Equal(5 + 10 + 1, overview.Series.Count);
        Assert.Equal([true, true, true, true, true], overview.Series.Take(5).Select(point => point.CoveredSeconds == 360));
        var gap = overview.Series.Skip(5).Take(10).ToArray();
        Assert.All(gap, point => Assert.Equal(0, point.CoveredSeconds));
        Assert.All(gap, point => Assert.Equal(0, point.RequestsPerSecond));
        Assert.Equal(Noon.AddMinutes(30), gap[0].Start);
        Assert.Equal(360, overview.Series[^1].CoveredSeconds);
        Assert.Equal(Noon.AddMinutes(90), overview.Series[^1].Start);
    }

    [Fact]
    public async Task AnIdleHourThatTheServerWasUpForIsCoveredZerosAndNotAGap()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        ManualTimeProvider clock = new(Noon);
        MetricProcess process = new(database, clock);
        await process.StartAsync();
        _ = process.Host(HostA);

        await process.RunForAsync(TimeSpan.FromMinutes(61));

        var series = (await process.OverviewAsync(MetricsRange.Hour)).Series;
        Assert.InRange(series.Count, 360, 361);
        Assert.All(series, point => Assert.True(point.CoveredSeconds > 0));
        Assert.All(series, point => Assert.Equal(0, point.RequestsPerSecond));
        Assert.All(series, point => Assert.Equal(0, point.Total.P95Ms));
    }

    [Fact]
    public async Task ACrashLosesTheOpenMinuteAndTheTimeAfterItIsAGapNotZeros()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        ManualTimeProvider clock = new(Noon);
        MetricProcess first = new(database, clock);
        await first.StartAsync();
        var host = first.Host(HostA);
        first.Request(host, 50);
        await first.RunUntilAsync(Noon.AddSeconds(130));
        first.Request(host, 70);
        await first.RunUntilAsync(Noon.AddSeconds(160));
        // The job writes every fifteen seconds: what was finished by now is safe. The minute in progress is not.
        await first.Job.WriteAsync(Ct);
        // Then the process dies: nothing is stopped, so nothing is flushed, and the abandoned services only ever fill their own queue.

        clock.Now = Noon.AddMinutes(6);
        MetricProcess second = new(database, clock);
        await second.StartAsync();
        await second.RunForAsync(TimeSpan.FromMinutes(2));

        var series = (await second.OverviewAsync(MetricsRange.Hour)).Series;
        var uncovered = series.Where(point => point.CoveredSeconds == 0).ToArray();
        Assert.Equal([Noon.AddMinutes(2), Noon.AddMinutes(3), Noon.AddMinutes(4), Noon.AddMinutes(5)], uncovered.Select(point => point.Start));
        Assert.All(uncovered, point => Assert.Equal(60, point.WidthSeconds));
        Assert.Equal(2, series.TakeWhile(point => point.CoveredSeconds > 0).Count());
        Assert.Equal(50L, await database.ScalarAsync("SELECT SUM(\"Requests\") FROM \"MetricRollups\" WHERE \"Subject\" = 0"));
    }

    [Fact]
    public async Task AfterARestartTheHourAndItsTablesStillHoldTheTrafficFromBeforeTheBoot()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        ManualTimeProvider clock = new(Noon);
        MetricProcess first = new(database, clock);
        await first.StartAsync();
        var host = first.Host(HostA);
        first.Request(host, 400);
        await first.RunForAsync(TimeSpan.FromMinutes(20));
        first.Request(host, 100, status: 502);
        await first.RunForAsync(TimeSpan.FromMinutes(1));
        await first.StopAsync();

        clock.Now = Noon.AddSeconds((21 * 60) + 30);
        MetricProcess second = new(database, clock);
        await second.StartAsync();
        await second.RunForAsync(TimeSpan.FromSeconds(10));

        var hour = await second.OverviewAsync(MetricsRange.Hour);

        var row = Assert.Single(hour.Hosts);
        Assert.Equal(HostId.FromValue(HostA), row.Id);
        Assert.Equal(500, row.Requests);
        Assert.Equal(0.2, row.ErrorRate, 6);
        // The chart starts where the data does (the first run's first minute) and has both runs' minutes in it, 21 covered before the boot.
        Assert.Equal(Noon, hour.Series[0].Start);
        Assert.Equal(21, hour.Series.Count(point => point.CoveredSeconds == 60));
        Assert.Equal(500, hour.Series.Sum(point => point.RequestsPerSecond * point.CoveredSeconds), 6);
        // The seed is a snapshot: the boot is a few seconds in, and the pre-boot part of the hour is whole minutes up to the ring.
        Assert.Equal(60 * 21, hour.Series.Where(point => point.Start < Noon.AddMinutes(21)).Sum(point => point.CoveredSeconds));
    }

    [Fact]
    public async Task TheBootMinutesFirstHalfIsInTheChartEvenWhenTheNewRingStartsOnTheMinuteItself()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        ManualTimeProvider clock = new(Noon);
        MetricProcess first = new(database, clock);
        await first.StartAsync();
        first.Request(first.Host(HostA), 50);
        await first.RunForAsync(TimeSpan.FromSeconds(3));
        await first.StopAsync();

        clock.Now = Noon.AddSeconds(5);
        MetricProcess second = new(database, clock);
        await second.StartAsync();
        second.Request(second.Host(HostA), 20);
        await second.RunForAsync(TimeSpan.FromSeconds(10));

        var overview = await second.OverviewAsync(MetricsRange.Hour);
        var series = overview.Series;

        // The first run's 50 requests are in the first point (the same ten seconds as the new run's start), and nothing is counted twice.
        Assert.Equal(70, series.Sum(point => point.RequestsPerSecond * point.CoveredSeconds), 6);
        Assert.Equal(Noon, series[0].Start);
        Assert.All(series, point => Assert.InRange(point.CoveredSeconds, 1, point.WidthSeconds));
        Assert.Equal(70, Assert.Single(overview.Hosts).Requests);
    }

    [Fact]
    public async Task APreBootMinuteNeverClaimsMoreSecondsThanItsCutWidth()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        ManualTimeProvider clock = new(Noon);
        MetricProcess first = new(database, clock);
        await first.StartAsync();
        first.Request(first.Host(HostA), 50);
        await first.RunUntilAsync(Noon.AddSeconds(48.6));
        await first.StopAsync();

        // The first run saw 49 s of the minute; the second's ring starts at 12:00:40, which cuts the minute's point to 40 s.
        clock.Now = Noon.AddSeconds(49);
        MetricProcess second = new(database, clock);
        await second.StartAsync();
        await second.RunForAsync(TimeSpan.FromSeconds(30));

        var series = (await second.OverviewAsync(MetricsRange.Hour)).Series;

        var pre = series[0];
        Assert.Equal(Noon, pre.Start);
        Assert.Equal(40, pre.WidthSeconds);
        Assert.Equal(40, pre.CoveredSeconds);
        Assert.All(series, point => Assert.InRange(point.CoveredSeconds, 1, point.WidthSeconds));
    }
}
