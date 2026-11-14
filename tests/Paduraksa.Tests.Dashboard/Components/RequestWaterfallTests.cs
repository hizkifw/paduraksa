using Paduraksa.Contracts.Logging;
using Paduraksa.Dashboard;
using Paduraksa.Dashboard.LogFiles;

namespace Paduraksa.Tests.Dashboard.Components;

public sealed class RequestWaterfallTests
{
    private static LogTailEntry Entry(double totalMs, LogTailTiming? timing) => new(1, DateTimeOffset.UnixEpoch, totalMs, Timing: timing);

    [Fact]
    public void EachStageIsPlacedAtItsOwnStartAndTheRowsGoInTheOrderTheyStarted()
    {
        // A new upstream connection from 2.5 ms, the body streamed up from 12 ms to 22 ms, the wait from there, and a gap before the send.
        var stages = RequestWaterfall.Stages(Entry(100, new LogTailTiming(
            GateMs: 2,
            UpstreamConnectStartMs: 2.5, UpstreamConnectMs: 8,
            RequestBodyStartMs: 12, RequestBodyMs: 10,
            UpstreamWaitStartMs: 22, UpstreamWaitMs: 50,
            ResponseSendStartMs: 80, ResponseSendMs: 20)));

        // The proxy's own work before the connect and between it and the body, and before the response, fills what the stages leave.
        Assert.Equal(["gate", "forward", "connect", "forward", "body", "wait", "prepare", "send"], stages.Select(static stage => stage.Key));
        Assert.Equal(
            [(0d, 2d), (2, 0.5), (2.5, 8), (10.5, 1.5), (12, 10), (22, 50), (72, 8), (80, 20)],
            stages.Select(static stage => (Math.Round(stage.StartMs, 6), Math.Round(stage.Ms, 6))));
        Assert.Equal("Waiting for upstream (TTFB)", stages.Single(static stage => stage.Key == "wait").Label);
    }

    [Fact]
    public void ABodyReadBeforeTheConnectComesFirstAndAnEarlyAnswerOverlapsIt()
    {
        // The body starts arriving before the connect does, and the upstream answers at 15 ms while it still arrives: no wait.
        var stages = RequestWaterfall.Stages(Entry(60, new LogTailTiming(
            GateMs: 1,
            UpstreamConnectStartMs: 3, UpstreamConnectMs: 4,
            RequestBodyStartMs: 1.5, RequestBodyMs: 50,
            UpstreamWaitStartMs: 15, UpstreamWaitMs: 0,
            ResponseSendStartMs: 15.2, ResponseSendMs: 44)));

        // The body's bar spans the answer and the start of the send, so only the half millisecond before it is the proxy's, and the last 0.8 ms after the send no stage covers.
        Assert.Equal(["gate", "forward", "body", "connect", "wait", "send", "gap"], stages.Select(static stage => stage.Key));
        var wait = stages.Single(static stage => stage.Key == "wait");
        Assert.Equal((15, 0), (wait.StartMs, wait.Ms));
    }

    [Fact]
    public void AStageThatDidntHappenHasNoRow()
    {
        var answered = RequestWaterfall.Stages(Entry(1, new LogTailTiming(GateMs: 0.5, RequestBodyMs: 0, ResponseSendStartMs: 0.8, ResponseSendMs: 0.2)));
        // A pooled connection: a connect of 0 and no start.
        var pooled = RequestWaterfall.Stages(Entry(5, new LogTailTiming(GateMs: 0.1, UpstreamConnectMs: 0, UpstreamWaitStartMs: 0.2, UpstreamWaitMs: 4, ResponseSendStartMs: 4.5, ResponseSendMs: 0.5)));

        Assert.Equal(["gate", "gap", "send"], answered.Select(static stage => stage.Key));
        Assert.Equal(["gate", "forward", "wait", "prepare", "send"], pooled.Select(static stage => stage.Key));
        Assert.Empty(RequestWaterfall.Stages(Entry(5, timing: null)));
    }

    [Fact]
    public void TheAxisIsTheTotalUnlessAStageReachesPastIt()
    {
        var entry = Entry(10, new LogTailTiming(GateMs: 1, ResponseSendStartMs: 9.5, ResponseSendMs: 0.6));

        Assert.Equal(10.1, RequestWaterfall.Scale(entry, RequestWaterfall.Stages(entry)), 9);
        Assert.Equal(10, RequestWaterfall.Scale(Entry(10, timing: null), []));
    }

