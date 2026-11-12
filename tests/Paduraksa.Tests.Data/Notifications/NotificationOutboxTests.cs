using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Paduraksa.Application.Notifications;
using Paduraksa.Core.Model;
using Paduraksa.Data;
using Paduraksa.Data.Notifications;
using static Paduraksa.Tests.Data.Config.TestConfigDatabase;

namespace Paduraksa.Tests.Data.Notifications;

/// <summary>The outbox's storage: routing, retention, and the query plans of the statements the worker, the history and retention run.</summary>
public sealed class NotificationOutboxTests : OutboxTestBase
{
    private NotificationOutbox Outbox(PlanCapture? capture = null) =>
        new(capture is null ? Db.Factory : new CapturingFactory(Db, capture), new NotificationStats());

    private static BufferedEvent Buffered(NotificationEventKind kind, DateTimeOffset at, string title = "t") =>
        new(NotificationEventId.New(), Event(kind, title), at);

    [Fact]
    public async Task EachEventGetsOneDeliveryPerEnabledSubscribedChannelAndAnEventNobodyWantsIsStillStored()
    {
        var down = await AddChannel(NotificationChannelKind.Webhook, true, NotificationEventKind.UpstreamDown);
        var both = await AddChannel(NotificationChannelKind.Ntfy, true, NotificationEventKind.UpstreamDown, NotificationEventKind.CertificateExpiring);
        _ = await AddChannel(NotificationChannelKind.Discord, false, NotificationEventKind.UpstreamDown);
        _ = await AddChannel(NotificationChannelKind.Gotify, true, NotificationEventKind.SignInLockout);
        var outbox = Outbox();

        var touched = await outbox.StoreAsync(
            [Buffered(NotificationEventKind.UpstreamDown, T0), Buffered(NotificationEventKind.CertificateExpiring, T0), Buffered(NotificationEventKind.AcmeRenewalRecovered, T0)],
            Ct);

        Assert.Equal(new[] { down, both }.Order(), touched.Order());
        var events = await Events();
        var deliveries = await Deliveries();
        Assert.Equal(3, events.Count);
        Assert.Equal(3, deliveries.Count);
        Assert.All(deliveries, d => Assert.Equal(NotificationDeliveryState.Pending, d.State));
        var none = events.Single(e => e.Kind == NotificationEventKind.AcmeRenewalRecovered);
        Assert.DoesNotContain(deliveries, d => d.EventId == none.Id);
        Assert.Equal(new[] { down, both }.Order(), deliveries.Where(d => d.EventId == events.Single(e => e.Kind == NotificationEventKind.UpstreamDown).Id).Select(d => d.ChannelId).Order());
        Assert.Equal("ups_1", events[0].Subject);
        Assert.Equal("""[["upstreamId","ups_1"]]""", events[0].Data);
    }

    [Fact]
    public async Task RetentionDeletesOldFinishedEventsWithTheirDeliveriesAndKeepsWhatIsNewOrStillPending()
    {
        var channel = await AddChannel();
        var old = TimeSpan.FromDays(20);
        var (finishedEvent, finished) = await LeftBehind(channel, old, title: "sent long ago");
        var (failedEvent, failed) = await LeftBehind(channel, old, title: "failed long ago");
        var (droppedEvent, dropped) = await LeftBehind(channel, old, title: "dropped long ago");
        var (pendingEvent, _) = await LeftBehind(channel, old, title: "still pending");
        var (recentEvent, recent) = await LeftBehind(channel, TimeSpan.FromDays(2), title: "recent");
        await Settle(finished, NotificationDeliveryState.Sent);
        await Settle(failed, NotificationDeliveryState.Failed);
        await Settle(dropped, NotificationDeliveryState.Dropped);
        await Settle(recent, NotificationDeliveryState.Sent);
        // An old event that nothing was subscribed to has no delivery at all.
        await using (var context = Db.NewContext())
        {
            context.NotificationEvents.Add(new NotificationEventRow { Id = NotificationEventId.New(), Kind = NotificationEventKind.UpstreamUp, Title = "nobody", Body = "b", OccurredAt = T0 - old, CreatedAt = T0 - old });
            await context.SaveChangesAsync(Ct);
        }

        var removed = await Outbox().PruneAsync(T0.AddDays(-14), batch: 500, Ct);

        Assert.Equal(4, removed);
        Assert.Equal(new[] { pendingEvent, recentEvent }.Order(), (await Events()).Select(e => e.Id).Order());
        Assert.DoesNotContain(await Deliveries(), d => d.EventId == finishedEvent || d.EventId == failedEvent || d.EventId == droppedEvent);
        Assert.Equal(2, (await Deliveries()).Count);
    }

    [Fact]
    public async Task RetentionWorksInShortBatchesAndTheDispatcherRunsItOnItsOwnClockAndRetention()
    {
        var channel = await AddChannel();
        List<NotificationDeliveryId> all = [];
        for (var i = 0; i < 7; i++)
        {
            all.Add((await LeftBehind(channel, TimeSpan.FromDays(10), title: $"old {i}")).Delivery);
        }

        _ = await LeftBehind(channel, TimeSpan.FromHours(1), title: "fresh");
        foreach (var id in all)
        {
            await Settle(id, NotificationDeliveryState.Sent);
        }

        var dispatcher = Dispatcher(Options(retention: TimeSpan.FromDays(7), pruneBatch: 3, maxAge: TimeSpan.FromDays(30)));
        var removed = await dispatcher.PruneAsync(Ct);

        Assert.Equal(7, removed);
        Assert.Equal(["fresh"], (await Events()).Select(e => e.Title));
    }

