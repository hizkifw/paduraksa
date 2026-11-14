using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Paduraksa.Proxy.Http;

/// <summary>
/// Counts the body bytes a request really moved: what was written to the client's response body and read from its request
/// body, after any transformation (an upstream's gzip counts as the compressed bytes), on HTTP/1.1, HTTP/2 and HTTP/3
/// alike (docs/design/observability.md "Counted body bytes"). It stands in for the request's <see cref="IHttpResponseBodyFeature"/>,
/// its request body stream and, on a request that can be upgraded, its upgrade feature, and passes every call through.
/// </summary>
/// <remarks>
/// <para><b>0 B in steady state.</b> One counter and its streams live on the gate's pooled state (one per H1 connection or
/// pooled H2/H3 stream), and <see cref="Begin"/> re-points them at the request's own features: a few feature reads and sets,
/// which Kestrel keeps in fields. Writes are counted before they are handed on, so the write path adds no continuation; a
/// read is counted when it completes, and one that goes asynchronous awaits through a pooled builder.</para>
/// <para><b>What "written" means.</b> The bytes handed to the server for the client. A client that goes away mid-body has
/// been counted what the proxy wrote before it noticed, which can include what still sat in the server's and the kernel's
/// send buffers; nothing more exact is visible. Bytes Kestrel discards (the body of a <c>HEAD</c> answer) count nothing.</para>
/// <para><b>Upgraded connections.</b> A WebSocket's frames don't pass through the response body: they go through the stream
/// the upgrade (HTTP/1.1) or the extended <c>CONNECT</c> (HTTP/2) returns. On a request that can be upgraded the counter also
/// stands in for that feature, and the stream it returns counts reads as bytes in and writes as bytes out; the request is
/// counted when it ends, so a WebSocket's bytes land when it closes. Accepting the upgrade allocates a little (Kestrel's own
/// task, and the <see cref="UpgradedConnections"/> entry the stop closes it through), never per message.</para>
/// <para><b>Lifetime.</b> Like the state it hangs on, nothing may hold it past the request: the next request on the connection
/// or stream re-points it. A request that reads <see cref="HttpRequest.BodyReader"/> gets Kestrel's own pipe reader over the
/// counting stream (Kestrel builds one whenever the body stream was replaced), which allocates; nothing on the public pipeline
/// reads the body that way.</para>
/// </remarks>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "The streams wrap the request's own; disposing them is the server's business, and the counter outlives each request.")]
internal sealed class BodyCounter : IHttpResponseBodyFeature, IHttpUpgradeFeature, IHttpExtendedConnectFeature
{
    private readonly CountingStream _responseStream;
    private readonly CountingStream _requestStream;
    private readonly CountingStream _upgradedStream;
    private readonly CountingPipeWriter _responseWriter;
    private IHttpResponseBodyFeature? _response;
    private IHttpUpgradeFeature? _upgrade;
    private IHttpExtendedConnectFeature? _connect;
    private UpgradedConnections? _connections;
    private HttpContext? _upgradable;
    private long _bytesOut;
    private long _bytesIn;
    private long _firstBodyRead;
    private long _lastBodyRead;
    private bool _bodyEnded;
    private bool _discardsBody;

    public BodyCounter()
    {
        _responseStream = new CountingStream(this);
        _requestStream = new CountingStream(this, timesReads: true);
        _upgradedStream = new CountingStream(this);
        _responseWriter = new CountingPipeWriter(this);
    }

    /// <summary>Response body bytes written to the client so far.</summary>
    public long ResponseBytes => _discardsBody ? 0 : _bytesOut;

    /// <summary>Request body bytes read from the client so far.</summary>
    public long RequestBytes => _bytesIn;

    /// <summary>
    /// From the first read of the request body that returned bytes to the last one, in <see cref="TimeSpan"/> ticks; 0 when no body
    /// byte was read. An upgraded connection's frames aren't the request body and aren't timed.
    /// </summary>
    public long RequestBodyTicks => _firstBodyRead == 0 ? 0 : System.Diagnostics.Stopwatch.GetElapsedTime(_firstBodyRead, _lastBodyRead).Ticks;

