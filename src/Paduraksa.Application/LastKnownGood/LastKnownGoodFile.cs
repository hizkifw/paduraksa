using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Paduraksa.Application.Config;
using Paduraksa.Application.Revisions;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

namespace Paduraksa.Application.LastKnownGood;

/// <summary>
/// <c>&lt;data dir&gt;/last-good.json</c>: the source document of the last snapshot that went live, so the
/// proxy can still come up serving traffic when the config database can't be used at startup. Only ever a fallback:
/// the database's latest revision is the source of truth whenever it can be read and compiled.
/// </summary>
/// <remarks>
/// <para><b>Format.</b> One JSON object: <c>format</c> (<see cref="Format"/>), the revision's <c>revision</c> ID and
/// <c>seq</c>, the <c>sha256</c> (lowercase hex) of the canonical document bytes, and the <c>document</c> itself, those
/// bytes embedded verbatim, so the file stays readable and the hash covers exactly what the revision stored. Reading
/// checks all of it. The document holds references only, never secrets, so the file needs no protection
/// beyond the data directory's.</para>
/// <para><b>Writes</b> run in the background, never on the caller's path: <see cref="Offer"/> records the entry and
/// returns, one writer at a time writes the newest entry offered (older ones still waiting are skipped, and an entry
/// older than one already offered is ignored). Each write is atomic: a new temp file next to the target with a random
/// name (<c>last-good.json.&lt;random&gt;.tmp</c>, created with <c>CreateNew</c>, which never opens anything that
/// exists, and mode 0600 on Unix), flushed to disk, then renamed over the target. The rename replaces a symbolic link at
/// the target rather than following it, so a reader sees the old file or the new one, never a torn one, and nothing
/// outside the data directory is ever written. Temp files a crash left behind are removed before each write (removing
/// a symbolic link, never its target). A failed write is logged and the next offer tries again.</para>
/// <para><b>Durability.</b> The file's data is flushed before the rename, but the directory isn't: .NET can't open a
/// directory to fsync it. After a power loss right after a write, the rename may be lost, and the previous file (still
/// complete and valid) is what's found. Safe, not durable; at worst a fallback one revision old.</para>
/// <para><b>One process per data directory.</b> Only one server process may use a data directory at a time (the
/// config database tolerates another writer, this file doesn't); nothing enforces it.</para>
/// <para><b>Reads</b> refuse a target that is a symbolic link and never throw: any problem comes back as a reason.</para>
/// </remarks>
public sealed partial class LastKnownGoodFile : IAsyncDisposable
{
    /// <summary>The file name inside the data directory.</summary>
    public const string FileName = "last-good.json";

    /// <summary>The <c>format</c> this build writes and reads.</summary>
    public const string Format = "paduraksa.dev/last-good/v1";

    // The document plus a small envelope.
    private const int MaxFileLength = ConfigRevisionStore.MaxDocumentLength + 4096;

    private readonly Lock _lock = new();
    private readonly ILogger _logger;
    private LastKnownGoodEntry? _pending;
    private long _offeredSeq;
    private bool _writing;
    private bool _disposed;
    private Task _writer = Task.CompletedTask;

    /// <param name="path">The file's full path (normally <c>&lt;data dir&gt;/last-good.json</c>).</param>
    /// <param name="loggerFactory">For write failures.</param>
    public LastKnownGoodFile(string path, ILoggerFactory? loggerFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<LastKnownGoodFile>();
    }

    /// <summary>The file's full path.</summary>
    public string Path { get; }

    /// <summary>The pattern of the temp files writes are prepared in, inside <see cref="Path"/>'s directory.</summary>
    public string TempPattern => System.IO.Path.GetFileName(Path) + ".*.tmp";

    /// <summary>Something (a file, or a link, even a dangling one) is at <see cref="Path"/>.</summary>
    internal bool IsPresent
    {
        get
        {
            FileInfo file = new(Path);
            return file.Exists || file.LinkTarget is not null;
        }
    }

    /// <summary>Completes once every entry offered so far is written (or its write failed and was logged).</summary>
    public Task FlushAsync()
    {
        lock (_lock)
        {
            return _writer;
        }
    }

