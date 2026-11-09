using System.IO.Compression;
using System.Text;

using Paduraksa.Contracts.Logging;
using Paduraksa.Core.Logging;

namespace Paduraksa.Application.Logging;

/// <summary>
/// The read side of the log files (PLAN §7.4, §12): what the dashboard browses and the API serves. It knows the writer's names (a live
/// <c>access.log</c>/<c>error.log</c> and the timestamped, optionally gzipped rotated ones) and nothing else in the directory, so a name
/// can only ever reach a log file.
/// </summary>
public interface ILogFileStore
{
    /// <summary>The directory the files live in.</summary>
    string Directory { get; }

    /// <summary>
    /// Every log file there, newest first within each log. Empty when the directory doesn't exist yet (a fresh install has none until
    /// the first line is written).
    /// </summary>
    /// <exception cref="IOException">The directory exists but cannot be read; a caller shows its own failure rather than "no files".</exception>
    /// <exception cref="UnauthorizedAccessException">The directory exists but cannot be read.</exception>
    IReadOnlyList<LogFileSummary> List();

    /// <summary>
    /// The last <paramref name="maxLines"/> lines of <paramref name="name"/> (capped at <see cref="LogFileStore.MaxTailLines"/>), optionally
    /// only those containing <paramref name="filter"/> (ordinal, case-insensitive). Null when no such log file is served.
    /// </summary>
    LogFileTail? ReadTail(string name, int maxLines, string? filter = null);

    /// <summary>
    /// Opens <paramref name="name"/> for download (read-shared, so the writer can keep appending), or null when no such log file is served.
    /// The caller disposes the stream.
    /// </summary>
    Stream? OpenRead(string name, out long length);
}

/// <summary>
/// <see cref="ILogFileStore"/> over one directory. Every read is bounded and share-safe: the dashboard never loads a whole file, and
/// the writer is never blocked. A file that is a symbolic link, or whose name is not one the writer produces, is not served at all.
/// <para>
/// A tail reads at most the last <see cref="TailWindowBytes"/> bytes of a plain file, or decompresses at most
/// <see cref="MaxReadCharacters"/> characters of a compressed one (a gzip stream has to be read from its start), keeps at most
/// <see cref="MaxTailLines"/> lines and cuts each line at <see cref="MaxLineLength"/> characters while it is being read, so no line -
/// however long, corrupt or compressed - is ever decoded into memory whole.
/// </para>
/// </summary>
public sealed class LogFileStore : ILogFileStore
{
    /// <summary>The most of a plain file that is read for a tail: its last 1 MiB (1 048 576 bytes).</summary>
    public const int TailWindowBytes = 1024 * 1024;

    /// <summary>The most lines a tail returns, whatever the caller asks for.</summary>
    public const int MaxTailLines = 1000;

    /// <summary>The lines a tail returns when the caller asks for none in particular.</summary>
    public const int DefaultTailLines = 200;

    /// <summary>
    /// The most a compressed file is decompressed for a tail, in characters (8 Mi): gzip has to be read from its start, so the read is
    /// bounded by what it is willing to decompress rather than by where the file's end is.
    /// </summary>
    public const int MaxReadCharacters = 8 * 1024 * 1024;

    /// <summary>The longest line a tail returns, in characters; longer lines are cut and counted as truncated.</summary>
    public const int MaxLineLength = 4096;

    private const int ReadBufferSize = 16 * 1024;

    private readonly string _directory;

