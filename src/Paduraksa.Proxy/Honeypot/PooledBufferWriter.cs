using System.Buffers;

namespace Paduraksa.Proxy.Honeypot;

/// <summary>
/// A growing buffer over <see cref="ArrayPool{T}"/>, so a page is generated first and its length known before the headers go out.
/// One contiguous array, because the answer is written from <see cref="WrittenMemory"/> in one call. Bounded: a site that writes
/// without end fails the request instead of the process.
/// </summary>
internal sealed class PooledBufferWriter : IBufferWriter<byte>, IDisposable
{
    /// <summary>The most a page may be: far beyond any page the generator writes.</summary>
    internal const int MaxLength = 4 * 1024 * 1024;

    private const int InitialLength = 16 * 1024;

    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(InitialLength);
    private int _written;

    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);

    public int Length => _written;

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > _buffer.Length - _written)
        {
            throw new InvalidOperationException("Advanced past what was handed out.");
        }

        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buffer.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buffer.AsSpan(_written);
    }

    public void Dispose()
    {
        var buffer = _buffer;
        _buffer = [];
        _written = 0;
        if (buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void Ensure(int sizeHint)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        var needed = Math.Max(sizeHint, 1);
        if (_buffer.Length - _written >= needed)
        {
            return;
        }

        var length = Math.Max(_buffer.Length * 2, _written + needed);
        if (length > MaxLength)
        {
            throw new InvalidOperationException("A trap page is larger than the limit.");
        }

        var bigger = ArrayPool<byte>.Shared.Rent(length);
        _buffer.AsSpan(0, _written).CopyTo(bigger);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = bigger;
    }
}