    [Fact]
    public async Task TheWorkersStatementsAreIndexSeeksNotScansOfTheDeliveries()
    {
        var channel = await AddChannel();
        _ = await LeftBehind(channel, TimeSpan.FromMinutes(1));
        PlanCapture capture = new();
        var outbox = Outbox(capture);

        _ = await outbox.RetryWaitingUntilAsync(channel, T0, Ct);
        var groups = await outbox.ClaimAsync(channel, T0, 100, Ct);
        var group = Assert.Single(groups);
        _ = await outbox.RecordAsync(group, NotificationDeliveryState.Sent, null, T0, null, Ct);

        var statements = capture.Statements;
        File.WriteAllText("/tmp/plans1.txt", string.Join("\n----\n", statements.Select(x => x.Sql + "\n=> " + x.Plan)));
        Assert.NotEmpty(statements);
        foreach (var (sql, plan) in statements)
        {
            Assert.DoesNotContain("SCAN NotificationDeliveries", plan, StringComparison.Ordinal);
            Assert.DoesNotContain("TEMP B-TREE", plan, StringComparison.Ordinal);
            Assert.DoesNotContain("SCAN NotificationEvents", plan, StringComparison.Ordinal);
            Assert.True(plan.Contains("INDEX", StringComparison.Ordinal) || plan.Contains("PRIMARY KEY", StringComparison.Ordinal), $"{sql}\n{plan}");
        }

        Assert.Contains(statements, s => s.Plan.Contains("IX_NotificationDeliveries_ChannelId_State_NextAttemptAt (ChannelId=? AND State=? AND NextAttemptAt>?)", StringComparison.Ordinal));
        Assert.Contains(statements, s => s.Plan.Contains("IX_NotificationDeliveries_ChannelId_State_NextAttemptAt (ChannelId=? AND State=? AND NextAttemptAt<?)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheRecoveryOfAStartAndRetentionSeekTheIndexesToo()
    {
        var channel = await AddChannel();
        var (_, delivery) = await LeftBehind(channel, TimeSpan.FromDays(20));
        await Settle(delivery, NotificationDeliveryState.Sent);
        _ = await LeftBehind(channel, TimeSpan.FromHours(2));
        PlanCapture capture = new();
        var outbox = Outbox(capture);

        _ = await outbox.RecoverAsync(T0 - TimeSpan.FromHours(1), T0, Ct);
        _ = await outbox.PruneAsync(T0.AddDays(-14), 100, Ct);

        // The one statement that has to look at every pending row (deliveries whose channel is gone) is the only scan, and runs once a start.
        foreach (var (sql, plan) in capture.Statements.Where(s => !s.Sql.Contains("NOT EXISTS", StringComparison.Ordinal) || s.Sql.Contains("\"EventId\"", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain("SCAN NotificationEvents", plan, StringComparison.Ordinal);
            if (sql.Contains("\"NotificationEvents\"", StringComparison.Ordinal) && sql.Contains("CreatedAt\" <", StringComparison.Ordinal) && sql.Contains("ORDER BY", StringComparison.Ordinal))
            {
                Assert.Contains("IX_NotificationEvents_CreatedAt", plan, StringComparison.Ordinal);
                Assert.DoesNotContain("TEMP B-TREE", plan, StringComparison.Ordinal);
            }
        }

        File.WriteAllText("/tmp/plans2.txt", string.Join("\n----\n", capture.Statements.Select(x => x.Sql + "\n=> " + x.Plan)));
        Assert.Contains(capture.Statements, s => s.Plan.Contains("IX_NotificationDeliveries_EventId_ChannelId", StringComparison.Ordinal));
        Assert.Contains(capture.Statements, s => s.Sql.StartsWith("DELETE FROM \"NotificationDeliveries\"", StringComparison.Ordinal) && s.Plan.Contains("IX_NotificationDeliveries_EventId_ChannelId", StringComparison.Ordinal));
    }

    private async Task Settle(NotificationDeliveryId id, NotificationDeliveryState state)
    {
        await using var context = Db.NewContext();
        _ = await context.NotificationDeliveries.Where(d => d.Id == id).ExecuteUpdateAsync(s => s.SetProperty(d => d.State, state), Ct);
    }

    private sealed class CapturingFactory(Config.TestConfigDatabase db, PlanCapture capture) : IDbContextFactory<ConfigDbContext>
    {
        public ConfigDbContext CreateDbContext() => db.NewContext(capture);
    }

    /// <summary>Records each statement EF runs and the plan SQLite makes for it, asked on the same connection with the same parameters.</summary>
    private sealed class PlanCapture : DbCommandInterceptor
    {
        private readonly Lock _lock = new();
        private readonly List<(string Sql, string Plan)> _statements = [];

        public List<(string Sql, string Plan)> Statements
        {
            get
            {
                lock (_lock)
                {
                    return [.. _statements];
                }
            }
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Record(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Record(command);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        private void Record(DbCommand command)
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

            lock (_lock)
            {
                _statements.Add((command.CommandText, string.Join("\n", lines)));
            }
        }
    }
}
