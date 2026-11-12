using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Paduraksa.Application.Notifications;
using Paduraksa.Core.Model;
using Paduraksa.Data;
using static Paduraksa.Tests.Data.Config.TestConfigDatabase;

namespace Paduraksa.Tests.Data.Notifications;

/// <summary>The flusher: events are buffered in memory and written in batches, together with their deliveries, and a publisher never waits for the database.</summary>
public sealed class NotificationFlusherTests : OutboxTestBase
{
    [Fact]
    public async Task PublishNeverWaitsForTheDatabaseAndTheBufferDropsItsOldestAndCountsThem()
    {
        await AddChannel();
        using SlowFactory slow = new(Db.Factory, held: true);
        var dispatcher = Dispatcher(Options(eventCapacity: 10, flushBatch: 5), slow);
        await dispatcher.StartAsync(Ct);

        // The database holds every call: a publisher that waited for it would never come back from this loop.
        var published = Task.Run(() =>
        {
            for (var i = 0; i < 50; i++)
            {
                dispatcher.Publish(Event(title: $"event {i}"));
            }
        }, Ct);
        await published.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        var dropped = dispatcher.Dropped;
        Assert.InRange(dropped, 50 - 10 - 5, 50 - 10 + 1);
        slow.Release();
        await SettledAsync(() => dispatcher.Routed == 50 - dropped);

        // What was not dropped was stored, once, in publish order: the newest ones survive.
        var titles = (await Events()).Select(e => e.Title).ToList();
        Assert.Equal(50 - (int)dropped, titles.Count);
        Assert.Equal("event 49", titles[^1]);
        Assert.Equal(titles.OrderBy(t => int.Parse(t.AsSpan(6))).ToList(), titles);
    }

    [Fact]
    public async Task AFullBufferIsWrittenAtOnceAndTheRestWaitsForTheIntervalOrTheStop()
    {
        await AddChannel();
        var dispatcher = Dispatcher(Options(flush: TimeSpan.FromSeconds(30), flushBatch: 100));
        await dispatcher.StartAsync(Ct);

        for (var i = 0; i < 250; i++)
        {
            dispatcher.Publish(Event(title: $"event {i}"));
        }

        // Two full batches go at once; the 50 left wait for the (long) interval.
        await SettledAsync(() => dispatcher.Routed >= 200);
        await Task.Delay(100, Ct);
        Assert.Equal(200, (await Events()).Count);

        // The stop writes them: a graceful stop loses nothing that was published.
        await dispatcher.StopAsync(Ct);
        Assert.Equal(250, (await Events()).Count);
        Assert.Equal(250, (await Deliveries()).Count);
    }

    [Fact]
    public async Task AWriteThatFailsKeepsItsBatchAndTriesAgainWithoutLosingOrRepeatingAnEvent()
    {
        await AddChannel();
        using SlowFactory failing = new(Db.Factory, held: false, failures: 3);
        var dispatcher = Dispatcher(Options(), failing);
        await dispatcher.StartAsync(Ct);

        for (var i = 0; i < 7; i++)
        {
            dispatcher.Publish(Event(title: $"event {i}"));
        }

        await SettledAsync(() => dispatcher.Routed >= 7);

        Assert.Equal(Enumerable.Range(0, 7).Select(i => $"event {i}"), (await Events()).Select(e => e.Title));
        Assert.Equal(0, dispatcher.Dropped);
    }

    [Fact]
    public async Task EveryEventInAFlushIsRoutedWithOneReadOfTheChannelsNotOnePerEvent()
    {
        await AddChannel(NotificationChannelKind.Webhook, true, NotificationEventKind.UpstreamDown);
        await AddChannel(NotificationChannelKind.Ntfy, true, NotificationEventKind.UpstreamDown);
        QueryCounter counter = new();
        var factory = new InterceptedFactory(Db, counter);
        var dispatcher = Dispatcher(Options(flush: TimeSpan.FromMilliseconds(200)), factory);
        await dispatcher.StartAsync(Ct);
        await Task.Delay(100, Ct);
        var before = counter.EnabledChannelReads;

        for (var i = 0; i < 40; i++)
        {
            dispatcher.Publish(Event(title: $"event {i}"));
        }

        await SettledAsync(() => dispatcher.Routed >= 40);

        Assert.Equal(1, counter.EnabledChannelReads - before);
        Assert.Equal(80, (await Deliveries()).Count);
    }

    /// <summary>Holds every call until released (a locked database), then fails the first few of them (<c>failures</c>), as a busy one does.</summary>
    private sealed class SlowFactory : IDbContextFactory<ConfigDbContext>, IDisposable
    {
        private readonly IDbContextFactory<ConfigDbContext> _inner;
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _failures;

        public SlowFactory(IDbContextFactory<ConfigDbContext> inner, bool held, int failures = 0)
        {
            _inner = inner;
            _failures = failures;
            if (!held)
            {
                _open.SetResult();
            }
        }

        public void Release() => _open.TrySetResult();

        public void Dispose() => _open.TrySetResult();

        public ConfigDbContext CreateDbContext() => _inner.CreateDbContext();

        public async Task<ConfigDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            await _open.Task.WaitAsync(cancellationToken);
            if (Interlocked.Decrement(ref _failures) >= 0)
            {
                throw new Microsoft.Data.Sqlite.SqliteException("database is locked", 5);
            }

            return await _inner.CreateDbContextAsync(cancellationToken);
        }
    }

    private sealed class InterceptedFactory(Config.TestConfigDatabase db, DbCommandInterceptor interceptor) : IDbContextFactory<ConfigDbContext>
    {
        public ConfigDbContext CreateDbContext() => db.NewContext(interceptor);
    }

    private sealed class QueryCounter : DbCommandInterceptor
    {
        private int _reads;

        public int EnabledChannelReads => Volatile.Read(ref _reads);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"NotificationChannels\"", StringComparison.Ordinal) && command.CommandText.Contains("\"Enabled\"", StringComparison.Ordinal))
            {
                _ = Interlocked.Increment(ref _reads);
            }

            return ValueTask.FromResult(result);
        }
    }
}
