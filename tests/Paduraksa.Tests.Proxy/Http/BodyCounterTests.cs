using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks.Sources;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Http;
using Paduraksa.Tests.Proxy.Testing;

namespace Paduraksa.Tests.Proxy.Http;

public sealed class BodyCounterTests
{
    private static (DefaultHttpContext Context, MemoryStream Sent) NewContext(byte[]? requestBody = null, string method = "GET")
    {
        DefaultHttpContext context = new();
        context.Request.Method = method;
        MemoryStream sent = new();
        context.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(sent));
        context.Request.Body = new MemoryStream(requestBody ?? []);
        return (context, sent);
    }

    [Fact]
    public async Task EveryWayOfWritingTheResponseIsCountedAndPassedOn()
    {
        var ct = TestContext.Current.CancellationToken;
        var (context, sent) = NewContext();
        BodyCounter counter = new();
        counter.Begin(context);

        context.Response.Body.Write(new byte[3], 0, 3);
        context.Response.Body.Write(new byte[5].AsSpan());
        context.Response.Body.WriteByte(1);
        await context.Response.Body.WriteAsync(new byte[7].AsMemory(), ct);
#pragma warning disable CA1835 // The array overload is one of the ways in, and it must be counted too.
        await context.Response.Body.WriteAsync(new byte[11], 0, 11, ct);
#pragma warning restore CA1835
        var span = context.Response.BodyWriter.GetSpan(13);
        span[..13].Clear();
        context.Response.BodyWriter.Advance(13);
        await context.Response.BodyWriter.WriteAsync(new byte[17], ct);
        await context.Response.BodyWriter.FlushAsync(ct);

        const long Total = 3 + 5 + 1 + 7 + 11 + 13 + 17;
        Assert.Equal(Total, counter.ResponseBytes);
        Assert.Equal(Total, sent.Length);
        Assert.Equal(0, counter.RequestBytes);
    }

    [Fact]
    public async Task EveryWayOfReadingTheRequestIsCounted()
    {
        var ct = TestContext.Current.CancellationToken;
        var (context, _) = NewContext(new byte[100]);
        BodyCounter counter = new();
        counter.Begin(context);
        var body = context.Request.Body;

        Assert.Equal(10, body.Read(new byte[10], 0, 10));
        Assert.Equal(10, body.Read(new byte[10].AsSpan()));
        Assert.NotEqual(-1, body.ReadByte());
        Assert.Equal(10, await body.ReadAsync(new byte[10].AsMemory(), ct));
#pragma warning disable CA1835 // As above, for reads.
        Assert.Equal(10, await body.ReadAsync(new byte[10], 0, 10, ct));
#pragma warning restore CA1835
        Assert.Equal(59, await body.ReadAsync(new byte[200].AsMemory(), ct));
        Assert.Equal(0, await body.ReadAsync(new byte[10].AsMemory(), ct));
        Assert.Equal(-1, body.ReadByte());

        Assert.Equal(100, counter.RequestBytes);
        Assert.Equal(0, counter.ResponseBytes);
    }

    /// <summary>
    /// Kestrel refuses synchronous body I/O, and <see cref="Stream"/>'s own array-based async methods fall back to the synchronous
    /// ones on a pool thread: the counter's array overloads must go to the inner stream's async path, never through that fallback.
    /// </summary>
    [Fact]
    public async Task TheArrayOverloadsStayAsynchronousOverABodyThatRefusesSynchronousIo()
    {
        var ct = TestContext.Current.CancellationToken;
        DefaultHttpContext context = new();
        AsyncOnlyStream response = new();
        context.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(response));
        context.Request.Body = new AsyncOnlyStream(new byte[10]);
        BodyCounter counter = new();
        counter.Begin(context);

#pragma warning disable CA1835 // The array overloads are the point.
        Assert.Equal(10, await context.Request.Body.ReadAsync(new byte[16], 0, 16, ct));
        await context.Response.Body.WriteAsync(new byte[7], 0, 7, ct);