    public LogFileStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
    }

    public string Directory => _directory;

    public IReadOnlyList<LogFileSummary> List()
    {
        List<LogFileSummary> files = [];
        string[] paths;
        try
        {
            // GetFiles, not EnumerateFiles: an unreadable directory must throw here (the page says so, the API answers 500), not in the
            // middle of the loop. A directory that isn't there yet is the one case that is simply empty.
            paths = System.IO.Directory.GetFiles(_directory);
        }
        catch (DirectoryNotFoundException)
        {
            return files;
        }

        foreach (string path in paths)
        {
            string name = System.IO.Path.GetFileName(path);
            if (!LogFileNames.IsLogFile(name) || IsLinked(path))
            {
                continue;
            }

            try
            {
                FileInfo file = new(path);
                files.Add(new LogFileSummary(name, LogOf(name), file.Length, file.LastWriteTimeUtc, IsCompressed(name)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file that vanished or can't be stat'ed between the listing and the read is simply not listed.
            }
        }

        files.Sort(Compare);
        return files;
    }

    public LogFileTail? ReadTail(string name, int maxLines, string? filter = null)
    {
        if (!TryResolve(name, out string path, out FileInfo? info))
        {
            return null;
        }

        int wanted = Math.Clamp(maxLines <= 0 ? DefaultTailLines : maxLines, 1, MaxTailLines);
        string? needle = string.IsNullOrWhiteSpace(filter) ? null : filter.Trim();
        try
        {
            using FileStream? stream = TryOpen(path);
            if (stream is null)
            {
                return null;
            }

            (List<string> lines, bool truncated) = IsCompressed(name)
                ? ReadCompressedTail(stream, wanted, needle)
                : ReadPlainTail(stream, wanted, needle);
            return new LogFileTail(name, LogOf(name), lines, truncated, info.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // A file that went away, can't be read, or isn't a valid gzip stream: the page says it couldn't be read.
            return null;
        }
    }

    public Stream? OpenRead(string name, out long length)
    {
        length = 0;
        if (!TryResolve(name, out string path, out _))
        {
            return null;
        }

        try
        {
            // The path is checked again now that the handle is open: a link could have been put in place since the first look.
            FileStream? stream = TryOpen(path);
            if (stream is null)
            {
                return null;
            }

            length = stream.Length;
            return stream;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The canonical path of one served file: inside the logs directory, a regular file, and not a symbolic link.</summary>
    private bool TryResolve(string? name, out string path, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out FileInfo? info)
    {
        path = string.Empty;
        info = null;
        if (!LogFileNames.IsLogFile(name) || name!.Contains('/') || name.Contains('\\'))
        {
            return false;
        }

        path = System.IO.Path.Combine(_directory, name);
        string full = System.IO.Path.GetFullPath(path);
        if (!string.Equals(System.IO.Path.GetDirectoryName(full), _directory, StringComparison.Ordinal) || IsLinked(full))
        {
            return false;
        }

        FileInfo candidate = new(full);
        if (!candidate.Exists)
        {
            return false;
        }

        info = candidate;
        return true;
    }

    /// <summary>
    /// Opens one served file read-shared, or null when it can't be opened. The path is checked for a link again after the open, so the
    /// second look covers a link that was put in place between the check and the open (the open itself would have followed it).
    /// </summary>
    private static FileStream? TryOpen(string path)
    {
        FileStream? stream = null;
        try
        {
            stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                // The writer keeps appending, and a rotation may rename the file while the read runs.
                Share = FileShare.ReadWrite | FileShare.Delete,
                BufferSize = 32 * 1024,
                Options = FileOptions.SequentialScan,
            });
            if (!IsLinked(path))
            {
                return stream;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        stream?.Dispose();
        return null;
    }

    /// <summary>
    /// Whether <paramref name="path"/> is a symbolic link (or another reparse point). Deleting a link deletes the link, but opening one
    /// reads its target, which may be anywhere: the dashboard serves neither.
    /// </summary>
    private static bool IsLinked(string path)
    {
        try
        {
            FileInfo file = new(path);
            return file.LinkTarget is not null || file.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>The last lines of a plain file: its last <see cref="TailWindowBytes"/> bytes, never the whole file.</summary>
    private static (List<string> Lines, bool Truncated) ReadPlainTail(FileStream stream, int wanted, string? needle)
    {
        long start = Math.Max(0, stream.Length - TailWindowBytes);
        bool truncated = start > 0;
        stream.Position = start;
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: ReadBufferSize, leaveOpen: true);
        if (truncated)
        {
            // The window starts in the middle of a line; that part of it is not a line. Skipped without decoding it.
            SkipPartialLine(reader);
        }

        (List<string> lines, bool more) = KeepTail(reader, wanted, needle);
        return (lines, truncated || more);
    }

    /// <summary>The last lines of a gzipped file: decompressed from its start, bounded by <see cref="MaxReadCharacters"/>.</summary>
    private static (List<string> Lines, bool Truncated) ReadCompressedTail(FileStream stream, int wanted, string? needle)
    {
        using GZipStream gzip = new(stream, CompressionMode.Decompress);
        using StreamReader reader = new(gzip, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: ReadBufferSize, leaveOpen: true);
        return KeepTail(reader, wanted, needle);
    }

    /// <summary>Reads to the end of the current line without keeping it (the start of a tail window, which is the middle of a line).</summary>
    private static void SkipPartialLine(TextReader reader)
    {
        char[] buffer = new char[ReadBufferSize];
        while (true)
        {
            int read = reader.Read(buffer, 0, buffer.Length);
            if (read == 0 || Array.IndexOf(buffer, '\n', 0, read) >= 0)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Reads in bounded chunks, keeping at most <paramref name="wanted"/> of the last lines that match <paramref name="needle"/>. The line
    /// being built is cut at <see cref="MaxLineLength"/> characters as it fills, and at most <see cref="MaxReadCharacters"/> characters are
    /// read in all, so a single huge line (a corrupt file, a gzip bomb) is never decoded whole. The flag is set when the read stopped at
    /// the cap or more matching lines existed than were kept.
    /// </summary>
    private static (List<string> Lines, bool Truncated) KeepTail(TextReader reader, int wanted, string? needle)
    {
        Queue<string> kept = new(wanted);
        bool truncated = false;
        long budget = MaxReadCharacters;
        char[] buffer = new char[ReadBufferSize];
        StringBuilder line = new(MaxLineLength);
        bool cut = false;
        bool started = false;
        while (budget > 0)
        {
            int read = reader.Read(buffer, 0, (int)Math.Min(buffer.Length, budget));
            if (read == 0)
            {
                break;
            }

            budget -= read;
            int start = 0;
            for (int i = 0; i < read; i++)
            {
                if (buffer[i] != '\n')
                {
                    continue;
                }

                Append(line, buffer, start, i, ref cut, ref started);
                Keep(line, cut, started: true, kept, wanted, needle, ref truncated);
                line.Clear();
                cut = false;
                started = false;
                start = i + 1;
            }

            Append(line, buffer, start, read, ref cut, ref started);
        }

        if (budget == 0 && reader.Peek() >= 0)
        {
            truncated = true;
        }

        // A last line without its newline: kept, cut or not (the file may simply not end with one).
        Keep(line, cut, started, kept, wanted, needle, ref truncated);
        return ([.. kept], truncated);
    }

    /// <summary>Appends one chunk of a line, holding at most <see cref="MaxLineLength"/> characters and remembering that more followed.</summary>
    private static void Append(StringBuilder line, char[] buffer, int start, int end, ref bool cut, ref bool started)
    {
        for (int i = start; i < end; i++)
        {
            started = true;
            if (line.Length < MaxLineLength)
            {
                line.Append(buffer[i]);
            }
            else
            {
                cut = true;
            }
        }
    }

    /// <summary>Keeps a finished line when it is wanted, with a <c>\r</c> of a CRLF dropped and the cut marked with an ellipsis.</summary>
    private static void Keep(StringBuilder line, bool cut, bool started, Queue<string> kept, int wanted, string? needle, ref bool truncated)
    {
        if (!started)
        {
            return;
        }

        if (!cut && line.Length > 0 && line[^1] == '\r')
        {
            line.Length--;
        }

        string text = cut ? string.Concat(line.ToString(), "...") : line.ToString();
        if (needle is not null && !text.Contains(needle, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (kept.Count == wanted)
        {
            kept.Dequeue();
            truncated = true;
        }

        kept.Enqueue(text);
    }

    private static string LogOf(string name)
    {
        LogFileKind? kind = LogFileNames.KindOfLive(name);
        if (kind is null && LogFileNames.TryParseRotated(name, out LogFileKind rotated, out _, out _))
        {
            kind = rotated;
        }

        return LogFileNames.Stem(kind ?? LogFileKind.Error);
    }

    private static bool IsCompressed(string name) => name.EndsWith(".gz", StringComparison.Ordinal);

    /// <summary>
    /// The list's order: by log, the live file first, then the rotated ones newest first by the timestamp in their name (not their write
    /// time, which a copy or a restore changes) and, within one second, the highest rotation sequence first. The name breaks any last tie,
    /// so the order never depends on the file system's enumeration.
    /// </summary>
    private static int Compare(LogFileSummary left, LogFileSummary right)
    {
        int byLog = string.CompareOrdinal(left.Log, right.Log);
        if (byLog != 0)
        {
            return byLog;
        }

        bool leftLive = LogFileNames.KindOfLive(left.Name) is not null;
        bool rightLive = LogFileNames.KindOfLive(right.Name) is not null;
        if (leftLive != rightLive)
        {
            return leftLive ? -1 : 1;
        }

        (DateTimeOffset At, int Sequence) leftKey = SortKey(left);
        (DateTimeOffset At, int Sequence) rightKey = SortKey(right);
        int byTime = rightKey.At.CompareTo(leftKey.At);
        if (byTime != 0)
        {
            return byTime;
        }

        int bySequence = rightKey.Sequence.CompareTo(leftKey.Sequence);
        return bySequence != 0 ? bySequence : string.CompareOrdinal(left.Name, right.Name);
    }

    /// <summary>A rotated file's own timestamp and rotation sequence, else its write time.</summary>
    private static (DateTimeOffset At, int Sequence) SortKey(LogFileSummary file) =>
        LogFileNames.TryParseRotated(file.Name, out _, out DateTimeOffset rotatedAt, out int sequence, out _)
            ? (rotatedAt, sequence)
            : (file.LastModified, 0);
}