    /// <summary>When the first read that returned body bytes completed, as a <see cref="System.Diagnostics.Stopwatch"/> timestamp; 0 when none did.</summary>
    public long FirstBodyReadTimestamp => _firstBodyRead;

    /// <summary>Whether a read found the end of the request body.</summary>
    public bool RequestBodyEnded => _bodyEnded;

    /// <summary>
    /// Zeroes the counts and stands in for the request's body features. Must run before anything has touched the body, so the
    /// public pipeline's outermost middleware calls it; a second call for the same request changes nothing.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="connections">Where an accepted upgrade is tracked, so the stop can end it; null: not tracked.</param>
    /// <returns>Whether the counter stands in for this request's body: false only on a server without a response body feature,
    /// whose bytes are then not known.</returns>
    public bool Begin(HttpContext context, UpgradedConnections? connections = null)
    {
        var features = context.Features;
        var response = features.Get<IHttpResponseBodyFeature>();
        if (response is null || ReferenceEquals(response, this))
        {
            return response is not null;
        }

        // Invariant: Kestrel puts its own body, upgrade and extended-CONNECT features back at the start of every request on a
        // connection or pooled stream, so what is read here is always this request's and a feature this counter set for an earlier
        // request is never seen again (BodyBytesTests.EachRequestOnAConnectionIsCountedOnItsOwnFeatures pins it).
        _bytesOut = 0;
        _bytesIn = 0;
        _firstBodyRead = 0;
        _lastBodyRead = 0;
        _bodyEnded = false;
        _discardsBody = HttpMethods.IsHead(context.Request.Method);
        _connections = connections;
        _upgradable = null;
        _response = response;
        _responseStream.Inner = response.Stream;
        _responseWriter.Inner = response.Writer;
        features.Set<IHttpResponseBodyFeature>(this);

        if (features.Get<IHttpRequestFeature>() is { } request)
        {
            _requestStream.Inner = request.Body;
            request.Body = _requestStream;
        }

        _upgrade = features.Get<IHttpUpgradeFeature>() is { IsUpgradableRequest: true } upgrade ? upgrade : null;
        if (_upgrade is not null)
        {
            features.Set<IHttpUpgradeFeature>(this);
            _upgradable = context;
        }

        _connect = features.Get<IHttpExtendedConnectFeature>() is { IsExtendedConnect: true } connect ? connect : null;
        if (_connect is not null)
        {
            features.Set<IHttpExtendedConnectFeature>(this);
            _upgradable = context;
        }

        return true;
    }

    // One writer and one reader at a time per direction (a stream or pipe is never written or read concurrently), but the two
    // directions run on different threads on a duplex forward, so each count has its own field and its one writer.
    private void AddOut(long bytes) => _bytesOut += bytes;

    private void AddIn(long bytes) => _bytesIn += bytes;

    // A read is timed when it completes; one timestamp per read that returned bytes, and none for the end of the body.
    private void AddBody(long bytes, bool ended)
    {
        _bytesIn += bytes;
        _bodyEnded |= ended;
        if (bytes > 0)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_firstBodyRead == 0)
            {
                _firstBodyRead = now;
            }