    /// <summary>Waits for the pending write, then ignores further offers.</summary>
    public async ValueTask DisposeAsync()
    {
        Task writer;
        lock (_lock)
        {
            _disposed = true;
            writer = _writer;
        }

        await writer.ConfigureAwait(false);
    }

    /// <summary>
    /// Queues <paramref name="entry"/> to be written in the background; returns at once. Ignored when an entry with the
    /// same or a later <c>Seq</c> was already offered, or once disposed.
    /// </summary>
    internal void Offer(LastKnownGoodEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_lock)
        {
            if (_disposed || entry.Revision.Seq <= _offeredSeq)
            {
                return;
            }

            _offeredSeq = entry.Revision.Seq;
            _pending = entry;
            if (!_writing)
            {
                _writing = true;
                _writer = Task.Run(WriteLoop);
            }
        }
    }

    /// <summary>Reads and checks the file: false, with the reason, when there is none or it can't be used.</summary>
    internal bool TryRead([NotNullWhen(true)] out LastKnownGoodEntry? entry, [NotNullWhen(false)] out string? problem)
    {
        entry = null;
        byte[] bytes;
        try
        {
            FileInfo file = new(Path);
            if (file.LinkTarget is not null)
            {
                problem = $"{Path} is a symbolic link, which isn't followed";
                return false;
            }

            if (!file.Exists)
            {
                problem = $"there is no {Path}";
                return false;
            }

            // One handle, and never more than the limit read: the file's size can change between a check and a read.
            using FileStream stream = new(Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] buffer = new byte[(int)Math.Min(stream.Length, MaxFileLength) + 1];
            int length = 0;
            int read;
            while (length < buffer.Length && (read = stream.Read(buffer, length, buffer.Length - length)) > 0)
            {
                length += read;
            }

            if (length > MaxFileLength)
            {
                problem = $"{Path} is more than the {MaxFileLength} bytes allowed";
                return false;
            }

            bytes = buffer[..length];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Logged, never written into the reason a client may read.
            LogReadFailed(_logger, ex, Path);
            problem = $"{Path} can't be read";
            return false;
        }

        problem = Parse(bytes, out entry);
        if (problem is not null)
        {
            problem = $"{Path} can't be used: {problem}";
        }

        return entry is not null;
    }

    /// <summary>The file's bytes for <paramref name="entry"/>.</summary>
    internal static byte[] Render(LastKnownGoodEntry entry)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("format", Format);
            writer.WriteString("revision", entry.Revision.Id.ToString());
            writer.WriteNumber("seq", entry.Revision.Seq);
            writer.WriteString("sha256", Convert.ToHexStringLower(entry.DocumentHash.Span));
            writer.WritePropertyName("document");
            writer.WriteRawValue(entry.Document.Span, skipInputValidation: true);
            writer.WriteEndObject();
        }

        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    /// <summary>Null and the entry, or the problem.</summary>
    private string? Parse(byte[] bytes, out LastKnownGoodEntry? entry)
    {
        entry = null;
        try
        {
            using JsonDocument json = JsonDocument.Parse(bytes);
            JsonElement root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return "it isn't a JSON object";
            }

            if (!root.TryGetProperty("format", out JsonElement format) || format.ValueKind != JsonValueKind.String || format.GetString() != Format)
            {
                return $"its format isn't '{Format}'";
            }

            if (!root.TryGetProperty("revision", out JsonElement revision) || revision.ValueKind != JsonValueKind.String
                || !ConfigRevisionId.TryParse(revision.GetString(), out ConfigRevisionId revisionId) || revisionId.IsEmpty)
            {
                return "its revision isn't a revision ID";
            }

            if (!root.TryGetProperty("seq", out JsonElement seq) || seq.ValueKind != JsonValueKind.Number || !seq.TryGetInt64(out long seqValue) || seqValue < 1)
            {
                return "its seq isn't a positive number";
            }

            if (!root.TryGetProperty("sha256", out JsonElement sha256) || sha256.ValueKind != JsonValueKind.String
                || sha256.GetString() is not { Length: SHA256.HashSizeInBytes * 2 } hex)
            {
                return "its sha256 isn't a SHA-256 in hex";
            }

            byte[] expected;
            try
            {
                expected = Convert.FromHexString(hex);
            }
            catch (FormatException)
            {
                return "its sha256 isn't a SHA-256 in hex";
            }

            if (!root.TryGetProperty("document", out JsonElement document) || document.ValueKind != JsonValueKind.Object)
            {
                return "it has no document";
            }

            byte[] documentBytes = JsonMarshal.GetRawUtf8Value(document).ToArray();
            if (!SHA256.HashData(documentBytes).AsSpan().SequenceEqual(expected))
            {
                return "its document doesn't match its sha256";
            }

            if (!CanonicalConfigJson.TryDeserialize(documentBytes, out ConfigDocument? parsed, out ConfigError? error))
            {
                return $"its document can't be read ({error.Code} at '{error.Path}': {error.Message})";
            }

            entry = new LastKnownGoodEntry(new RevisionRef(revisionId, seqValue), documentBytes, expected) { Parsed = parsed };
            return null;
        }
        catch (JsonException ex)
        {
            LogDamaged(_logger, ex, Path);
            return "it isn't valid JSON";
        }
