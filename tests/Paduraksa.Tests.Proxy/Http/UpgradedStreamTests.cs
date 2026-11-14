using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

using Paduraksa.Proxy.Http;
using Paduraksa.Tests.Proxy.Testing;

namespace Paduraksa.Tests.Proxy.Http;

/// <summary>
/// The stop's close of an upgraded connection, on a stream the test writes as YARP would: the close frame goes out only between two of
/// the upstream's frames, whatever the writes' sizes, and nothing follows it.
/// </summary>
public sealed class UpgradedStreamTests
{
    private static readonly byte[] GoingAway = [0x88, 0x02, 0x03, 0xE9];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A server frame: FIN, the opcode, the length in its shortest form, optionally a mask, and a payload.</summary>
    private static byte[] Frame(int opcode, int length, bool masked = false)
    {
        List<byte> frame = [(byte)(0x80 | opcode)];
        var mask = masked ? 0x80 : 0;
        if (length < 126)
        {
            frame.Add((byte)(mask | length));
        }
        else if (length <= ushort.MaxValue)
        {
            frame.AddRange([(byte)(mask | 126), (byte)(length >> 8), (byte)length]);
        }
        else
        {
            frame.Add((byte)(mask | 127));
            for (var shift = 56; shift >= 0; shift -= 8)
            {
                frame.Add((byte)((long)length >> shift));
            }
        }

        if (masked)
        {
            frame.AddRange([1, 2, 3, 4]);
        }

        frame.AddRange(Enumerable.Repeat((byte)'x', length));
        return [.. frame];
    }

    private static (UpgradedStream Stream, MemoryStream Client, AbortRecorder Abort) Create(bool webSocket = true)
    {
        DefaultHttpContext context = new();
        AbortRecorder abort = new();
        context.Features.Set<IHttpRequestLifetimeFeature>(abort);
        MemoryStream client = new();
        return (new UpgradedStream(new UpgradedConnections(), context, client, webSocket), client, abort);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(1000)]
    [InlineData(100_000)]
    public async Task EveryWriteSizeEndsOnABoundaryExactlyWhereAFrameEnds(int chunk)
    {
        byte[][] frames = [Frame(0x1, 5), Frame(0x2, 300), Frame(0x9, 0), Frame(0x2, 70_000), Frame(0x1, 125, masked: true), Frame(0x2, 126), Frame(0x8, 2)];
        HashSet<int> ends = [];
        var total = 0;
        foreach (var frame in frames)
        {
            total += frame.Length;
            ends.Add(total);
        }

        var all = frames.SelectMany(static frame => frame).ToArray();
        var (stream, client, _) = Create();
        for (var offset = 0; offset < all.Length; offset += chunk)
        {
            var length = Math.Min(chunk, all.Length - offset);
            await stream.WriteAsync(all.AsMemory(offset, length), Ct);
            Assert.Equal(ends.Contains(offset + length), stream.AtFrameBoundary);
        }

        Assert.Equal(all, client.ToArray());
    }

    [Fact]
    public async Task TheCloseFrameWaitsForTheFrameInProgressAndNothingFollowsIt()
    {
        var (stream, client, abort) = Create();
        var frame = Frame(0x2, 1000);
        await stream.WriteAsync(frame.AsMemory(0, 400), Ct);

        var closing = stream.CloseAsync(Ct);
        // Long enough for several of the closer's tries; the frame is unfinished, so none may write.
        await Task.Delay(150, Ct);
        Assert.Equal(400, client.Length);

        await stream.WriteAsync(frame.AsMemory(400), Ct);
        await WaitUntilAsync(() => client.Length == frame.Length + GoingAway.Length);
        Assert.Equal([.. frame, .. GoingAway], client.ToArray());

        // What the upstream sends after the close frame (its own close, say) is dropped.
        await stream.WriteAsync(Frame(0x8, 2), Ct);
        await stream.FlushAsync(Ct);
        Assert.Equal(frame.Length + GoingAway.Length, client.Length);

        // The client answered and the forward ended: no abort.
        stream.Ended();
        await closing.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.False(abort.Aborted);
    }

    [Fact]
    public async Task AWebSocketThatDoesNotFinishTheHandshakeIsAborted()
    {
        var (stream, client, abort) = Create();
        await stream.CloseAsync(Ct).WaitAsync(UpgradedConnections.HandshakeWait + TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(GoingAway, client.ToArray());
        Assert.True(abort.Aborted);
    }

    [Fact]
    public async Task AnUpstreamStuckMidFrameIsAbortedWithoutAClose()
    {
        var (stream, client, abort) = Create();
        await stream.WriteAsync(Frame(0x2, 1000).AsMemory(0, 10), Ct);

        await stream.CloseAsync(Ct).WaitAsync(UpgradedConnections.FrameBoundaryWait + TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(10, client.Length);
        Assert.True(abort.Aborted);
    }

    [Fact]
    public async Task AnUpgradeThatIsNotAWebSocketIsAbortedAtOnceAndPassesItsBytesUntouched()
    {
        var (stream, client, abort) = Create(webSocket: false);
        byte[] raw = [0x88, 0x02, 0x03];
        await stream.WriteAsync(raw, Ct);
        Assert.Equal(raw, client.ToArray());

        await stream.CloseAsync(Ct).WaitAsync(TimeSpan.FromSeconds(1), Ct);
        Assert.True(abort.Aborted);
        Assert.Equal(raw, client.ToArray());
    }

    [Fact]
    public async Task ARequestThatEndedIsNeverAbortedItsContextMayBeAnothers()
    {
        var (stream, _, abort) = Create(webSocket: false);
        stream.Ended();

        await stream.CloseAsync(Ct).WaitAsync(TimeSpan.FromSeconds(1), Ct);
        Assert.False(abort.Aborted);
    }

    [Fact]
    public void AMessageWrittenThroughAllocatesNothing()
    {
        var (stream, client, _) = Create();
        var frame = Frame(0x1, 100);
        var result = AllocAssert.Measure(
            (stream, client, frame),
            static state =>
            {
                state.client.Position = 0;
                var write = state.stream.WriteAsync(state.frame);
                if (!write.IsCompletedSuccessfully)
                {
                    throw new InvalidOperationException("a memory stream completes at once");
                }

                state.stream.FlushAsync().GetAwaiter().GetResult();
            });

        Assert.Equal(0, result.TotalBytes);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            // Poll step of a bounded wait for the closer's next try.
            await Task.Delay(10, Ct);
        }
    }

    private sealed class AbortRecorder : IHttpRequestLifetimeFeature
    {
        public bool Aborted { get; private set; }

        public CancellationToken RequestAborted { get; set; }

        public void Abort() => Aborted = true;
    }
}
