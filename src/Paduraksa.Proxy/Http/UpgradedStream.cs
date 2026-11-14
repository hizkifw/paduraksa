using System.Runtime.CompilerServices;

using Microsoft.AspNetCore.Http;

namespace Paduraksa.Proxy.Http;

/// <summary>
/// The client's side of one upgraded connection as YARP sees it: everything passes through, and the stop can end it
/// (<see cref="UpgradedConnections"/>). On a WebSocket it follows the frames the upstream sends, so a close frame can go out between two of
/// them; once it has, what the upstream still sends is dropped, because nothing may follow a close frame.
/// </summary>
/// <remarks>
/// One writer at a time (YARP's upstream-to-client copy) and one closer, which only writes at a frame boundary while the writer is
/// idle. Both claim the write side through <see cref="_state"/>, and the idle states say whether the last write ended on a boundary,
/// so the closer's claim and the boundary are one atomic read and nothing is written in the middle of a frame.
/// </remarks>
internal sealed class UpgradedStream : Stream
{
    private const int IdleAtBoundary = 0;
    private const int IdleMidFrame = 1;
    private const int Writing = 2;
    private const int Closed = 3;

    // FIN + close opcode, a 2-byte unmasked payload: 1001 Going Away. A server's frames are never masked.
    private static readonly byte[] GoingAway = [0x88, 0x02, 0x03, 0xE9];

    private readonly UpgradedConnections _owner;
    private readonly HttpContext _context;
    private readonly Stream _inner;
    private readonly bool _webSocket;
    private readonly TaskCompletionSource _endedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _gate = new();
    private int _state;
    private bool _ended;
    private Task? _closing;

    // The frame the upstream is sending: header bytes still to come, then payload bytes still to come.
    private int _headerPosition;
    private int _extendedLengthLeft;
    private int _maskLeft;
    private ulong _payloadLength;
    private ulong _payloadLeft;

    public UpgradedStream(UpgradedConnections owner, HttpContext context, Stream inner, bool webSocket)
    {
        _owner = owner;
        _context = context;
        _inner = inner;
        _webSocket = webSocket;
    }

    public override bool CanRead => _inner.CanRead;

    public override bool CanWrite => _inner.CanWrite;

    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>Whether the frames written so far end on a frame boundary (tests).</summary>
    internal bool AtFrameBoundary => _headerPosition == 0 && _payloadLeft == 0;

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => _inner.Read(buffer);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
        if (!_webSocket)
        {
            _inner.Flush();
        }
        else if (TryClaim(out var idle))
        {
            try
            {
                _inner.Flush();
            }
            finally
            {
                Volatile.Write(ref _state, idle);
            }
        }
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        if (!_webSocket)
        {
            return _inner.FlushAsync(cancellationToken);
        }

        if (!TryClaim(out var idle))
        {
            return Task.CompletedTask;
        }

        Task flush;
        try
        {
            flush = _inner.FlushAsync(cancellationToken);
        }
        catch
        {
            Volatile.Write(ref _state, idle);
            throw;
        }

        if (flush.IsCompleted)
        {
            Volatile.Write(ref _state, idle);
            return flush;
        }

        return AwaitFlushAsync(flush, idle);
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (!_webSocket)
        {
            _inner.Write(buffer);
            return;
        }

        if (!TryClaim(out _))
        {
            return;
        }

        var next = IdleMidFrame;
        try
        {
            _inner.Write(buffer);
            next = Follow(buffer);
        }
        finally
        {
            Volatile.Write(ref _state, next);
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!_webSocket)
        {
            return _inner.WriteAsync(buffer, cancellationToken);
        }

        if (!TryClaim(out var idle))
        {
            return ValueTask.CompletedTask;
        }

        ValueTask write;
        try
        {
            write = _inner.WriteAsync(buffer, cancellationToken);
        }
        catch
        {
            // What went out of this buffer is not known, so the stream is no longer known to be at a boundary.
            Volatile.Write(ref _state, IdleMidFrame);
            throw;
        }