#pragma warning disable CA1031 // A damaged file is a reason not to use it, never a reason for startup to fail.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogDamaged(_logger, ex, Path);
            return "it can't be read";
        }
    }

    private void WriteLoop()
    {
        bool done = false;
        try
        {
            while (true)
            {
                LastKnownGoodEntry next;
                lock (_lock)
                {
                    if (_pending is null)
                    {
                        // Under the same lock as the check, so an offer either finds the loop running or starts one.
                        _writing = false;
                        done = true;
                        return;
                    }

                    next = _pending;
                    _pending = null;
                }

                try
                {
                    Write(next);
                    LogWritten(next.Revision.Seq, next.Revision.Id, Path);
                }
#pragma warning disable CA1031 // Never fault the writer: FlushAsync and DisposeAsync await it, and the next offer retries.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    LogWriteFailed(ex, next.Revision.Seq, next.Revision.Id, Path);
                }
            }
        }
        finally
        {
            if (!done)
            {
                // Ended by something the catch above can't see (it catches everything): never leave offers stranded.
                lock (_lock)
                {
                    _writing = false;
                }
            }
        }
    }

    private void Write(LastKnownGoodEntry entry)
    {
        byte[] bytes = Render(entry);
        string directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);

        // Temp files a crash left behind go first. File.Delete removes a symbolic link itself, not its target, and
        // throws for a directory (the write then fails and is logged).
        foreach (string stale in Directory.EnumerateFileSystemEntries(directory, TempPattern))
        {
            File.Delete(stale);
        }

        // A new name every time, and CreateNew (O_EXCL) never opens anything that exists: the bytes only ever go into a
        // new regular file of our own.
        string temp = System.IO.Path.Combine(directory, $"{System.IO.Path.GetFileName(Path)}.{System.IO.Path.GetRandomFileName()}.tmp");
        FileStreamOptions create = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            create.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            using (FileStream stream = new(temp, create))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            // rename(2): atomic, and it replaces a symbolic link at the target instead of following it.
            File.Move(temp, Path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void TryDelete(string temp)
    {
        try
        {
            File.Delete(temp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The next write removes it first anyway.
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The last-known-good config {Path} can't be read; startup continues without it.")]
    private static partial void LogReadFailed(ILogger logger, Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The last-known-good config {Path} is damaged and can't be used; startup continues without it.")]
    private static partial void LogDamaged(ILogger logger, Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Wrote the last-known-good config (revision #{Seq}, {Revision}) to {Path}.")]
    private partial void LogWritten(long seq, ConfigRevisionId revision, string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Couldn't write the last-known-good config (revision #{Seq}, {Revision}) to {Path}; the previous file stays. The next publish tries again.")]
    private partial void LogWriteFailed(Exception exception, long seq, ConfigRevisionId revision, string path);
}

/// <summary>What <see cref="LastKnownGoodFile"/> holds: a revision that went live and its canonical document.</summary>
/// <param name="Revision">The revision.</param>
/// <param name="Document">Its canonical document bytes, exactly as the revision stores them.</param>
/// <param name="DocumentHash">SHA-256 of <paramref name="Document"/>.</param>
internal sealed record LastKnownGoodEntry(RevisionRef Revision, ReadOnlyMemory<byte> Document, ReadOnlyMemory<byte> DocumentHash)
{
    /// <summary>The document, once read back from the file.</summary>
    public ConfigDocument? Parsed { get; init; }
}
