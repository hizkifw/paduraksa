using Bunit;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using Paduraksa.Application.Metrics;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Ids;
using Paduraksa.Core.Model;
using Paduraksa.Data;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Metrics;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Storage.Sqlite;

namespace Paduraksa.Tests.Dashboard.Components;

internal sealed class MetricsClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    // The test only moves time forward, so the wall clock is also the monotonic one.
    public override long GetTimestamp() => Now.UtcTicks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
}

/// <summary>The metrics read side, whole: a registry the test records into, a sampler it ticks, a snapshot, and (when asked) a real rollup database.</summary>
internal sealed class MetricsRig : IAsyncDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    public static readonly HostId App = HostId.New();
    public static readonly HostId Blog = HostId.New();
    public static readonly UpstreamId Web = UpstreamId.New();
    public static readonly UpstreamId Media = UpstreamId.New();

    private readonly MetricsClock _clock = new(Start);
    private readonly string _directory = Directory.CreateTempSubdirectory("paduraksa-metrics-").FullName;
    private ServiceProvider? _database;
    private readonly MetricsDatabaseFactory _factory = new();
    private RuntimeSnapshot? _snapshot;

    public MetricsRig()
    {
        Registry = new MetricsRegistry(processors: 2, timeProvider: _clock);
        Backlog = new MetricRollupBacklog();
        Sampler = new MetricSampler(Registry, _clock, Backlog);
        Store = new SnapshotStore();
    }

    public MetricsRegistry Registry { get; }

    public MetricRollupBacklog Backlog { get; }

    public MetricSampler Sampler { get; }

    public SnapshotStore Store { get; }

    public FakeDashboardConfig Config { get; private set; } = null!;

    /// <summary>Signs an operator in and registers the service; the page's clock is this rig's.</summary>
    public DashboardTestContext SignIn()
    {
        var context = new DashboardTestContext();
        // Before the sign-in: its setup builds the service provider, after which nothing can be registered.
        context.Services.AddSingleton(new MetricsService(Registry, Store, Sampler, new MetricRollupStore(_factory), ProcessCounters.None, _clock));
        Config = ConfigPages.SignIn(context, Role.Operator, _clock);
        return context;
    }

    public void Publish()
    {
        var result = SnapshotCompiler.Compile(
            new ProxyConfigModel
            {
                Upstreams =
                [
                    new UpstreamSpec { Id = Web, Destinations = [new DestinationSpec(DestinationId.New(), "http://127.0.0.1:1")] },
                    new UpstreamSpec { Id = Media, Destinations = [new DestinationSpec(DestinationId.New(), "http://127.0.0.1:2")] },
                ],
                Hosts =
                [
                    new HostSpec { Id = App, Names = ["app.test"], Https = false, Routes = [new RouteSpec { Id = RouteId.New(), Upstream = Web }] },
                    new HostSpec { Id = Blog, Names = ["blog.test"], Https = false, Routes = [new RouteSpec { Id = RouteId.New(), Upstream = Media }] },
                ],
            },
            new SnapshotCompilerOptions { Metrics = Registry });
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Errors));
        var snapshot = result.Snapshot!;
        _snapshot = snapshot.Bind(1, [.. snapshot.RouteIdBySlot.Select(_ => new Endpoint(null, null, "route"))]);
        Registry.Publish(_snapshot.MetricRegistrations);
        Store.Publish(_snapshot);
    }

    public void Record(HostId host, int count, int status, long micros, UpstreamId? upstream = null, long upstreamMicros = -1, int error = 0)
    {
        var compiled = _snapshot!.HostsByIndex.Single(h => h.Id == host);
        var route = compiled.Routes[0];
        for (var i = 0; i < count; i++)
        {
            Registry.RecordRequest(new RequestMetrics
            {
                Host = compiled.MetricSlot,
                Route = route.MetricSlot,
                Upstream = upstream is null || route.Upstream is null ? MetricSlot.None : route.Upstream.Slot,
                Status = status,
                Outcome = GateOutcome.Forwarded,
                TotalMicros = micros,
                UpstreamMicros = upstreamMicros,
                ForwarderError = error,
            });
        }
    }

    public void RecordUnmatched(int count)
    {
        for (var i = 0; i < count; i++)
        {
            Registry.RecordRequest(new RequestMetrics { Host = MetricsRegistry.UnmatchedHost, Status = 404, Outcome = GateOutcome.UnknownHost, TotalMicros = 100 });
        }
    }

    public void TickAt(int seconds)
    {
        _clock.Now = Start.AddSeconds(seconds);
        Sampler.Tick();
        _last = seconds;
    }

    /// <summary>Samples on every second after the last tick, as the service does: a longer silence is a stall the sampler doesn't claim to have observed.</summary>
    public void TickEverySecondTo(int seconds)
    {
        for (var second = _last + 1; second <= seconds; second++)
        {
            TickAt(second);
        }
    }

    private int _last;

    public async Task UseDatabaseAsync()
    {
        _database = new ServiceCollection().AddPaduraksaDbContextFactory<ConfigDbContext>(Path.Combine(_directory, ConfigDatabase.FileName)).BuildServiceProvider();
        var factory = _database.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<ConfigDbContext>>();
        await ConfigDatabase.InitializeAsync(factory, new Int64OrderedIdGenerator(TimeProvider.System), Xunit.TestContext.Current.CancellationToken);
        _factory.Inner = factory;
    }

    public Task StoreMinuteAsync(MetricRollupSubject subject, string id, DateTimeOffset at, long requests, long errors = 0) =>
        new MetricRollupStore(_factory).WriteAsync(
            [new MetricRollup(subject, id, new MetricPoint { Start = at.ToUnixTimeSeconds() / 60 * 60, Width = 60, CoveredSeconds = 60, Requests = requests, Status2xx = requests - errors, Status5xx = errors, Errors = errors, TotalP50 = 1_000, TotalP95 = 2_000, TotalP99 = 3_000, UpstreamTimed = requests, UpstreamP95 = 1_000 })],
            Xunit.TestContext.Current.CancellationToken);

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
}

/// <summary>The database the test has opened, or - until it has - one that fails every time, as a database that is down does.</summary>
internal sealed class MetricsDatabaseFactory : Microsoft.EntityFrameworkCore.IDbContextFactory<ConfigDbContext>
{
    public Microsoft.EntityFrameworkCore.IDbContextFactory<ConfigDbContext>? Inner { get; set; }

    public ConfigDbContext CreateDbContext() => Inner?.CreateDbContext() ?? throw new InvalidOperationException("The database is down.");
}
