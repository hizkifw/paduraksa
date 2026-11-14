using Paduraksa.Contracts.Logging;
using Paduraksa.Dashboard.LogFiles;

namespace Paduraksa.Tests.Dashboard.Components;

public sealed class RequestWaterfallTests
{
    private static LogTailEntry Entry(double totalMs, LogTailTiming? timing) => new(1, DateTimeOffset.UnixEpoch, totalMs, Timing: timing);

    [Fact]
    public void AForwardIsLaidOutInThePipelinesOrder()
    {
        var stages = RequestWaterfall.Stages(Entry(100, new LogTailTiming(GateMs: 2, RequestBodyMs: 10, UpstreamConnectMs: 8, UpstreamWaitMs: 60, ResponseSendMs: 30)));

        Assert.Equal(["gate", "body", "connect", "wait", "send"], stages.Select(static stage => stage.Key));
        Assert.Equal((0, 2), (stages[0].StartMs, stages[0].Ms));
        // The body is read once the upstream connection is up.
        Assert.Equal((10, 10), (stages[1].StartMs, stages[1].Ms));
        Assert.Equal((2, 8), (stages[2].StartMs, stages[2].Ms));
        Assert.Equal((10, 60), (stages[3].StartMs, stages[3].Ms));
        // The send ends with the request.
        Assert.Equal((70, 30), (stages[4].StartMs, stages[4].Ms));
    }

    [Fact]
    public void AnAnswerHasNoForwardAndAPooledConnectIsReused()
    {
        var answered = RequestWaterfall.Stages(Entry(1, new LogTailTiming(GateMs: 0.5, RequestBodyMs: 0, ResponseSendMs: 0.2)));
        var pooled = RequestWaterfall.Stages(Entry(5, new LogTailTiming(GateMs: 0.1, UpstreamConnectMs: 0, UpstreamWaitMs: 4, ResponseSendMs: 0.5)));

        Assert.Equal(["gate", "send"], answered.Select(static stage => stage.Key));
        Assert.True(pooled.Single(static stage => stage.Key == "connect").Reused);
        Assert.Empty(RequestWaterfall.Stages(Entry(5, timing: null)));
    }

    [Theory]
    [InlineData(0, 25, 100, "margin-left: 0%; width: 25%")]
    [InlineData(50, 25, 100, "margin-left: 50%; width: 25%")]
    [InlineData(90, 50, 100, "margin-left: 90%; width: 10%")]
    [InlineData(1, 2, 3, "margin-left: 33.33%; width: 66.67%")]
    [InlineData(0, 1, 0, "margin-left: 0%; width: 100%")]
    public void ABarIsPlacedInPercentOfTheTotalAndNeverRunsOffTheTrack(double start, double ms, double total, string expected) =>
        Assert.Equal(expected, RequestWaterfall.Geometry(new WaterfallStage("x", "X", start, ms), total));
}
