using Paduraksa.Contracts.Logging;
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

        Assert.Equal(["gate", "connect", "body", "wait", "send"], stages.Select(static stage => stage.Key));
        Assert.Equal(
            [(0d, 2d), (2.5, 8), (12, 10), (22, 50), (80, 20)],
            stages.Select(static stage => (stage.StartMs, stage.Ms)));
        Assert.Equal("Waiting for upstream (TTFB)", stages[3].Label);
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

        Assert.Equal(["gate", "body", "connect", "wait", "send"], stages.Select(static stage => stage.Key));
        var wait = stages.Single(static stage => stage.Key == "wait");
        Assert.Equal((15, 0), (wait.StartMs, wait.Ms));
    }

    [Fact]
    public void AStageThatDidntHappenHasNoRow()
    {
        var answered = RequestWaterfall.Stages(Entry(1, new LogTailTiming(GateMs: 0.5, RequestBodyMs: 0, ResponseSendStartMs: 0.8, ResponseSendMs: 0.2)));
        // A pooled connection: a connect of 0 and no start.
        var pooled = RequestWaterfall.Stages(Entry(5, new LogTailTiming(GateMs: 0.1, UpstreamConnectMs: 0, UpstreamWaitStartMs: 0.2, UpstreamWaitMs: 4, ResponseSendStartMs: 4.5, ResponseSendMs: 0.5)));

        Assert.Equal(["gate", "send"], answered.Select(static stage => stage.Key));
        Assert.Equal(["gate", "wait", "send"], pooled.Select(static stage => stage.Key));
        Assert.Empty(RequestWaterfall.Stages(Entry(5, timing: null)));
    }

    [Fact]
    public void TheAxisIsTheTotalUnlessAStageReachesPastIt()
    {
        var entry = Entry(10, new LogTailTiming(GateMs: 1, ResponseSendStartMs: 9.5, ResponseSendMs: 0.6));

        Assert.Equal(10.1, RequestWaterfall.Scale(entry, RequestWaterfall.Stages(entry)), 9);
        Assert.Equal(10, RequestWaterfall.Scale(Entry(10, timing: null), []));
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
