using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Paduraksa.Application.Metrics;
using Paduraksa.Application.Security;
using Paduraksa.Core.Ids;
using Paduraksa.Core.Model;
using Paduraksa.Data;
using Paduraksa.Proxy.AccessLists;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.ForwardAuth;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Metrics;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.RateLimiting;
using Paduraksa.Proxy.RuleSets;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Storage.Sqlite;
using Paduraksa.Tests.Shared;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>
/// The security read side as the server builds it: the gate's runtimes, a metric registry the snapshot gets its slots from, the sampler over
/// both, and (once <see cref="UseDatabaseAsync"/> has run) a real rollup database. Until then the database is down, as it is when it can't be read.
/// </summary>
internal sealed class SecurityRig : IAsyncDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("paduraksa-security-").FullName;
    private readonly SwitchableFactory _factory = new();
    private ServiceProvider? _database;
    private long _generation;

    public SecurityRig(TimeProvider time)
    {
        Time = time;
        Registry = new MetricsRegistry(processors: 2, timeProvider: time);
        Sampler = new MetricSampler(Registry, time, new MetricRollupBacklog(), new SecuritySources(ProofOfWork.Stats, RateLimits.Stats, AccessLists.Stats, Connections.Stats, ForwardAuth, RuleSets));
        MetricStore = new MetricRollupStore(_factory);
        SecurityStore = new SecurityRollupStore(_factory);
        Metrics = new MetricsService(Registry, Store, Sampler, MetricStore, ProcessCounters.None, time);
        Service = new SecurityOverviewService(Store, Connections, Sampler, Metrics, SecurityStore, time);
    }

    public TimeProvider Time { get; }

    public SnapshotStore Store { get; } = new();

    public ProofOfWorkRuntime ProofOfWork { get; } = new();

    public RateLimitRuntime RateLimits { get; } = new(new RateLimitTable(64));

    public AccessListRuntime AccessLists { get; } = new(TimeProvider.System, new TestPasswords().Verifier);

    public RuleSetRuntime RuleSets { get; } = new();

    public ForwardAuthRuntime ForwardAuth { get; } = new();

    public ConnectionRuntime Connections { get; } = new(new ConnectionTable(64));

    public MetricsRegistry Registry { get; }

    public MetricSampler Sampler { get; }

    public MetricRollupStore MetricStore { get; }

    public SecurityRollupStore SecurityStore { get; }

    public MetricsService Metrics { get; }

    public SecurityOverviewService Service { get; }

    public void Register(IServiceCollection services)
    {
        services.AddSingleton(Service);
        services.AddSingleton(Metrics);
    }

    public void Publish(ProxyConfigModel model)
    {
        var result = SnapshotCompiler.Compile(
            model,
            new SnapshotCompilerOptions { RateLimits = RateLimits, ProofOfWork = ProofOfWork, RuleSets = RuleSets, ForwardAuth = ForwardAuth, Metrics = Registry });
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Errors));
        var snapshot = result.Snapshot!.Bind(++_generation, [.. result.Snapshot!.RouteIdBySlot.Select(_ => new Endpoint(null, null, "route"))]);
        Registry.Publish(snapshot.MetricRegistrations);
        Store.Publish(snapshot);
    }

    /// <summary>Records requests to <paramref name="host"/> that the gate answered with <paramref name="outcome"/>.</summary>
    public void Refuse(HostId host, GateOutcome outcome, int count = 1)
    {
        var slot = Store.Current.HostsByIndex.Single(h => h.Id == host).MetricSlot;
        for (var i = 0; i < count; i++)
        {
            Registry.RecordRequest(new RequestMetrics { Host = slot, Status = 403, Outcome = outcome, TotalMicros = 100, UpstreamMicros = -1 });
        }
    }

    public async Task UseDatabaseAsync()
    {
        _database = new ServiceCollection().AddPaduraksaDbContextFactory<ConfigDbContext>(Path.Combine(_directory, ConfigDatabase.FileName)).BuildServiceProvider();
        var factory = _database.GetRequiredService<IDbContextFactory<ConfigDbContext>>();
        await ConfigDatabase.InitializeAsync(factory, new Int64OrderedIdGenerator(TimeProvider.System), Xunit.TestContext.Current.CancellationToken);
        _factory.Inner = factory;
    }

    /// <summary>
    /// Samples what has been counted and every second of the minute after it, as the sampler's service does, so the minute is closed; then writes
    /// the closed minutes as the rollup job does, and moves on past the service's 30 s rollup cache: from then on the longer ranges see the
    /// counts, and a page that polls reads them at its next tick.
    /// </summary>
    public async Task SettleAsync(Action<TimeSpan> advance)
    {
        ArgumentNullException.ThrowIfNull(advance);
        Sampler.Tick();
        for (var second = 0; second < 60; second++)
        {
            advance(TimeSpan.FromSeconds(1));
            Sampler.Tick();
        }

        var ct = Xunit.TestContext.Current.CancellationToken;
        await MetricStore.WriteAsync(Sampler.Rollups.Drain(), ct);
        await SecurityStore.WriteAsync(Sampler.SecurityRollups.Drain(), ct);
        advance(TimeSpan.FromSeconds(31));
    }

    public async ValueTask DisposeAsync()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory a still-open SQLite file keeps; the OS cleans it.
        }
    }

    private sealed class SwitchableFactory : IDbContextFactory<ConfigDbContext>
    {
        public IDbContextFactory<ConfigDbContext>? Inner { get; set; }

        public ConfigDbContext CreateDbContext() => Inner?.CreateDbContext() ?? throw new InvalidOperationException("The database is down.");
    }
}

/// <summary>A clock that moves only when told, and fires the timers it passes: a <see cref="PeriodicTimer"/> ticks exactly when a test advances it.</summary>
internal sealed class SteppedTime(DateTimeOffset start) : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<SteppedTimer> _timers = [];
    private DateTimeOffset _now = start;
    private long _timestamp = start.UtcTicks;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    // Monotonic time moves with the wall clock, so the sampler's coverage is the time the test moved.
    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _timestamp;
        }
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_gate)
        {
            SteppedTimer timer = new(callback, state, _now + dueTime, period);
            _timers.Add(timer);
            return timer;
        }
    }

    public void Advance(TimeSpan by)
    {
        List<SteppedTimer> due = [];
        lock (_gate)
        {
            _now += by;
            _timestamp += by.Ticks;
            foreach (var timer in _timers)
            {
                while (timer.Due is { } at && at <= _now)
                {
                    due.Add(timer);
                    timer.Next();
                }
            }
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    private sealed class SteppedTimer(TimerCallback callback, object? state, DateTimeOffset due, TimeSpan period) : ITimer
    {
        private DateTimeOffset? _due = due;
        private bool _disposed;

        public DateTimeOffset? Due => _disposed ? null : _due;

        public void Next() => _due = period > TimeSpan.Zero && _due is { } at ? at + period : null;

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan newPeriod) => throw new NotSupportedException();

        public void Dispose() => _disposed = true;

        public ValueTask DisposeAsync()
        {
            _disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
