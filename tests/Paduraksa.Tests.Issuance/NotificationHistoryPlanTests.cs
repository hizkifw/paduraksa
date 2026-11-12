using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Paduraksa.Application.Notifications;
using Paduraksa.Contracts.Notifications;
using Paduraksa.Core.Model;
using Paduraksa.Data;
using Paduraksa.Data.Notifications;
using static Paduraksa.Tests.Data.Config.TestConfigDatabase;

namespace Paduraksa.Tests.Issuance;

/// <summary>The history's statements on a real database: what they return, and that SQLite answers them from the indexes (a history of 14 days is not read whole to find a channel's page).</summary>
public sealed class NotificationHistoryPlanTests
{
    private static async Task<(IssuanceHarness Harness, NotificationChannelId Channel, NotificationChannelId Other)> SeedAsync()
    {
        var h = await IssuanceHarness.CreateAsync();
        NotificationChannelRow Row(string label) => new()
        {
            Id = NotificationChannelId.New(),
            Kind = NotificationChannelKind.Webhook,
            Label = label,
            Events = ["upstream.down"],
            CreatedAt = h.Time.GetUtcNow(),
            UpdatedAt = h.Time.GetUtcNow(),
        };
        var one = Row("one");
        var two = Row("two");
        await using var context = await h.Config.Db.Factory.CreateDbContextAsync(Ct);
        context.NotificationChannels.AddRange(one, two);
        for (var i = 0; i < 6; i++)
        {
            NotificationEventRow row = new()
            {
                Id = NotificationEventId.New(),
                Kind = i % 2 == 0 ? NotificationEventKind.UpstreamDown : NotificationEventKind.CertificateExpiring,
                Title = $"event {i}",
                Body = "b",
                OccurredAt = h.Time.GetUtcNow().AddMinutes(-60 + i),
                CreatedAt = h.Time.GetUtcNow().AddMinutes(-60 + i),
            };
            context.NotificationEvents.Add(row);
            foreach (var channel in new[] { one, two })
            {
                context.NotificationDeliveries.Add(new NotificationDeliveryRow
                {
                    Id = NotificationDeliveryId.New(),
                    EventId = row.Id,
                    ChannelId = channel.Id,
                    State = i == 5 && channel == two ? NotificationDeliveryState.Pending : i % 3 == 0 ? NotificationDeliveryState.Failed : NotificationDeliveryState.Sent,
                    NextAttemptAt = row.CreatedAt,
                });
            }
        }

        await context.SaveChangesAsync(Ct);
        return (h, one.Id, two.Id);
    }

    private sealed class NoWake : IDeliveryWake
    {
        public void Wake(NotificationChannelId channel)
        {
        }
    }

    private static NotificationHistoryService Service(IssuanceHarness h, PlanCapture capture) =>
        new(new CapturingFactory(h, capture), h.Config.Service, h.Permissions, new NotificationStats(), new NoWake(), h.Time);

