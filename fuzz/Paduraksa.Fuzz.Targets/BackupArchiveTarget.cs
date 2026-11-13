using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

using Paduraksa.Core.Vault;
using Paduraksa.Vault.Backup;

namespace Paduraksa.Fuzz;

/// <summary>
/// A backup archive as a restore reads it, up to the passphrase check (the file is attacker-supplied: anyone can hand an admin a "backup"), and the manifest inside it.
/// The first input byte picks the shape. <b>Archive:</b> the 48-byte header through <see cref="BackupHeader.Parse"/>, then the chunked AES-256-GCM stream through
/// <see cref="AeadDecryptStream"/> under a fixed key (the key derivation is what costs, up to a gigabyte and sixteen passes; a header that passes may ask for it, so it is not run here: it is
/// the same bytes through the same parse). Properties: only a <see cref="BackupException"/> escapes, with the reason the header table and the chunk rules give (read here again from
/// the format's description: the magic and a short file are NotABackup, any other field is UnsupportedFormat, the first chunk's tag is WrongPassphrase, a later one or a missing last
/// chunk is Damaged); the same verdict twice; a header that is accepted is the bytes <see cref="BackupHeader.ToBytes"/> writes and asks for a derivation within the caps; what the stream yields is
/// what an independent AES-GCM reading of the chunks yields; and a read allocates a bounded amount whatever the header says (the checks come before anything sized by the file).
/// <b>Manifest:</b> <see cref="BackupManifestFormat.Parse"/> (a backup's author knows its passphrase, so the manifest is hostile too): only <see cref="BackupFailure.Damaged"/>, and a
/// manifest that parses writes and reads back to itself.
/// </summary>
internal static class BackupArchiveTarget
{
    private static readonly byte[] Key = [.. Enumerable.Repeat((byte)0x42, 32)];

    // The reader's own buffers (two chunks) and the plaintext it hands back, with room: nothing in it is sized by a header field.
    private const long AllocationBound = 600 * 1024;

    private const int Chunk = BackupHeader.ChunkSize;

    public static FuzzTarget Target { get; } = new(
        "backup-archive",
        "a backup archive up to the passphrase check (header, chunk framing, authentication) and its manifest",
        Run,
        Seeds);

    private static void Run(ReadOnlySpan<byte> input)
    {
        if (input.IsEmpty)
        {
            return;
        }

        if ((input[0] & 1) == 0)
        {
            Archive(input[1..].ToArray());
        }
        else
        {
            Manifest(input[1..].ToArray());
        }
    }

    private static void Archive(byte[] bytes)
    {
        var verdict = Checks.Deterministic(() => Read(bytes).Describe(), "the archive verdict");
        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = Read(bytes);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Checks.That(allocated <= AllocationBound + (3L * bytes.Length), $"reading a {bytes.Length}-byte archive allocated {allocated} B");
        Checks.That(verdict == result.Describe(), "the verdict changed");

        var (header, why) = ExpectedHeader(bytes);
        if (header is null)
        {
            Checks.That(result.Failure == why, $"the header is {why} by the format's table and {result.Describe()} to the reader");
            return;
        }

        Checks.That(result.Failure is null or BackupFailure.WrongPassphrase or BackupFailure.Damaged, $"an acceptable header was refused as {result.Failure}");
        var parsed = BackupHeader.Parse(bytes.AsSpan(0, BackupHeader.Length));
        Checks.That(parsed.ToBytes().AsSpan().SequenceEqual(bytes.AsSpan(0, BackupHeader.Length)), "an accepted header is not what ToBytes writes for it");
        Checks.That(
            parsed.Format == 1 && parsed.Parallelism is >= 1 and <= BackupHeader.MaxParallelism && parsed.Iterations is >= 1 and <= BackupHeader.MaxIterations
                && parsed.MemoryKiB >= 8 * parsed.Parallelism && parsed.MemoryKiB <= BackupHeader.MaxMemoryKiB && parsed.Salt.Length == BackupHeader.SaltLength,
            "an accepted header asks for a key derivation outside the caps");
        Checks.That(parsed.Kdf == new BackupKdf("argon2id", BackupHeader.Argon2Version, parsed.MemoryKiB, parsed.Iterations, parsed.Parallelism), "the header and its KDF description disagree");

        var expected = Decrypt(bytes);
        Checks.That(expected.Failure == result.Failure, $"the chunks give {expected.Describe()} by the format's rules and {result.Describe()} to the reader");
        Checks.That(expected.Plain.AsSpan().SequenceEqual(result.Plain), "the plaintext differs from an independent decryption");
        Checks.That(result.Plain.Length <= bytes.Length, "the stream yields more than it was given");
    }

