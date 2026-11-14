using System.Diagnostics;

using Paduraksa.Proxy.Logging;

namespace Paduraksa.Tests.Proxy.Logging;

public sealed class RequestStagesTests
{
    private const int None = AccessLogEntry.UnknownOffset;

    private static long Ms(double ms) => TimeSpan.FromMilliseconds(ms).Ticks;

    [Fact]
    public void OnAPooledConnectionWithoutABodyTheWaitIsTheWholeForward() =>
        Assert.Equal(Ms(50), RequestStages.WaitTicks(forwardStartMicros: 1_000, Ms(50), None, 0, None, 0, bodyUnfinished: false));

    [Fact]
    public void ANewConnectionsWaitStartsWhenItIsUp() =>
        // The forward starts at 1 ms, connects from 1.1 ms for 8 ms, and is answered at 51 ms: 41.9 ms of it was the wait.
        Assert.Equal(Ms(41.9), RequestStages.WaitTicks(1_000, Ms(50), 1_100, Ms(8), None, 0, false));

    [Fact]
    public void WithABodyTheWaitStartsAtItsLastByte() =>
        // The body arrives from 2 ms to 32 ms; the answer at 51 ms.
        Assert.Equal(Ms(19), RequestStages.WaitTicks(1_000, Ms(50), 1_100, Ms(0.5), 2_000, Ms(30), false));

    [Fact]
    public void AnUpstreamThatAnswersBeforeTheBodyIsInWaitedForNothing() =>
        // An early 413: the answer at 11 ms while the body still arrived until 302 ms.
        Assert.Equal(0, RequestStages.WaitTicks(1_000, Ms(10), None, 0, 2_000, Ms(300), false));

    [Fact]
    public void AnUpstreamThatCutTheBodyOffWaitedForNothing()
    {
        // The answer at 11 ms after the body's first and only byte read at 2 ms: no read found the body's end, so it was never all sent.
        Assert.Equal(0, RequestStages.WaitTicks(1_000, Ms(10), None, 0, 2_000, 0, bodyUnfinished: true));
        Assert.Equal(Ms(9), RequestStages.WaitTicks(1_000, Ms(10), None, 0, 2_000, 0, bodyUnfinished: false));
    }

    [Fact]
    public void ABodyOfOneReadStillEndsWhereItWasRead() =>
        Assert.Equal(Ms(10), RequestStages.WaitTicks(1_000, Ms(50), None, 0, 41_000, 0, false));

    [Fact]
    public void NothingForwardedHasNoWait()
    {
        Assert.Equal(AccessLogEntry.Unknown, RequestStages.WaitTicks(None, AccessLogEntry.Unknown, None, AccessLogEntry.Unknown, 2_000, Ms(3), false));
        Assert.Equal(AccessLogEntry.Unknown, RequestStages.WaitTicks(None, Ms(5), None, 0, None, 0, false));
        Assert.Equal(AccessLogEntry.Unknown, RequestStages.WaitStartTicks(None, Ms(5), 0));
    }

    [Fact]
    public void TheWaitStartsWhereItsLengthEndsAtTheAnswer()
    {
        var wait = RequestStages.WaitTicks(1_000, Ms(50), 1_100, Ms(8), None, 0, false);

        Assert.Equal(Ms(9.1), RequestStages.WaitStartTicks(1_000, Ms(50), wait));
        // An early answer: the zero-length wait sits at the answer.
        Assert.Equal(Ms(11), RequestStages.WaitStartTicks(1_000, Ms(10), 0));
    }

    [Fact]
    public void AnOverlongDurationIsHeldRatherThanWrapped() =>
        Assert.InRange(RequestStages.WaitTicks(int.MaxValue, long.MaxValue, None, 0, int.MaxValue, long.MaxValue, false), 0, long.MaxValue);

    [Fact]
    public void AnOffsetIsMicrosecondsAfterTheStartAndUnknownWhenNotTaken()
    {
        var started = Stopwatch.GetTimestamp();

        Assert.Equal(1_500, RequestStages.Offset(started, started + (Stopwatch.Frequency * 3 / 2000)));
        Assert.Equal(0, RequestStages.Offset(started, started));
        Assert.Equal(None, RequestStages.Offset(started, 0));
        Assert.Equal(None, RequestStages.Offset(started, started - Stopwatch.Frequency));
        Assert.Equal(None, RequestStages.Offset(started, started + (Stopwatch.Frequency * 3_600)));
    }
}
