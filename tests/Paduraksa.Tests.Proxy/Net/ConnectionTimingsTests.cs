using Microsoft.AspNetCore.Http;

using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Logging;
using Paduraksa.Proxy.Net;
using Paduraksa.Tests.Proxy.Testing;

namespace Paduraksa.Tests.Proxy.Net;

public sealed class ConnectionTimingsTests
{
    [Fact]
    public async Task TheHandshakeIsTakenOnceAndOnlyTheFirstRequestReportsIt()
    {
        var timings = ConnectionTimings.StartingHandshake();
        await Task.Delay(20, TestContext.Current.CancellationToken);
        timings.HandshakeCompleted();
        var handshake = timings.HandshakeTicks;
        await Task.Delay(20, TestContext.Current.CancellationToken);
        timings.HandshakeCompleted();

        Assert.True(handshake >= TimeSpan.FromMilliseconds(15).Ticks, $"{handshake} ticks");
        Assert.Equal(handshake, timings.HandshakeTicks);

        var (first, second) = (StateOn(timings), StateOn(timings));
        Assert.Equal(1, first.ConnectionRequest);
        Assert.Equal(handshake, first.TlsHandshakeTicks);
        Assert.Equal(2, second.ConnectionRequest);
        Assert.Equal(AccessLogEntry.HandshakeReused, second.TlsHandshakeTicks);
    }

    [Fact]
    public void APlainConnectionCountsItsRequestsAndHasNoHandshake()
    {
        var timings = ConnectionTimings.Plain();
        timings.HandshakeCompleted();

        var state = StateOn(timings);

        Assert.Equal(ConnectionTimings.Unknown, timings.HandshakeTicks);
        Assert.Equal(1, state.ConnectionRequest);
        Assert.Equal(AccessLogEntry.Unknown, state.TlsHandshakeTicks);
    }

    [Fact]
    public void WithoutTheFeatureNothingIsKnown()
    {
        GateRequestState state = new();
        state.CountOnConnection(new DefaultHttpContext());

        Assert.Equal(0, state.ConnectionRequest);
        Assert.Equal(AccessLogEntry.Unknown, state.TlsHandshakeTicks);
    }

    [Fact]
    public void CountingARequestAllocatesNothing()
    {
        var timings = ConnectionTimings.StartingHandshake();
        timings.HandshakeCompleted();
        DefaultHttpContext context = new();
        context.Features.Set(timings);
        GateRequestState state = new();

        AllocAssert.Zero((state, context), static s => s.state.CountOnConnection(s.context), "GateRequestState.CountOnConnection");
        Assert.True(timings.Requests > 1);
    }

    private static GateRequestState StateOn(ConnectionTimings timings)
    {
        DefaultHttpContext context = new();
        context.Features.Set(timings);
        GateRequestState state = new();
        state.CountOnConnection(context);
        return state;
    }
}
