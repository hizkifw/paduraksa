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
}
