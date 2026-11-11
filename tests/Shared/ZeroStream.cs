// Linked into Paduraksa.Tests.Proxy and Paduraksa.Tests.Integration (<Compile Include Link>), like HeaderEchoUpstreamHost.
namespace Paduraksa.Tests.Shared;

/// <summary>A readable stream of <c>length</c> zero bytes that doesn't hold them: an upload of any size for a test.</summary>
internal sealed class ZeroStream(long length) : Stream
{
    private long _position;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => length;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var count = (int)Math.Min(buffer.Length, length - _position);
        buffer[..count].Clear();
        _position += count;
        return count;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(Read(buffer.Span));

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
