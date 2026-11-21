using Microsoft.Extensions.Logging.Abstractions;

using Paduraksa.Application.Honeypot;
using Paduraksa.Contracts.Metrics;
using Paduraksa.Proxy.Honeypot;
using Paduraksa.Tests.Data.Config;
using static Paduraksa.Tests.Data.Honeypot.HoneypotRig;

namespace Paduraksa.Tests.Data.Honeypot;

/// <summary>The job that writes and prunes on a timer, and that no read or delete walks a whole table.</summary>
public sealed class HoneypotJobAndPlanTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheJobWritesAtOnceThenEveryIntervalAndOnceMoreWhenItStops()
    {
        await using var rig = await HoneypotRig.CreateAsync();
        HoneypotRecordingJob job = new(rig.Store, HoneypotRetention.Default, rig.Time, NullLogger<HoneypotRecordingJob>.Instance);
        var first = Hit(id: 1);
        rig.Recorder.Record(in first);
        await job.StartAsync(Ct);
        try
        {
            await WaitForRequestsAsync(rig, 1);

            var second = Hit(id: 2, after: TimeSpan.FromSeconds(1));
            rig.Recorder.Record(in second);
            rig.Time.Advance(HoneypotRecordingJob.WriteInterval);
            await WaitForRequestsAsync(rig, 2);

            var last = Hit(id: 3, after: TimeSpan.FromSeconds(2));
            rig.Recorder.Record(in last);
        }
        finally
        {
            await job.StopAsync(Ct);
        }

        // The request queued after the last interval is written by the stop.
        Assert.Equal(3L, await RequestsOf(rig));
        Assert.Equal(1L, await rig.Database.ScalarAsync("SELECT count(*) FROM HoneypotSessions"));
    }

    [Fact]
    public async Task AFailingWriteOrPruneIsReportedAndNeverThrows()
    {
        await using var down = HoneypotRig.Down();
        HoneypotRecordingJob job = new(down.Store, HoneypotRetention.Default, down.Time, NullLogger<HoneypotRecordingJob>.Instance);
        var hit = Hit();
        down.Recorder.Record(in hit);

        await job.WriteAsync(Ct);
        await job.PruneAsync(Ct);

        Assert.Equal((1, 1), down.Recorder.Waiting);
    }

    [Fact]
    public async Task NoReadOrDeleteWalksTheRequestsTableAndTheSummarySeeksItsRange()
    {
        await using var rig = await HoneypotRig.CreateAsync();
        await rig.RecordAsync(Enumerable.Range(0, 20).Select(i => Hit(agent: "agent-" + i, id: (ulong)i, after: TimeSpan.FromSeconds(i))).ToArray());
        PlanCapture capture = new();
        HoneypotService service = new(new CapturingFactory(rig.Database, capture), rig.Stats, rig.Time);
        HoneypotStore store = new(new CapturingFactory(rig.Database, capture), rig.Recorder);
        var firstPage = await service.ListSessionsAsync(new HoneypotSessionFilter(MetricsRange.Day), 5, null, Ct);

        _ = await service.ReadSummaryAsync(MetricsRange.Day, null, Ct);
        _ = await service.ListSessionsAsync(new HoneypotSessionFilter(MetricsRange.Day, Host), 5, firstPage.Next, Ct);
        _ = await service.ReadSessionAsync(firstPage.Items[0].Id, Ct);
        _ = await service.ListRequestsAsync(firstPage.Items[0].Id, 10, null, Ct);
        _ = await store.PruneAsync(Start, new HoneypotRetention(TimeSpan.FromDays(30), 10, 10), Ct);

        Assert.NotEmpty(capture.Statements);
        foreach (var (sql, plan) in capture.Statements)
        {
            // The caps' counts walk an index, which is as cheap as SQLite counts rows; nothing reads the table's rows.
            Assert.True(!plan.Contains("SCAN HoneypotRequests", StringComparison.Ordinal) || plan.Contains("USING COVERING INDEX", StringComparison.Ordinal), $"{sql}\n{plan}");
            // The summary seeks the range: sessions by their last request, guessed paths by request time. The list walks sessions newest first by ID and stops at its page, which is the cheaper plan for a range that reaches the newest ones.
            if (sql.Contains("GROUP BY", StringComparison.Ordinal))
            {
                Assert.Contains(sql.Contains("\"HoneypotRequests\"", StringComparison.Ordinal) ? "IX_HoneypotRequests_Time" : "IX_HoneypotSessions_LastSeen", plan, StringComparison.Ordinal);
            }
        }
    }

    private static async Task<long> RequestsOf(HoneypotRig rig) => (long)(await rig.Database.ScalarAsync("SELECT count(*) FROM HoneypotRequests"))!;

    private static async Task WaitForRequestsAsync(HoneypotRig rig, long count)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (await RequestsOf(rig) < count)
        {
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(30), $"The job never wrote {count} request(s).");
            // Poll step of a bounded wait for the job's write.
            await Task.Delay(20, Ct);
        }
    }
}
