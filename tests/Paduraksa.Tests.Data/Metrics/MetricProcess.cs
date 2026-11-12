using Microsoft.Extensions.Logging.Abstractions;

using Paduraksa.Application.Metrics;
using Paduraksa.Contracts.Metrics;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Metrics;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Tests.Data.Config;

namespace Paduraksa.Tests.Data.Metrics;

/// <summary>
/// The metrics side of one run of the proxy: its registry, sampler, the two hosted services and the read side, over a database that
/// outlives it and a clock the test moves. A second <see cref="MetricProcess"/> on the same database and clock is a restart.
/// </summary>
internal sealed class MetricProcess : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly ManualTimeProvider _clock;
    private int _baselineTimers;

    public MetricProcess(TestConfigDatabase database, ManualTimeProvider clock)
    {
        _clock = clock;
        Registry = new MetricsRegistry(processors: 2, timeProvider: clock);
        Backlog = new MetricRollupBacklog();
        Sampler = new MetricSampler(Registry, clock, Backlog);
        Store = new MetricRollupStore(database.Factory);
        // The write interval is long: the job writes when it starts, when it stops and when a test asks.
        Job = new MetricRollupJob(Sampler, Store, new MetricRollupOptions { WriteInterval = TimeSpan.FromHours(10) }, clock, NullLogger<MetricRollupJob>.Instance);
        Service = new MetricSamplerService(Sampler, clock, NullLogger<MetricSamplerService>.Instance);
        Metrics = new MetricsService(Registry, new SnapshotStore(), Sampler, Store, ProcessCounters.None, clock);
        // A test that ends with this run still up (or "crashed") must not leave it writing while the database's directory goes.
        database.Own(this);
    }

    public MetricsRegistry Registry { get; }

    public MetricRollupBacklog Backlog { get; }

    public MetricSampler Sampler { get; }

    public MetricRollupStore Store { get; }

    public MetricRollupJob Job { get; }

    public MetricSamplerService Service { get; }

    public MetricsService Metrics { get; }

    /// <summary>The hosted services start in registration order: the job (which seeds the sampler) and then the sampler.</summary>
    public async Task StartAsync()
    {
        // A process that crashed left its timers armed on the shared clock: they are part of what "settled" means from here on.
        var others = _clock.ArmedTimers;
        await Job.StartAsync(Ct);
        await Service.StartAsync(Ct);
        // Both loops run on their own: they are up when the job's write timer and the sampler's delay are armed.
        _baselineTimers = others + 2;
        await SettledAsync();
    }

    /// <summary>A graceful stop: they stop in reverse, so the sampler files its last minute before the job writes.</summary>
    public async Task StopAsync()
    {
        await Service.StopAsync(Ct);
        await Job.StopAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await Service.StopAsync(CancellationToken.None);
        await Job.StopAsync(CancellationToken.None);
    }

    public Task<MetricsOverview> OverviewAsync(MetricsRange range) => Metrics.ReadOverviewAsync(range, Ct);

    public MetricSlot Host(Guid id) => Registry.SlotFor(MetricSubject.Host, id);

    public void Request(MetricSlot host, int count = 1, int status = 200, long totalMicros = 1_000)
    {
        for (var i = 0; i < count; i++)
        {
            Registry.RecordRequest(new RequestMetrics { Host = host, Status = status, Outcome = GateOutcome.Forwarded, TotalMicros = totalMicros, UpstreamMicros = -1 });
        }
    }

    /// <summary>Moves the clock to <paramref name="target"/> a second boundary at a time, letting the sampler's service tick on each.</summary>
    public async Task RunUntilAsync(DateTimeOffset target)
    {
        while (_clock.Now < target)
        {
            var step = MetricSamplerService.UntilNextSecond(_clock.Now);
            if (_clock.Now + step > target)
            {
                step = target - _clock.Now;
            }

            _clock.Advance(step);
            await SettledAsync();
        }
    }

    /// <summary>The clock jumps by <paramref name="span"/> in one step, so the sampler's next tick is that late: a stall with the process still up.</summary>
    public async Task StallAsync(TimeSpan span)
    {
        _clock.Advance(span);
        await SettledAsync();
    }

    public Task RunForAsync(TimeSpan span) => RunUntilAsync(_clock.Now + span);

    private async Task SettledAsync()
    {
        // The tick ran when the service's delay is armed again.
        for (var i = 0; i < 5_000 && _clock.ArmedTimers != _baselineTimers; i++)
        {
            if (i < 100)
            {
                await Task.Yield();
            }
            else
            {
                await Task.Delay(1, Ct);
            }
        }

        Assert.Equal(_baselineTimers, _clock.ArmedTimers);
    }
}