            _lastBodyRead = now;
        }
    }

    Stream IHttpResponseBodyFeature.Stream => _responseStream;

    PipeWriter IHttpResponseBodyFeature.Writer => _responseWriter;

    void IHttpResponseBodyFeature.DisableBuffering() => _response!.DisableBuffering();

    Task IHttpResponseBodyFeature.StartAsync(CancellationToken cancellationToken) => _response!.StartAsync(cancellationToken);

    Task IHttpResponseBodyFeature.CompleteAsync() => _response!.CompleteAsync();

    // Through the counting stream rather than the server's own SendFileAsync, which would write past the count.
    Task IHttpResponseBodyFeature.SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken) =>
        SendFileFallback.SendFileAsync(_responseStream, path, offset, count, cancellationToken);

    bool IHttpUpgradeFeature.IsUpgradableRequest => _upgrade?.IsUpgradableRequest ?? false;

    async Task<Stream> IHttpUpgradeFeature.UpgradeAsync()
    {
        _upgradedStream.Inner = await _upgrade!.UpgradeAsync().ConfigureAwait(false);
        return Tracked(string.Equals(_upgradable!.Request.Headers.Upgrade, "websocket", StringComparison.OrdinalIgnoreCase));
    }

    bool IHttpExtendedConnectFeature.IsExtendedConnect => _connect?.IsExtendedConnect ?? false;

    string? IHttpExtendedConnectFeature.Protocol => _connect?.Protocol;

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    async ValueTask<Stream> IHttpExtendedConnectFeature.AcceptAsync()
    {
        _upgradedStream.Inner = await _connect!.AcceptAsync().ConfigureAwait(false);
        return Tracked(string.Equals(_connect.Protocol, "websocket", StringComparison.OrdinalIgnoreCase));
    }

    // The closer writes through the counting stream, so a close frame the stop sends is counted like any other byte out.
    private Stream Tracked(bool webSocket) =>
        _connections is { } connections ? connections.Track(_upgradable!, _upgradedStream, webSocket) : _upgradedStream;

    /// <summary>A stream that passes everything to <see cref="Inner"/> and counts what was read and written through it.</summary>
    private sealed class CountingStream(BodyCounter owner, bool timesReads = false) : Stream
    {
        public Stream Inner { get; set; } = Null;

        public override bool CanRead => Inner.CanRead;

        public override bool CanWrite => Inner.CanWrite;

        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Flush() => Inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => Inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => Counted(Inner.Read(buffer, offset, count), count);

        public override int Read(Span<byte> buffer) => Counted(Inner.Read(buffer), buffer.Length);

        public override int ReadByte()
        {
            var value = Inner.ReadByte();
            Counted(value >= 0 ? 1 : 0, 1);
            return value;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = Inner.ReadAsync(buffer, cancellationToken);
            return read.IsCompletedSuccessfully ? new ValueTask<int>(Counted(read.Result, buffer.Length)) : AwaitReadAsync(read, buffer.Length);
        }

        // Stream's own array-based async methods fall back to the synchronous ones on a pool thread, which Kestrel refuses; these go
        // to the Memory overloads instead. Only legacy callers use them (YARP and the gate use Memory), so the Task they cost is accepted.
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count)
        {
            owner.AddOut(count);
            Inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            owner.AddOut(buffer.Length);
            Inner.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            owner.AddOut(1);
            Inner.WriteByte(value);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            owner.AddOut(buffer.Length);
            return Inner.WriteAsync(buffer, cancellationToken);
        }

        // An upgraded stream is the client's connection: closing it must close that. The body streams' own Dispose is the
        // server's to decide, and it ignores it.
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Inner.Dispose();
            }

            base.Dispose(disposing);
        }

        // A read for no bytes (a reader waiting for data without a buffer) that returns none is not the end of the body.
        private int Counted(int read, int requested)
        {
            if (timesReads)
            {
                owner.AddBody(read, ended: read == 0 && requested > 0);
            }
            else
            {
                owner.AddIn(read);
            }

            return read;
        }

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        private async ValueTask<int> AwaitReadAsync(ValueTask<int> read, int requested) => Counted(await read.ConfigureAwait(false), requested);

    }

    /// <summary>The response body's pipe, counting what is advanced or written through it.</summary>
    private sealed class CountingPipeWriter(BodyCounter owner) : PipeWriter
    {
        public PipeWriter Inner { get; set; } = null!;

        public override bool CanGetUnflushedBytes => Inner.CanGetUnflushedBytes;

        public override long UnflushedBytes => Inner.UnflushedBytes;

        public override void Advance(int bytes)
        {
            Inner.Advance(bytes);
            owner.AddOut(bytes);
        }

        public override Memory<byte> GetMemory(int sizeHint = 0) => Inner.GetMemory(sizeHint);

        public override Span<byte> GetSpan(int sizeHint = 0) => Inner.GetSpan(sizeHint);

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default) => Inner.FlushAsync(cancellationToken);

        public override ValueTask<FlushResult> WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
        {
            owner.AddOut(source.Length);
            return Inner.WriteAsync(source, cancellationToken);
        }

        public override void CancelPendingFlush() => Inner.CancelPendingFlush();

        public override void Complete(Exception? exception = null) => Inner.Complete(exception);

        public override ValueTask CompleteAsync(Exception? exception = null) => Inner.CompleteAsync(exception);
    }
}
