using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace Paduraksa.Vault.Backup;

/// <summary>
/// Argon2id (RFC 9106) from the passphrase to the archive's AES-256 key. <c>Konscious.Security.Cryptography.Argon2</c> is a managed
/// implementation (no native library to ship, so the container and every platform behave alike); a test pins it to RFC 9106's own
/// Argon2id vector.
/// </summary>
/// <remarks>
/// The passphrase is its UTF-8 bytes exactly as typed (no normalization: the platform's normalization can be off, and a rule that
/// differs between the machine that made an archive and the one that opens it would lock people out). The pinned copies are zeroed
/// after use; the library's own internal buffers and the passphrase string a caller started from are not ours to zero, which is
/// why the key is held in memory only for the duration of one archive.
/// </remarks>
internal static class BackupKeyDerivation
{
    public const int KeyLength = 32;

    /// <summary>The 32-byte key, in a pinned array the caller zeroes.</summary>
    public static byte[] Derive(ReadOnlySpan<char> passphrase, ReadOnlySpan<byte> salt, int memoryKiB, int iterations, int parallelism, ReadOnlySpan<byte> associatedData = default, ReadOnlySpan<byte> secret = default)
    {
        // Exactly sized and handed to the library as is, so the one copy the library keeps is the one zeroed below.
        var password = GC.AllocateArray<byte>(Encoding.UTF8.GetByteCount(passphrase), pinned: true);
        byte[]? derived = null;
        try
        {
            Encoding.UTF8.GetBytes(passphrase, password);
            var argon = new Argon2id(password)
            {
                Salt = salt.ToArray(),
                MemorySize = memoryKiB,
                Iterations = iterations,
                DegreeOfParallelism = parallelism,
            };
            if (!associatedData.IsEmpty)
            {
                argon.AssociatedData = associatedData.ToArray();
            }

            if (!secret.IsEmpty)
            {
                argon.KnownSecret = secret.ToArray();
            }

            using (argon)
            {
                derived = argon.GetBytes(KeyLength);
            }

            var key = GC.AllocateArray<byte>(KeyLength, pinned: true);
            derived.CopyTo(key, 0);
            return key;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
            if (derived is not null)
            {
                CryptographicOperations.ZeroMemory(derived);
            }
        }
    }
}