        if (write.IsCompletedSuccessfully)
        {
            Volatile.Write(ref _state, Follow(buffer.Span));
            return write;
        }

        return AwaitWriteAsync(write, buffer);
    }

    /// <summary>Ends the connection: a WebSocket's close frame and its handshake where possible, else an abort. Idempotent.</summary>
    internal Task CloseAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return _closing ??= CloseCoreAsync(cancellationToken);
        }
    }

    /// <summary>The request is over: from now on its context may belong to another request and must not be aborted.</summary>
    internal void Ended()
    {
        lock (_gate)
        {
            _ended = true;
        }

        _endedSignal.TrySetResult();
        _owner.Remove(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task CloseCoreAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        if (_webSocket && await SendGoingAwayAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                // The client answers with its own close, which YARP forwards; the upstream then closes and the forward ends.
                await _endedSignal.Task.WaitAsync(UpgradedConnections.HandshakeWait, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
            }
        }

        Abort();
    }

    private async Task<bool> SendGoingAwayAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + UpgradedConnections.FrameBoundaryWait;
        while (!_endedSignal.Task.IsCompleted && !cancellationToken.IsCancellationRequested)
        {
            if (Interlocked.CompareExchange(ref _state, Closed, IdleAtBoundary) == IdleAtBoundary)
            {
                try
                {
                    await _inner.WriteAsync(GoingAway, cancellationToken).ConfigureAwait(false);
                    await _inner.FlushAsync(cancellationToken).ConfigureAwait(false);
                    return true;
                }
#pragma warning disable CA1031 // The client may already be gone; the abort that follows ends the connection either way.
                catch (Exception)
#pragma warning restore CA1031
                {
                    return false;
                }
            }

            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return false;
    }

    private void Abort()
    {
        lock (_gate)
        {
            if (!_ended)
            {
                _context.Abort();
            }
        }
    }

    // YARP never writes concurrently, so a failed claim can only mean the close frame went out (or is going out): drop the write.
    private bool TryClaim(out int idle)
    {
        idle = Volatile.Read(ref _state);
        return idle != Closed && Interlocked.CompareExchange(ref _state, Writing, idle) == idle;
    }

    private int Follow(ReadOnlySpan<byte> written)
    {
        while (!written.IsEmpty)
        {
            if (_payloadLeft > 0)
            {
                var skip = (int)Math.Min(_payloadLeft, (ulong)written.Length);
                _payloadLeft -= (ulong)skip;
                written = written[skip..];
                continue;
            }

            var value = written[0];
            written = written[1..];
            switch (_headerPosition)
            {
                case 0:
                    _headerPosition = 1;
                    break;
                case 1:
                    var length = value & 0x7F;
                    _maskLeft = (value & 0x80) != 0 ? 4 : 0;
                    _extendedLengthLeft = length switch { 126 => 2, 127 => 8, _ => 0 };
                    _payloadLength = length < 126 ? (ulong)length : 0;
                    _headerPosition = 2;
                    break;
                default:
                    if (_extendedLengthLeft > 0)
                    {
                        _payloadLength = (_payloadLength << 8) | value;
                        _extendedLengthLeft--;
                    }
                    else
                    {
                        _maskLeft--;
                    }

                    break;
            }

            if (_headerPosition == 2 && _extendedLengthLeft == 0 && _maskLeft == 0)
            {
                _headerPosition = 0;
                _payloadLeft = _payloadLength;
            }
        }

        return AtFrameBoundary ? IdleAtBoundary : IdleMidFrame;
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask AwaitWriteAsync(ValueTask write, ReadOnlyMemory<byte> buffer)
    {
        var idle = IdleMidFrame;
        try
        {
            await write.ConfigureAwait(false);
            idle = Follow(buffer.Span);
        }
        finally
        {
            Volatile.Write(ref _state, idle);
        }
    }

    private async Task AwaitFlushAsync(Task flush, int idle)
    {
        try
        {
            await flush.ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _state, idle);
        }
    }
}