    private sealed record Outcome(BackupFailure? Failure, byte[] Plain)
    {
        public string Describe() => Failure is { } failure ? $"refused: {failure}" : $"read {Plain.Length} B: {Convert.ToHexString(SHA256.HashData(Plain))}";
    }

    /// <summary>The reader as the restore uses it: the header parse, then the stream drained.</summary>
    private static Outcome Read(byte[] bytes)
    {
        try
        {
            using MemoryStream archive = new(bytes);
            var header = new byte[BackupHeader.Length];
            if (archive.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length)
            {
                throw new BackupException(BackupFailure.NotABackup, "short");
            }

            _ = BackupHeader.Parse(header);
            using AeadDecryptStream decrypted = new(archive, Key, header);
            using MemoryStream plain = new();
            decrypted.CopyTo(plain);
            return new Outcome(null, plain.ToArray());
        }
        catch (BackupException ex)
        {
            return new Outcome(ex.Reason, []);
        }
    }

    /// <summary>The header's table, read again: null and the reason when a field is not allowed.</summary>
    private static (byte[]? Header, BackupFailure? Why) ExpectedHeader(byte[] bytes)
    {
        byte[] magic = [(byte)'P', (byte)'D', (byte)'K', (byte)'B', (byte)'A', (byte)'K', 0x1A, 0x0A];
        if (bytes.Length < BackupHeader.Length || !bytes.AsSpan(0, 8).SequenceEqual(magic))
        {
            return (null, BackupFailure.NotABackup);
        }

        var version = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(8));
        var memory = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(12));
        var passes = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16));
        var lanes = bytes[20];
        var chunk = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(40));
        var reserved = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(44));
        var allowed = version == 1 && bytes[10] == 1 && bytes[11] == 1 && bytes[21] == 0 && bytes[22] == 0 && bytes[23] == 0 && chunk == Chunk && reserved == 0
            && lanes is >= 1 and <= 16 && passes is >= 1 and <= 16 && memory >= 8u * lanes && memory <= 1024u * 1024u;
        return allowed ? (bytes[..BackupHeader.Length], null) : (null, BackupFailure.UnsupportedFormat);
    }

    /// <summary>
    /// The chunks as the format describes them, decrypted without the reader: each is sealed with a counter nonce and the header and a last-chunk byte as associated data,
    /// every chunk but the last is exactly <see cref="Chunk"/> bytes of plaintext, and a stream that ends on a full chunk has no last one.
    /// </summary>
    private static Outcome Decrypt(byte[] bytes)
    {
        var header = bytes.AsSpan(0, BackupHeader.Length).ToArray();
        var rest = bytes.AsSpan(BackupHeader.Length);
        var sealedLength = Chunk + AeadChunk.TagLength;
        using AesGcm aes = new(Key, AeadChunk.TagLength);
        MemoryStream plain = new();
        var chunks = Math.Max(1, (rest.Length + sealedLength - 1) / sealedLength);
        for (var index = 0; index < chunks; index++)
        {
            var piece = rest[Math.Min(rest.Length, index * sealedLength)..Math.Min(rest.Length, (index + 1) * sealedLength)];
            var fail = index == 0 ? BackupFailure.WrongPassphrase : BackupFailure.Damaged;
            if (piece.Length < AeadChunk.TagLength)
            {
                return new Outcome(fail, []);
            }

            var last = piece.Length < sealedLength;
            var nonce = new byte[12];
            BinaryPrimitives.WriteInt64BigEndian(nonce.AsSpan(4), index);
            var aad = new byte[header.Length + 1];
            header.CopyTo(aad, 0);
            aad[^1] = last ? (byte)1 : (byte)0;
            var cipher = piece[..^AeadChunk.TagLength];
            var output = new byte[cipher.Length];
            try
            {
                aes.Decrypt(nonce, cipher, piece[^AeadChunk.TagLength..], output, aad);
            }
            catch (CryptographicException)
            {
                return new Outcome(fail, []);
            }

            plain.Write(output);
            if (!last && index == chunks - 1)
            {
                return new Outcome(BackupFailure.Damaged, []);
            }
        }

        return new Outcome(null, plain.ToArray());
    }

    private static void Manifest(byte[] json)
    {
        BackupManifest? manifest = null;
        BackupFailure? failure = null;
        var verdict = Checks.Deterministic(
            () =>
            {
                try
                {
                    var parsed = BackupManifestFormat.Parse(json);
                    return $"{parsed.Format} {parsed.PaduraksaVersion} {parsed.CreatedAt:O} {string.Join(',', parsed.ConfigMigrations)} {string.Join(',', parsed.VaultMigrations)} {string.Join(',', parsed.Entries)} {parsed.Kdf}";
                }
                catch (BackupException ex)
                {
                    return "refused " + ex.Reason;
                }
            },
            "the manifest verdict");
        try
        {
            manifest = BackupManifestFormat.Parse(json);
        }
        catch (BackupException ex)
        {
            failure = ex.Reason;
        }

        if (manifest is null)
        {
            Checks.That(failure == BackupFailure.Damaged && verdict.StartsWith("refused", StringComparison.Ordinal), $"a manifest was refused as {failure}");
            return;
        }

        Checks.That(manifest.Format == BackupHeader.CurrentFormat, "a manifest of another format was accepted");

        // A manifest this build wrote reads back to itself; the strings a hostile one carries may not be writable as JSON (a lone surrogate), and then there is nothing to compare.
        var strict = new UTF8Encoding(false, true);
        try
        {
            foreach (var text in new[] { manifest.PaduraksaVersion, manifest.Kdf.Algorithm }.Concat(manifest.ConfigMigrations).Concat(manifest.VaultMigrations).Concat(manifest.Entries.SelectMany(static e => new[] { e.Name, e.Sha256 })))
            {
                _ = strict.GetByteCount(text);
            }
        }
        catch (EncoderFallbackException)
        {
            return;
        }

        var written = BackupManifestFormat.Write(manifest);
        var again = BackupManifestFormat.Parse(written);
        Checks.That(
            again.Format == manifest.Format && again.PaduraksaVersion == manifest.PaduraksaVersion && again.CreatedAt == manifest.CreatedAt && again.CreatedAt.Offset == manifest.CreatedAt.Offset
                && again.ConfigMigrations.SequenceEqual(manifest.ConfigMigrations) && again.VaultMigrations.SequenceEqual(manifest.VaultMigrations)
                && again.Entries.SequenceEqual(manifest.Entries) && again.Kdf == manifest.Kdf,
            "a manifest that parsed doesn't write and read back to itself");
        Checks.That(BackupManifestFormat.Write(again).AsSpan().SequenceEqual(written), "the manifest's written form is not stable");
    }

    private static IEnumerable<(string Name, byte[] Bytes)> Seeds()
    {
        static byte[] Header(int memory = 64, int passes = 1, int lanes = 1) =>
            BackupHeader.Create(memory, passes, lanes, [.. Enumerable.Range(1, BackupHeader.SaltLength).Select(static i => (byte)i)]).ToBytes();

        static byte[] Sealed(byte[] header, byte[] plain, byte[]? key = null)
        {
            using MemoryStream archive = new();
            var parsed = BackupHeader.Parse(header);
            using (AeadEncryptStream stream = new(archive, key ?? Key, parsed))
            {
                stream.Write(plain);
                stream.Complete();
            }

            return archive.ToArray();
        }

        static byte[] Archive(byte[] bytes) => [0, .. bytes];

        var hello = Encoding.ASCII.GetBytes("manifest.json and the files after it");
        var good = Sealed(Header(), hello);
        yield return ("valid-small", Archive(good));
        yield return ("valid-empty", Archive(Sealed(Header(), [])));
        yield return ("valid-2k", Archive(Sealed(Header(), [.. Enumerable.Range(0, 2048).Select(static i => (byte)(i * 31))])));
        yield return ("valid-other-cost", Archive(Sealed(Header(memory: 1024 * 1024, passes: 16, lanes: 16), hello)));
        yield return ("wrong-key", Archive(Sealed(Header(), hello, [.. Enumerable.Repeat((byte)9, 32)])));
        yield return ("flipped-tag", Archive([.. good[..^1], (byte)(good[^1] ^ 1)]));
        yield return ("cut-in-tag", Archive(good[..^8]));
        yield return ("header-only", Archive(good[..BackupHeader.Length]));
        yield return ("short-header", Archive(good[..20]));
        yield return ("empty-archive", Archive([]));
        yield return ("not-a-backup", Archive(Encoding.ASCII.GetBytes("SQLite format 3\0 and then some more bytes to pass the length of a header....")));
        yield return ("future-format", Archive([.. good[..8], 0, 2, .. good[10..]]));
        yield return ("unknown-cipher", Archive([.. good[..11], 2, .. good[12..]]));
        yield return ("reserved-bytes", Archive([.. good[..21], 1, .. good[22..]]));
        yield return ("zero-passes", Archive([.. good[..16], 0, 0, 0, 0, .. good[20..]]));
        yield return ("huge-memory", Archive([.. good[..12], 0xFF, 0xFF, 0xFF, 0xFF, .. good[16..]]));
        yield return ("no-lanes", Archive([.. good[..20], 0, .. good[21..]]));
        yield return ("wrong-chunk-size", Archive([.. good[..40], 0, 0, 1, 0, .. good[44..]]));

        var entries = "[{\"name\":\"paduraksa.db\",\"bytes\":4096,\"sha256\":\"" + new string('a', 64) + "\"}]";
        var manifest = "{\"format\":1,\"paduraksa\":\"0.1.0\",\"createdAt\":\"2026-11-13T12:00:00.0000000+00:00\",\"databases\":{\"config\":[\"20261101000000_Init\"],\"vault\":[\"20261101000001_Init\"]},\"entries\":" + entries
            + ",\"kdf\":{\"algorithm\":\"argon2id\",\"version\":19,\"memoryKiB\":65536,\"iterations\":3,\"parallelism\":4}}";
        yield return ("manifest", [1, .. Encoding.UTF8.GetBytes(manifest)]);
        yield return ("manifest-other-format", [1, .. Encoding.UTF8.GetBytes(manifest.Replace("\"format\":1", "\"format\":2", StringComparison.Ordinal))]);
        yield return ("manifest-bad-date", [1, .. Encoding.UTF8.GetBytes(manifest.Replace("2026-11-13T12:00:00.0000000+00:00", "9999-12-31T23:59:59.9999999-14:00", StringComparison.Ordinal))]);
        yield return ("manifest-no-kdf", [1, .. Encoding.UTF8.GetBytes(manifest[..manifest.IndexOf(",\"kdf\"", StringComparison.Ordinal)] + "}")]);
        yield return ("manifest-not-an-object", [1, .. "[1,2,3]"u8.ToArray()]);
        yield return ("manifest-big-number", [1, .. Encoding.UTF8.GetBytes(manifest.Replace("4096", "99999999999999999999", StringComparison.Ordinal))]);
        yield return ("manifest-empty", [1]);
    }
}
