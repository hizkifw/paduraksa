namespace Paduraksa.Dashboard.Certificates;

/// <summary>
/// Reads a certificate file and a separate key file as one PEM stream (a newline between them), for the vault's import, which takes the
/// chain and its key together. Nothing is buffered beyond the caller's read: each part is read straight from its source, in order.
/// </summary>
internal sealed class PemConcatStream(Stream first, Stream second) : Stream
{
    private static readonly byte[] Separator = "\n"u8.ToArray();

    private int _part;
    private int _separatorRead;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (buffer.Length > 0)
        {
            switch (_part)
            {
                case 0:
                    var read = await first.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read > 0)
                    {
                        return read;
                    }

                    _part = 1;
                    break;
                case 1:
                    if (_separatorRead < Separator.Length)
                    {
                        var count = Math.Min(buffer.Length, Separator.Length - _separatorRead);
                        Separator.AsMemory(_separatorRead, count).CopyTo(buffer);
                        _separatorRead += count;
                        return count;
                    }

                    _part = 2;
                    break;
                case 2:
                    return await second.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
        }

        return 0;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            first.Dispose();
            second.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await first.DisposeAsync().ConfigureAwait(false);
        await second.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