    [Fact]
    public async Task EveryFilterIsAnswerableFromTheIndexesAndTheRowsAreWhatTheFilterSays()
    {
        var (h, one, two) = await SeedAsync();
        await using (h)
        {
            PlanCapture capture = new();
            var service = Service(h, capture);

            var all = await service.QueryAsync(new NotificationHistoryFilter(), 100, null, Ct);
            var byChannel = await service.QueryAsync(new NotificationHistoryFilter(Channel: two), 100, null, Ct);
            var failed = await service.QueryAsync(new NotificationHistoryFilter(Outcome: NotificationDeliveryState.Failed), 100, null, Ct);
            var channelFailed = await service.QueryAsync(new NotificationHistoryFilter(Channel: one, Outcome: NotificationDeliveryState.Failed), 100, null, Ct);
            var kind = await service.QueryAsync(new NotificationHistoryFilter(Kind: NotificationEventKind.CertificateExpiring), 100, null, Ct);
            var range = await service.QueryAsync(new NotificationHistoryFilter(From: h.Time.GetUtcNow().AddMinutes(-58), To: h.Time.GetUtcNow().AddMinutes(-56)), 100, null, Ct);
            var pending = await service.QueryAsync(new NotificationHistoryFilter(Outcome: NotificationDeliveryState.Pending), 100, null, Ct);
            var page = await service.QueryAsync(new NotificationHistoryFilter(), 2, null, Ct);
            var next = await service.QueryAsync(new NotificationHistoryFilter(), 2, page.Next, Ct);

            Assert.Equal(["event 5", "event 4", "event 3", "event 2", "event 1", "event 0"], all.Items.Select(e => e.Title));
            Assert.All(all.Items, e => Assert.Equal(2, e.Deliveries.Count));
            Assert.Equal(6, byChannel.Items.Count);
            Assert.Equal(["event 3", "event 0"], failed.Items.Select(e => e.Title));
            Assert.Equal(["event 3", "event 0"], channelFailed.Items.Select(e => e.Title));
            Assert.Equal(["event 5", "event 3", "event 1"], kind.Items.Select(e => e.Title));
            Assert.Equal(["event 3", "event 2"], range.Items.Select(e => e.Title));
            Assert.Equal(["event 5"], pending.Items.Select(e => e.Title));
            Assert.NotNull(pending.Items[0].Deliveries.Single(d => d.State == NotificationDeliveryState.Pending).NextAttemptAt);
            Assert.Equal(["event 5", "event 4"], page.Items.Select(e => e.Title));
            Assert.Equal(["event 3", "event 2"], next.Items.Select(e => e.Title));
            Assert.Null((await service.QueryAsync(new NotificationHistoryFilter(), 6, null, Ct)).Next);

            foreach (var (sql, plan) in capture.Statements)
            {
                if (sql.Contains("\"n\".\"Attempts\"", StringComparison.Ordinal))
                {
                    // A page's deliveries: a seek per event on the (event, channel) index.
                    Assert.StartsWith("SEARCH n USING INDEX IX_NotificationDeliveries_EventId_ChannelId (EventId=?)", plan, StringComparison.Ordinal);
                    Assert.DoesNotContain("TEMP B-TREE", plan, StringComparison.Ordinal);
                }
                else if (sql.Contains("FROM \"NotificationEvents\"", StringComparison.Ordinal) && sql.Contains("\"CreatedAt\" >=", StringComparison.Ordinal))
                {
                    // A time range: the age index (the range's events are sorted, which is the range's size).
                    Assert.Contains("SEARCH n USING INDEX IX_NotificationEvents_CreatedAt (CreatedAt>? AND CreatedAt<?)", plan, StringComparison.Ordinal);
                }
                else if (sql.Contains("FROM \"NotificationEvents\"", StringComparison.Ordinal))
                {
                    // Newest first without a range: the key, backwards, up to the limit; a filter on channel or outcome is a probe of the deliveries index per event.
                    Assert.True((plan + "\n").StartsWith("SCAN n\n", StringComparison.Ordinal) || plan.StartsWith("SEARCH n USING INTEGER PRIMARY KEY (rowid<?)", StringComparison.Ordinal), plan);
                    Assert.DoesNotContain("TEMP B-TREE", plan, StringComparison.Ordinal);
                    Assert.DoesNotContain("SCAN n0", plan, StringComparison.Ordinal);
                }
            }

            Assert.Contains(capture.Statements, s => s.Plan.Contains("IX_NotificationDeliveries_EventId_ChannelId (EventId=? AND ChannelId=?)", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task AChannelsRecentDeliveriesAreReadFromItsOwnIndexNewestFirst()
    {
        var (h, one, _) = await SeedAsync();
        await using (h)
        {
            PlanCapture capture = new();
            var service = Service(h, capture);

            var recent = await service.RecentAsync(one, 3, Ct);

            Assert.Equal(["event 5", "event 4", "event 3"], recent.Select(e => e.Title));
            Assert.All(recent, e => Assert.Equal(one, Assert.Single(e.Deliveries).ChannelId));
            Assert.Contains(capture.Statements, s => s.Plan.Contains("IX_NotificationDeliveries_ChannelId_Id (ChannelId=?)", StringComparison.Ordinal));
            Assert.All(capture.Statements.Where(s => s.Sql.Contains("\"n\".\"Attempts\"", StringComparison.Ordinal)), s => Assert.DoesNotContain("TEMP B-TREE", s.Plan, StringComparison.Ordinal));
            Assert.All(capture.Statements.Where(s => s.Sql.Contains("FROM \"NotificationEvents\"", StringComparison.Ordinal)), s => Assert.StartsWith("SEARCH n USING INTEGER PRIMARY KEY", s.Plan, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task TheChannelListFiguresCountQueuedAndTheLastSevenDaysFromTheIndexes()
    {
        var (h, one, two) = await SeedAsync();
        await using (h)
        {
            PlanCapture capture = new();
            await using var context = new CapturingFactory(h, capture).CreateDbContext();

            var figures = await NotificationFigures.ReadAsync(context, h.Time.GetUtcNow() - NotificationHistoryService.FigureWindow, Ct);

            // Events 0 and 3 failed on both; event 5 is pending on the second channel; the other four were sent.
            Assert.Equal(new NotificationChannelFigures(0, 4, 2), figures[one]);
            Assert.Equal(new NotificationChannelFigures(1, 3, 2), figures[two]);
            // Both are covering scans of the claim index (channel, state, time): no row of the table is read, and the events are not joined.
            Assert.All(capture.Statements, s => Assert.Contains("USING COVERING INDEX IX_NotificationDeliveries_ChannelId_State_NextAttemptAt", s.Plan, StringComparison.Ordinal));
            Assert.All(capture.Statements, s => Assert.DoesNotContain("NotificationEvents", s.Sql, StringComparison.Ordinal));
        }
    }

    private sealed class CapturingFactory(IssuanceHarness harness, PlanCapture capture) : IDbContextFactory<ConfigDbContext>
    {
        public ConfigDbContext CreateDbContext() => harness.Config.Db.NewContext(capture);
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

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
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

            return ValueTask.FromResult(result);
        }
    }
}