#pragma warning restore CA1835

        Assert.Equal(10, counter.RequestBytes);
        Assert.Equal(7, counter.ResponseBytes);
        Assert.Equal(7, response.Written);
    }

    [Fact]
    public async Task ASentFileIsCounted()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, new byte[1_000], TestContext.Current.CancellationToken);
            var (context, sent) = NewContext();
            BodyCounter counter = new();
            counter.Begin(context);

            await context.Response.SendFileAsync(path, 100, 400, TestContext.Current.CancellationToken);

            Assert.Equal(400, counter.ResponseBytes);
            Assert.Equal(400, sent.Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AServerWithoutAResponseBodyFeatureIsNotCountedAndItsBytesStayUnknown()
    {
        var (context, _) = NewContext();
        context.Features.Set<IHttpResponseBodyFeature>(null);
        var state = GateMiddleware.BeginRequest(context);

        state.CountBody(context);

        Assert.Null(state.CountedBody);
        Assert.False(new BodyCounter().Begin(context));
    }

    [Fact]
    public void AHeadAnswersBodyIsNotCounted()
    {
        var (context, _) = NewContext(method: "HEAD");
        BodyCounter counter = new();
        counter.Begin(context);

        context.Response.Body.Write(new byte[10]);

        Assert.Equal(0, counter.ResponseBytes);
    }

    [Fact]
    public void ASecondBeginForTheSameRequestKeepsItsCountsAndDoesNotWrapItself()
    {
        var (context, sent) = NewContext();
        BodyCounter counter = new();
        counter.Begin(context);
        context.Response.Body.Write(new byte[10]);

        counter.Begin(context);
        context.Response.Body.Write(new byte[5]);

        Assert.Equal(15, counter.ResponseBytes);
        Assert.Equal(15, sent.Length);
    }

    [Fact]
    public void TheNextRequestStartsFromZeroOnItsOwnFeatures()
    {
        BodyCounter counter = new();
        var (first, firstSent) = NewContext(new byte[8]);
        counter.Begin(first);
        first.Response.Body.Write(new byte[10]);
        _ = first.Request.Body.Read(new byte[8]);

        var (second, secondSent) = NewContext(new byte[3]);
        counter.Begin(second);
        second.Response.Body.Write(new byte[4]);
        _ = second.Request.Body.Read(new byte[8]);

        Assert.Equal(4, counter.ResponseBytes);
        Assert.Equal(3, counter.RequestBytes);
        Assert.Equal(10, firstSent.Length);
        Assert.Equal(4, secondSent.Length);
    }

    [Fact]
    public async Task AnUpgradedConnectionsBytesAreCountedBothWays()
    {
        var ct = TestContext.Current.CancellationToken;
        var (context, _) = NewContext();
        MemoryStream tunnel = new();
        tunnel.Write(new byte[50]);
        tunnel.Position = 0;
        FakeUpgrade upgrade = new(tunnel);
        context.Features.Set<IHttpUpgradeFeature>(upgrade);
        BodyCounter counter = new();
        counter.Begin(context);

        var feature = context.Features.Get<IHttpUpgradeFeature>()!;
        Assert.Same(counter, feature);
        Assert.True(feature.IsUpgradableRequest);
        var stream = await feature.UpgradeAsync();
        Assert.Equal(50, await stream.ReadAsync(new byte[64].AsMemory(), ct));
        await stream.WriteAsync(new byte[20].AsMemory(), ct);
        await stream.DisposeAsync();

        Assert.Equal(50, counter.RequestBytes);
        Assert.Equal(20, counter.ResponseBytes);
        Assert.True(upgrade.Upgraded);
        Assert.Throws<ObjectDisposedException>(() => tunnel.ReadByte());
    }

    [Fact]
    public async Task AnExtendedConnectsBytesAreCountedBothWays()
    {
        var ct = TestContext.Current.CancellationToken;
        var (context, _) = NewContext();
        MemoryStream tunnel = new();
        tunnel.Write(new byte[30]);
        tunnel.Position = 0;
        FakeConnect connect = new(tunnel);
        context.Features.Set<IHttpExtendedConnectFeature>(connect);
        BodyCounter counter = new();
        counter.Begin(context);

        var feature = context.Features.Get<IHttpExtendedConnectFeature>()!;
        Assert.Same(counter, feature);
        Assert.Equal("websocket", feature.Protocol);
        var stream = await feature.AcceptAsync();
        Assert.Equal(30, await stream.ReadAsync(new byte[64].AsMemory(), ct));
        stream.Write(new byte[9]);

        Assert.Equal(30, counter.RequestBytes);
        Assert.Equal(9, counter.ResponseBytes);
    }

    [Fact]
    public void ARequestThatCannotBeUpgradedKeepsTheServersFeatures()
    {
        var (context, _) = NewContext();
        FakeUpgrade upgrade = new(Stream.Null) { Upgradable = false };
        context.Features.Set<IHttpUpgradeFeature>(upgrade);
        FakeConnect connect = new(Stream.Null) { Extended = false };
        context.Features.Set<IHttpExtendedConnectFeature>(connect);

        new BodyCounter().Begin(context);

        Assert.Same(upgrade, context.Features.Get<IHttpUpgradeFeature>());
        Assert.Same(connect, context.Features.Get<IHttpExtendedConnectFeature>());
    }

    /// <summary>
    /// A read the server completes later (the usual case for a request body on the wire) goes through the counter's pooled
    /// builder: once warm, a pending read, completed inline, allocates nothing, and neither does a write.
    /// </summary>
    [Fact]
    public void APendingReadAndAWriteAllocateNothing()
    {
        // A debug build compiles every async method's state machine as a class, which allocates on each call whatever builder pools the box.
        Assert.SkipWhen(
            typeof(BodyCounter).Assembly.GetCustomAttribute<DebuggableAttribute>()?.IsJITOptimizerDisabled == true,
            "Only an optimised build of the proxy can show the pooled read at 0 B.");
        var (context, _) = NewContext();
        PendingStream inner = new();
        context.Request.Body = inner;
        context.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(inner));
        BodyCounter counter = new();
        counter.Begin(context);
        (Stream Body, Stream Response, PendingStream Inner, byte[] Buffer) state = (context.Request.Body, context.Response.Body, inner, new byte[16]);

        var result = AllocAssert.MeasureSettled(state, static s =>
        {
            var read = s.Body.ReadAsync(s.Buffer.AsMemory(), CancellationToken.None);
            s.Inner.Complete(4);
            if (!read.IsCompleted)
            {
                throw new InvalidOperationException("the read should have completed inline");
            }

            _ = read.GetAwaiter().GetResult();
            Completed(s.Response.WriteAsync(s.Buffer.AsMemory(), CancellationToken.None));
        });

        Assert.Equal(0, result.TotalBytes);
        Assert.True(counter.RequestBytes > 0 && counter.RequestBytes % 4 == 0);
        Assert.Equal(counter.RequestBytes * 4, counter.ResponseBytes);
    }

    private static void Completed(ValueTask write)
    {
        if (!write.IsCompletedSuccessfully)
        {
            throw new InvalidOperationException("the write should have completed at once");
        }
    }

    /// <summary>Like Kestrel's body streams: the async methods work, the synchronous ones throw.</summary>
    private sealed class AsyncOnlyStream(byte[]? data = null) : Stream
    {
        private int _position;

        public long Written { get; private set; }

        public override bool CanRead => true;

        public override bool CanWrite => true;

        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var source = data ?? [];
            var n = Math.Min(buffer.Length, source.Length - _position);
            source.AsSpan(_position, n).CopyTo(buffer.Span);
            _position += n;
            return new ValueTask<int>(n);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Written += buffer.Length;
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous operations are disallowed.");

        public override void Write(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous operations are disallowed.");

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class FakeUpgrade(Stream tunnel) : IHttpUpgradeFeature
    {
        public bool Upgradable { get; init; } = true;

        public bool Upgraded { get; private set; }

        public bool IsUpgradableRequest => Upgradable;

        public Task<Stream> UpgradeAsync()
        {
            Upgraded = true;
            return Task.FromResult(tunnel);
        }
    }

    private sealed class FakeConnect(Stream tunnel) : IHttpExtendedConnectFeature
    {
        public bool Extended { get; init; } = true;

        public bool IsExtendedConnect => Extended;

        public string? Protocol => "websocket";

        public ValueTask<Stream> AcceptAsync() => ValueTask.FromResult(tunnel);
    }

    /// <summary>A stream whose reads stay pending until <see cref="Complete"/>, which runs the continuation inline; writes go nowhere.</summary>
    private sealed class PendingStream : Stream, IValueTaskSource<int>
    {
        private ManualResetValueTaskSourceCore<int> _source;

        public override bool CanRead => true;

        public override bool CanWrite => true;

        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void Complete(int bytes) => _source.SetResult(bytes);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _source.Reset();
            return new ValueTask<int>(this, _source.Version);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public int GetResult(short token) => _source.GetResult(token);

        public ValueTaskSourceStatus GetStatus(short token) => _source.GetStatus(token);

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
            _source.OnCompleted(continuation, state, token, flags);

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
        }
    }
}
