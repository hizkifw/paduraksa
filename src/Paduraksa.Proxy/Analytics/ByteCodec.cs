using System.Text;

namespace Paduraksa.Proxy.Analytics;

/// <summary>A growable buffer the analytics blobs are written into: unsigned LEB128 integers, length-prefixed UTF-8 strings and raw bytes.</summary>
internal sealed class ByteWriter
{
    private byte[] _buffer = new byte[256];
    private int _length;

    public int Length => _length;

    public ReadOnlySpan<byte> Written => _buffer.AsSpan(0, _length);

    public void Add(byte value)
    {
        Ensure(1);
        _buffer[_length++] = value;
    }

    public void Add(ReadOnlySpan<byte> bytes)
    {
        Ensure(bytes.Length);
        bytes.CopyTo(_buffer.AsSpan(_length));
        _length += bytes.Length;
    }

    public void Varint(long value)
    {
        // Counts are never negative; a negative one would take ten bytes and read back as itself, so it is only wasteful, not wrong.
        var rest = (ulong)value;
        while (rest >= 0x80)
        {
            Add((byte)(rest | 0x80));
            rest >>= 7;
        }

        Add((byte)rest);
    }

    public void Text(string value)
    {
        var bytes = Encoding.UTF8.GetByteCount(value);
        Varint(bytes);
        Ensure(bytes);
        Encoding.UTF8.GetBytes(value, _buffer.AsSpan(_length));
        _length += bytes;
    }

    private void Ensure(int more)
    {
        if (_length + more > _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + more));
        }
    }
}

/// <summary>Reads what <see cref="ByteWriter"/> wrote. A short or malformed input sets <see cref="Failed"/> and every later read returns zero or empty, so a decoder checks once at the end.</summary>
internal ref struct ByteReader
{
    private readonly ReadOnlySpan<byte> _source;
    private int _position;

    public ByteReader(ReadOnlySpan<byte> source)
    {
        _source = source;
        _position = 0;
        Failed = false;
    }

    public bool Failed { get; private set; }

    public int Remaining => _source.Length - _position;

    public byte Byte()
    {
        if (_position >= _source.Length)
        {
            Failed = true;
            return 0;
        }

        return _source[_position++];
    }

    public long Varint()
    {
        ulong result = 0;
        for (var shift = 0; shift < 70; shift += 7)
        {
            var next = Byte();
            if (Failed)
            {
                return 0;
            }

            result |= (ulong)(next & 0x7F) << shift;
            if ((next & 0x80) == 0)
            {
                return (long)result;
            }
        }

        Failed = true;
        return 0;
    }

    /// <summary>A count that sizes a loop: at most <paramref name="limit"/>, so a corrupt blob can't ask for a billion entries.</summary>
    public int Count(int limit)
    {
        var value = Varint();
        if (value < 0 || value > limit)
        {
            Failed = true;
            return 0;
        }

        return (int)value;
    }

    public string Text(int maxBytes)
    {
        var length = Count(maxBytes);
        if (Failed || length > Remaining)
        {
            Failed = true;
            return string.Empty;
        }

        var text = Encoding.UTF8.GetString(_source.Slice(_position, length));
        _position += length;
        return text;
    }

    public ReadOnlySpan<byte> Rest => _source[_position..];

    public void Skip(int bytes)
    {
        if (bytes > Remaining)
        {
            Failed = true;
            return;
        }

        _position += bytes;
    }
}