    public static TheoryData<string, double, LogTailTiming, string> Shapes => new()
    {
        { "pooled, no body", 4, new(GateMs: 0.05, UpstreamWaitStartMs: 0.2, UpstreamWaitMs: 3, ResponseSendStartMs: 3.5, ResponseSendMs: 0.5), "gate forward wait prepare send" },
        {
            "new connection, no body", 9,
            new(GateMs: 0.05, UpstreamConnectStartMs: 0.3, UpstreamConnectMs: 5, UpstreamWaitStartMs: 5.4, UpstreamWaitMs: 3, ResponseSendStartMs: 8.6, ResponseSendMs: 0.4),
            "gate forward connect forward wait prepare send"
        },
        {
            "new connection, body", 8,
            new(GateMs: 0.05, UpstreamConnectStartMs: 0.3, UpstreamConnectMs: 1.7, RequestBodyStartMs: 0.1, RequestBodyMs: 4, UpstreamWaitStartMs: 4.1, UpstreamWaitMs: 2.9, ResponseSendStartMs: 7.2, ResponseSendMs: 0.8),
            "gate forward body connect wait prepare send"
        },
        {
            "pooled, body", 8,
            new(GateMs: 0.05, RequestBodyStartMs: 0.2, RequestBodyMs: 3, UpstreamWaitStartMs: 3.3, UpstreamWaitMs: 3.7, ResponseSendStartMs: 7.2, ResponseSendMs: 0.8),
            "gate forward body forward wait prepare send"
        },
        {
            "early answer", 20,
            new(GateMs: 0.05, RequestBodyStartMs: 0.2, RequestBodyMs: 15, UpstreamWaitStartMs: 3, UpstreamWaitMs: 0, ResponseSendStartMs: 3.1, ResponseSendMs: 16.9),
            "gate forward body wait send"
        },
        {
            "refused forward", 2,
            new(GateMs: 0.05, UpstreamConnectStartMs: 0.3, UpstreamConnectMs: 1, UpstreamWaitStartMs: 1.3, UpstreamWaitMs: 0, ResponseSendStartMs: 1.5, ResponseSendMs: 0.5),
            "gate forward connect wait prepare send"
        },
        { "answered by the gate", 1, new(GateMs: 0.5, ResponseSendStartMs: 0.6, ResponseSendMs: 0.4), "gate gap send" },
        { "no timing beyond the gate", 1, new(GateMs: 0.5), "gate gap" },
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void EveryRequestShapeIsAccountedForWithDerivedRowsOnlyWhereTheresTime(string shape, double total, LogTailTiming timing, string rows)
    {
        var stages = RequestWaterfall.Stages(Entry(total, timing));

        Assert.Equal(rows.Split(' '), stages.Select(static stage => stage.Key));
        AssertAccountsForTheTime(total, stages, shape);
    }

    [Fact]
    public void TheOwnersRequestAddsUpToItsTotalAsItIsShown()
    {
        // Pooled, no body: gate 0.03 ms, TTFB 1 ms, send 0.41 ms in a 1.6 ms request used to show 1.44 ms of it.
        var entry = Entry(1.6, new LogTailTiming(GateMs: 0.03, UpstreamWaitStartMs: 0.1, UpstreamWaitMs: 1, ResponseSendStartMs: 1.19, ResponseSendMs: 0.41));
        var fmt = new DisplayFormat();

        var stages = RequestWaterfall.Stages(entry);

        Assert.Equal(["0.03 ms", "0.07 ms", "1 ms", "0.09 ms", "0.41 ms"], stages.Select(stage => fmt.Duration(stage.ShownMs)));
        Assert.Equal(1.6, stages.Sum(static stage => stage.ShownMs), 9);
        Assert.Equal(["Forwarding the request", "Preparing the response"], stages.Where(static stage => stage.Derived).Select(static stage => stage.Label));
    }

    [Fact]
    public void EveryRowExplainsItsStageAndADerivedOneSaysItIsDerived()
    {
        var stages = RequestWaterfall.Stages(Entry(20, new LogTailTiming(
            GateMs: 0.1, UpstreamConnectStartMs: 0.2, UpstreamConnectMs: 4, RequestBodyStartMs: 0.15, RequestBodyMs: 5,
            UpstreamWaitStartMs: 5.2, UpstreamWaitMs: 8, ResponseSendStartMs: 14, ResponseSendMs: 4)));

        Assert.Equal(["gate", "forward", "body", "connect", "forward", "wait", "prepare", "send", "gap"], stages.Select(static stage => stage.Key));
        Assert.All(stages, static stage => Assert.False(string.IsNullOrWhiteSpace(stage.Note), stage.Key));
        Assert.Equal(8, stages.Select(static stage => stage.Note).Distinct().Count());
        Assert.StartsWith("Derived.", stages.First(static stage => stage.Key == "forward").Note, StringComparison.Ordinal);
        Assert.Contains("proxy's own work between stages", stages.Single(static stage => stage.Key == "prepare").Note, StringComparison.Ordinal);
        Assert.Contains("not covered by any recorded stage", stages.Single(static stage => stage.Key == "gap").Note, StringComparison.Ordinal);
        Assert.DoesNotContain("Derived", stages.Single(static stage => stage.Key == "wait").Note, StringComparison.Ordinal);
    }

    [Fact]
    public void ASliverUnderAMicrosecondIsRoundingAndNotARow()
    {
        // The gate ends 0.0004 ms before the wait starts, and the send ends 0.0003 ms before the total.
        var stages = RequestWaterfall.Stages(Entry(2.0003, new LogTailTiming(GateMs: 0.1, UpstreamWaitStartMs: 0.1004, UpstreamWaitMs: 1, ResponseSendStartMs: 1.1004, ResponseSendMs: 0.9)));

        Assert.Equal(["gate", "wait", "send"], stages.Select(static stage => stage.Key));
    }

    [Fact]
    public void RowsFromRandomRequestsAreChronologicalNeverNegativeAndAddUpToTheTotal()
    {
        var random = new Random(20261114);
        for (var round = 0; round < 2_000; round++)
        {
            // One request as a chain of pieces in microseconds, some of them gaps the proxy spent; the total is where the chain ends.
            int Piece() => random.Next(4) == 0 ? 0 : random.Next(1, random.Next(2) == 0 ? 400 : 40_000);
            var (gate, forwardGap, wait, prepareGap, send) = (Piece(), Piece(), Piece(), Piece(), Piece());
            var (connectGap, connect) = random.Next(2) == 0 ? (Piece(), Piece()) : (0, 0);
            var waitStart = gate + forwardGap + connectGap + connect;
            var sendStart = waitStart + wait + prepareGap;
            var total = (sendStart + send) / 1000d;
            var timing = new LogTailTiming(
                GateMs: gate / 1000d,
                UpstreamConnectStartMs: connect > 0 ? (gate + forwardGap) / 1000d : null,
                UpstreamConnectMs: connect > 0 ? connect / 1000d : null,
                UpstreamWaitStartMs: waitStart / 1000d, UpstreamWaitMs: wait / 1000d,
                ResponseSendStartMs: sendStart / 1000d, ResponseSendMs: send / 1000d);

            var stages = RequestWaterfall.Stages(Entry(total, timing));

            AssertAccountsForTheTime(total, stages, $"round {round}");
            Assert.InRange(stages.Sum(static stage => stage.Ms), total - 0.003, total + 0.003);
            // Shown rounded, the parts miss the total by no more than half the coarsest row's step: a row can't be rounded finer than DisplayFormat shows it, and one with time is never hidden.
            var shown = stages.Sum(static stage => stage.ShownMs);
            Assert.True(Math.Abs(shown - total) <= stages.Where(static stage => stage.Ms > 0).Max(static stage => DisplayFormat.DurationStep(stage.Ms)) / 2 + 1e-9, $"round {round}: shown {shown}, total {total}; " + string.Join(", ", stages.Select(stage => $"{stage.Key} {stage.Ms}->{stage.ShownMs}")));
        }
    }

    private static void AssertAccountsForTheTime(double total, IReadOnlyList<WaterfallStage> stages, string shape)
    {
        Assert.True(stages.All(static stage => stage.Ms >= 0 && stage.StartMs >= 0), shape + ": a row has a negative start or length");
        Assert.True(stages.Zip(stages.Skip(1), static (a, b) => a.StartMs <= b.StartMs).All(static ordered => ordered), shape + ": rows are not in order");
        Assert.True(stages.Where(static stage => stage.Derived).All(static stage => stage.Ms >= 0.00095), shape + ": a derived row for no time");

        // Every instant of the request is under some row: the rows' union is the whole of it.
        var reach = 0d;
        foreach (var stage in stages)
        {
            Assert.True(stage.StartMs - reach < 0.00095, $"{shape}: a blank stretch before {stage.Key} at {stage.StartMs}");
            reach = Math.Max(reach, stage.StartMs + stage.Ms);
        }

        Assert.True(total - reach < 0.00095, $"{shape}: a blank stretch at the end, rows reach {reach} of {total}");
    }

    [Theory]
    [InlineData(0, 25, 100, "left: 0%; width: 25%")]
    [InlineData(50, 25, 100, "left: 50%; width: 25%")]
    [InlineData(90, 50, 100, "left: 90%; width: 10%")]
    [InlineData(1, 2, 3, "left: 33.33%; width: 66.67%")]
    [InlineData(0, 1, 0, "left: 0%; width: 100%")]
    public void ABarIsPlacedInPercentOfTheAxisAndNeverRunsOffTheTrack(double start, double ms, double total, string expected) =>
        Assert.Equal(expected, RequestWaterfall.Geometry(new WaterfallStage("x", "X", start, ms), total));
}
