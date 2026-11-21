using System.Buffers;
using System.Buffers.Text;

namespace Paduraksa.Proxy.Honeypot;

/// <summary>
/// A UTF-8 sink over an <see cref="IBufferWriter{T}"/>, or over a fixed span (for a title that is measured and reused). A page is
/// written fragment by fragment into the writer's own buffer, so no page is ever built as a string.
/// </summary>
internal ref struct Utf8Out
{
    private const int ChunkHint = 4096;

    private readonly IBufferWriter<byte>? _sink;
    private Span<byte> _buffer;
    private int _position;

    public Utf8Out(IBufferWriter<byte> sink)
    {
        _sink = sink;
        _buffer = default;
        _position = 0;
    }

    /// <summary>A sink over <paramref name="fixedBuffer"/>: what does not fit is dropped.</summary>
    public Utf8Out(Span<byte> fixedBuffer)
    {
        _sink = null;
        _buffer = fixedBuffer;
        _position = 0;
    }

    /// <summary>The bytes written into a fixed buffer.</summary>
    public readonly int Length => _position;

    public void Write(scoped ReadOnlySpan<byte> value)
    {
        if (value.Length > _buffer.Length - _position && !Grow(value.Length))
        {
            return;
        }

        value.CopyTo(_buffer[_position..]);
        _position += value.Length;
    }

    public void Write(byte value)
    {
        if (_position == _buffer.Length && !Grow(1))
        {
            return;
        }

        _buffer[_position++] = value;
    }

    public void WriteInt(int value)
    {
        if (_buffer.Length - _position < 11 && !Grow(11))
        {
            return;
        }

        _ = Utf8Formatter.TryFormat(value, _buffer[_position..], out var written);
        _position += written;
    }

    /// <summary>Writes <paramref name="value"/> with at least <paramref name="digits"/> digits, zero-padded.</summary>
    public void WriteInt(int value, int digits)
    {
        Span<byte> scratch = stackalloc byte[11];
        _ = Utf8Formatter.TryFormat(value, scratch, out var length);
        for (var pad = length; pad < digits; pad++)
        {
            Write((byte)'0');
        }

        Write(scratch[..length]);
    }

    /// <summary>Writes a word with its first letter upper-cased.</summary>
    public void WriteCapitalized(scoped ReadOnlySpan<byte> word)
    {
        if (word.IsEmpty)
        {
            return;
        }

        Write(word[0] is >= (byte)'a' and <= (byte)'z' ? (byte)(word[0] - 32) : word[0]);
        Write(word[1..]);
    }

    public void WriteHex(int value, int digits)
    {
        for (var shift = (digits - 1) * 4; shift >= 0; shift -= 4)
        {
            Write("0123456789abcdef"u8[(value >> shift) & 0xF]);
        }
    }

    /// <summary>Hands what is buffered to the sink. Every page ends with this.</summary>
    public void Flush()
    {
        if (_sink is not null && _position > 0)
        {
            _sink.Advance(_position);
            _position = 0;
            _buffer = default;
        }
    }

    private bool Grow(int needed)
    {
        if (_sink is null)
        {
            return false;
        }

        if (_position > 0)
        {
            _sink.Advance(_position);
            _position = 0;
        }

        _buffer = _sink.GetSpan(Math.Max(needed, ChunkHint));
        return true;
    }
}
