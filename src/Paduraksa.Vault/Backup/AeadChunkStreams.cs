using System.Buffers.Binary;
using System.Security.Cryptography;
using Paduraksa.Core.Vault;

namespace Paduraksa.Vault.Backup;

/// <summary>
/// The chunked AES-256-GCM format both stream types share (the STREAM construction of Hoang, Reyhanitabar, Rogaway and Vizar,
/// as in age and Tink): the plaintext is cut into <see cref="BackupHeader.ChunkSize"/> chunks, each sealed on its own, so a
/// multi-gigabyte archive is encrypted and decrypted in constant memory and still can't be reordered, cut short or extended.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per chunk:</b> nonce = 4 zero bytes and the chunk's index as a 64-bit big-endian counter (the key is unique to the archive: it
/// comes from a random salt, so the counter alone makes every nonce under it unique); associated data = the whole 48-byte header and
/// one more byte, 1 for the last chunk and 0 otherwise; the chunk on disk is the ciphertext and the 16-byte tag.
/// </para>
/// <para>
/// <b>Where it ends.</b> Every chunk but the last holds exactly <see cref="BackupHeader.ChunkSize"/> plaintext bytes, the last holds
/// fewer (possibly none; a plaintext that is an exact multiple gets an empty last chunk). A reader therefore knows a chunk is
/// the last one from its length and authenticates it as such: cutting an archive at a chunk boundary leaves no last chunk
/// (refused as truncated), cutting inside a chunk fails its tag, and a chunk moved, dropped or duplicated fails its counter.
/// </para>
/// <para>
/// <b>Wrong passphrase or damage.</b> A tag that fails on the <i>first</i> chunk means the key is not the archive's (or the header
/// differs from the one it was sealed with, which is indistinguishable): <see cref="BackupFailure.WrongPassphrase"/>. Anywhere
/// later it is <see cref="BackupFailure.Damaged"/>, since the key has already proven itself.
/// </para>
/// </remarks>
internal static class AeadChunk
{
    public const int TagLength = 16;

    public const int NonceLength = 12;

    public static void Nonce(long index, Span<byte> nonce)
    {
        nonce.Clear();
        BinaryPrimitives.WriteInt64BigEndian(nonce[4..], index);
    }

    public static byte[] AssociatedData(byte[] header, bool last)
    {
        var aad = new byte[header.Length + 1];
        header.CopyTo(aad, 0);
        aad[^1] = last ? (byte)1 : (byte)0;
        return aad;
    }
}

/// <summary>Write side: everything written is encrypted into <c>destination</c> after the header. <see cref="Complete"/> writes the last chunk.</summary>
internal sealed class AeadEncryptStream : Stream
{
    private readonly Stream _destination;
    private readonly AesGcm _aes;
    private readonly byte[] _header;
    private readonly byte[] _buffer = GC.AllocateArray<byte>(BackupHeader.ChunkSize, pinned: true);
    private readonly byte[] _sealed = new byte[BackupHeader.ChunkSize + AeadChunk.TagLength];
    private int _buffered;
    private long _index;
    private bool _completed;

    public AeadEncryptStream(Stream destination, ReadOnlySpan<byte> key, BackupHeader header)
    {
        _destination = destination;
        _aes = new AesGcm(key, AeadChunk.TagLength);
        _header = header.ToBytes();
        _destination.Write(_header);
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        while (!buffer.IsEmpty)
        {
            // A full buffer is only sealed once more data follows: whether it is the last chunk is not known before then.
            if (_buffered == _buffer.Length)
            {
                SealChunk(last: false);
            }

            var take = Math.Min(buffer.Length, _buffer.Length - _buffered);
            buffer[..take].CopyTo(_buffer.AsSpan(_buffered));
            _buffered += take;
            buffer = buffer[take..];
        }
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    /// <summary>Seals what is left as the last chunk (after a full one, an empty last chunk) and flushes the destination.</summary>
    public void Complete()
    {
        if (_completed)
        {
            return;
        }

        if (_buffered == _buffer.Length)
        {
            SealChunk(last: false);
        }

        SealChunk(last: true);
        _completed = true;
        _destination.Flush();
    }

    public override void Flush()
    {
        // Nothing is released before Complete: a chunk's role (last or not) is only known then.
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _aes.Dispose();
            CryptographicOperations.ZeroMemory(_buffer);
        }

        base.Dispose(disposing);
    }

    private void SealChunk(bool last)
    {
        Span<byte> nonce = stackalloc byte[AeadChunk.NonceLength];
        AeadChunk.Nonce(_index++, nonce);
        _aes.Encrypt(nonce, _buffer.AsSpan(0, _buffered), _sealed.AsSpan(0, _buffered), _sealed.AsSpan(_buffered, AeadChunk.TagLength), AeadChunk.AssociatedData(_header, last));
        _destination.Write(_sealed.AsSpan(0, _buffered + AeadChunk.TagLength));
        _buffered = 0;
    }
}

/// <summary>Read side: yields the plaintext of <c>source</c> (positioned after the header) chunk by chunk, and fails on the first chunk that does not authenticate.</summary>
internal sealed class AeadDecryptStream : Stream
{
    private readonly Stream _source;
    private readonly AesGcm _aes;
    private readonly byte[] _header;
    private readonly byte[] _plain = GC.AllocateArray<byte>(BackupHeader.ChunkSize, pinned: true);
    private readonly byte[] _sealed = new byte[BackupHeader.ChunkSize + AeadChunk.TagLength];
    private int _available;
    private int _position;
    private long _index;
    private bool _ended;

    public AeadDecryptStream(Stream source, ReadOnlySpan<byte> key, byte[] header)
    {
        _source = source;
        _aes = new AesGcm(key, AeadChunk.TagLength);
        _header = header;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(Span<byte> buffer)
    {
        while (_position == _available)
        {
            if (_ended)
            {
                return 0;
            }

            LoadChunk();
        }

        var count = Math.Min(buffer.Length, _available - _position);
        _plain.AsSpan(_position, count).CopyTo(buffer);
        _position += count;
        return count;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override void Flush()
    {
        // Read-only.
    }

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _aes.Dispose();
            CryptographicOperations.ZeroMemory(_plain);
        }

        base.Dispose(disposing);
    }

    private void LoadChunk()
    {
        var read = _source.ReadAtLeast(_sealed, _sealed.Length, throwOnEndOfStream: false);
        if (read == 0 && _index > 0)
        {
            throw new BackupException(BackupFailure.Damaged, "The backup is incomplete: it ends before its last block.");
        }

        var last = read < _sealed.Length;
        var failure = _index == 0 ? BackupFailure.WrongPassphrase : BackupFailure.Damaged;
        if (read < AeadChunk.TagLength)
        {
            throw new BackupException(failure, Message(failure));
        }

        var length = read - AeadChunk.TagLength;
        Span<byte> nonce = stackalloc byte[AeadChunk.NonceLength];
        AeadChunk.Nonce(_index, nonce);
        try
        {
            _aes.Decrypt(nonce, _sealed.AsSpan(0, length), _sealed.AsSpan(length, AeadChunk.TagLength), _plain.AsSpan(0, length), AeadChunk.AssociatedData(_header, last));
        }
        catch (CryptographicException)
        {
            throw new BackupException(failure, Message(failure));
        }

        _index++;
        _available = length;
        _position = 0;
        _ended = last;
    }

    private static string Message(BackupFailure failure) =>
        failure == BackupFailure.WrongPassphrase
            ? "The passphrase is wrong, or this backup is damaged."
            : "The backup is damaged: a block failed its authentication check (it was changed, or cut short). Nothing was restored.";
}
