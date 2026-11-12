using System.Security.Cryptography;

namespace Paduraksa.Vault.Crypto;

/// <summary>
/// A plaintext the vault is holding: a pinned array (the GC can't copy it around) that is zeroed on disposal. Best
/// effort: it cannot stop a process dump taken while the value is live, or the copies a caller makes.
/// </summary>
internal sealed class SecretBuffer : IDisposable
{
    private readonly byte[] _bytes;

    private SecretBuffer(byte[] bytes) => _bytes = bytes;

    public int Length => _bytes.Length;

    public ReadOnlySpan<byte> Span => _bytes;

    /// <summary>A read-only view as memory, for the one capability that lends the value to an async callback.</summary>
    public ReadOnlyMemory<byte> Memory => _bytes;

    /// <summary>The writable view, for the code that fills the buffer.</summary>
    public Span<byte> Fill => _bytes;

    /// <summary>The writable view as memory, for reading a stream straight into the buffer.</summary>
    public Memory<byte> FillMemory => _bytes;

    /// <summary>A zero-filled buffer to decrypt or generate into.</summary>
    public static SecretBuffer Allocate(int length) => new(GC.AllocateArray<byte>(length, pinned: true));

    public static SecretBuffer CopyOf(ReadOnlySpan<byte> value)
    {
        var buffer = Allocate(value.Length);
        value.CopyTo(buffer._bytes);
        return buffer;
    }

    public void Dispose() => CryptographicOperations.ZeroMemory(_bytes);
}
