using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using Paduraksa.Core.Logging;

namespace Paduraksa.Server.Logging;

/// <summary>How one log file is written: where it lives, when it rotates and how much of it is kept.</summary>
/// <param name="Kind">The log it writes.</param>
/// <param name="Directory">Where the files live.</param>
/// <param name="MaxFileSizeBytes">Rotate when the next line would push the live file past this.</param>
/// <param name="RetainedFiles">Keep at most this many rotated files (compressed ones included), the newest.</param>
/// <param name="MaxAge">The age past which a rotated file goes.</param>
/// <param name="MaxTotalSizeBytes">Delete the oldest rotated files until their total is at most this.</param>
internal sealed record LogFileWriterSettings(
    LogFileKind Kind,
    string Directory,
    long MaxFileSizeBytes,
    int RetainedFiles,
    TimeSpan MaxAge,
    long MaxTotalSizeBytes);

/// <summary>
/// Test seams, handed to the writer before its threads start (never set in production): <see cref="Pause"/> holds the writer before it
/// writes each line it has taken, and <see cref="Taken"/> is signalled once a line has been taken out of the queue, so a test can fill the
/// queue and see the drop counter without depending on the scheduler.
/// </summary>
internal sealed class RotatingLogFileTestSeams
{
    public ManualResetEventSlim? Pause { get; init; }

    public ManualResetEventSlim? Taken { get; init; }
}

/// <summary>
/// One log file, written from a background thread: the call site formats a line and drops it in a bounded queue, and this
/// writer appends it to a buffered <see cref="FileStream"/>, flushes once a second, rotates by size and by UTC date, gzips
/// rotated files in the background and trims the directory to the retention limits.
/// <para>
/// Nothing on the writing side waits for the disk. A full queue drops the line and counts it; an I/O failure is counted, the
/// file is closed (so the next line opens a fresh handle) and the problem is reported to the console at most once a minute,
/// never through the error log, which is this writer's own output. Lines leave the queue in the order they were offered.
/// </para>
/// </summary>
internal sealed class RotatingLogFile : IDisposable
{
    /// <summary>How long unflushed lines may sit in the buffer.</summary>
    internal static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The one item in the queue that is not a line: <see cref="Flush"/> offers it so the writer's wait for work ends at once
    /// instead of at the next <see cref="FlushInterval"/>. It is recognised by identity, so no line can be taken for it, and it is
    /// never written. A full queue refuses it, and the writer then finds <see cref="_flushRequested"/> within the interval.
    /// </summary>
    private static readonly string FlushToken = new('\0', 1);

    /// <summary>How long a caller's <see cref="Flush"/> waits for the writer to reach the disk.</summary>
    private static readonly TimeSpan FlushWait = TimeSpan.FromSeconds(5);

    /// <summary>How long shutdown waits for the compression thread to finish the file it is on.</summary>
    private static readonly TimeSpan CompressionShutdownWait = TimeSpan.FromSeconds(5);

    /// <summary>How often a repeated I/O problem is reported to the console.</summary>
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMinutes(1);

    /// <summary>A line longer than this is cut: a log line is a line, not a document.</summary>
    private const int MaxLineLength = 64 * 1024;

    private const int QueueCapacity = 8192;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly LogFileWriterSettings _settings;
    private readonly TimeProvider _time;
    private readonly ILogger _report;
    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>(), QueueCapacity);
    private readonly BlockingCollection<string> _compressions = new(new ConcurrentQueue<string>());
    private readonly ManualResetEventSlim _flushRequested = new(false);
    private readonly ManualResetEventSlim _flushDone = new(true);
    private readonly Thread _writerThread;
    private readonly Thread _compressionThread;
    private readonly string _livePath;

    private readonly Lock _reportLock = new();

    private readonly ManualResetEventSlim? _pause;
    private readonly ManualResetEventSlim? _taken;

    private long _dropped;
    private long _failures;
    private DateTimeOffset _lastReport = DateTimeOffset.MinValue;

    // The consumer thread's own state; never read or written by a caller.
    private FileStream? _stream;
    private long _length;
    private DateTime _openDate;

    private int _disposed;

    public RotatingLogFile(LogFileWriterSettings settings, TimeProvider time, ILogger report, RotatingLogFileTestSeams? testSeams = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(report);
        _settings = settings;
        _time = time;
        _report = report;
        _pause = testSeams?.Pause;
        _taken = testSeams?.Taken;
        _livePath = Path.Combine(settings.Directory, LogFileNames.Live(settings.Kind));

        // Two background threads: writes and rotation on one (so a line never waits for a gzip), compression and retention on the
        // other. Both are abandoned at shutdown if they overrun their wait; the process is going away either way.
        _writerThread = new Thread(Run) { IsBackground = true, Name = $"paduraksa-{LogFileNames.Stem(settings.Kind)}-log" };
        _compressionThread = new Thread(RunCompression) { IsBackground = true, Name = $"paduraksa-{LogFileNames.Stem(settings.Kind)}-gzip" };
        _writerThread.Start();
        _compressionThread.Start();
    }

    public string LivePath => _livePath;

    /// <summary>The clock the writer stamps its lines and rotations with.</summary>
    public TimeProvider Time => _time;

    /// <summary>Lines dropped because the queue was full.</summary>
    public long DroppedLines => Interlocked.Read(ref _dropped);

    /// <summary>I/O failures counted (a write, a rotation, a compression); the writer retried after each.</summary>
    public long Failures => Interlocked.Read(ref _failures);

    /// <summary>
    /// Offers one line to the writer. Never blocks and never throws: a full queue drops the line and counts it.
    /// </summary>
    public void WriteLine(ReadOnlySpan<char> line)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var text = line.Length > MaxLineLength ? string.Concat(line[..MaxLineLength], "...") : line.ToString();
        if (!_queue.TryAdd(text))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>
    /// Asks the writer to write out and flush what it holds, and waits (at most <see cref="FlushWait"/>) for it to happen. For shutdown
    /// and tests; logging call sites never need it.
    /// </summary>
    /// <remarks>
    /// The request wakes the writer at once: it offers <see cref="FlushToken"/> to the queue, an item that is not a line, so the
    /// writer's wait for work ends there instead of at the next <see cref="FlushInterval"/>. A full queue drops the token like any
    /// line, and the writer then still notices <see cref="_flushRequested"/> within the interval: the request is never lost, only
    /// sometimes late.
    /// </remarks>
    public void Flush()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _flushDone.Reset();
        _flushRequested.Set();
        _ = _queue.TryAdd(FlushToken);
        // Short waits, re-checking the writer's lifetime: a Flush racing with Dispose returns at once instead of waiting out the whole
        // budget for a writer that has already gone (and never touches an event that Dispose may have disposed).
        for (var waited = 0; waited < FlushWait.TotalMilliseconds; waited += 50)
        {
            if (_flushDone.Wait(50) || Volatile.Read(ref _disposed) != 0)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.CompleteAdding();
        _compressions.CompleteAdding();
        // Let a paused writer (a test seam) finish before the join.
        _pause?.Set();
        var writerStopped = _writerThread.Join(FlushWait);
        _compressionThread.Join(CompressionShutdownWait);
        if (writerStopped)
        {
            // Only once its thread has stopped: a writer stuck past the wait keeps its own stream, and the process is going anyway.
            CloseStream();
        }

        // A Flush that is waiting (or about to wait) must return: this is the last write.
        _flushRequested.Reset();
        _flushDone.Set();
        var dropped = Interlocked.Read(ref _dropped);
        if (dropped > 0)
        {
            LogFileLogs.DroppedLines(_report, LogFileNames.Live(_settings.Kind), dropped);
        }

        // The queue, the compression queue and the flush events are deliberately left undisposed: the writer and compression threads may
        // still be finishing (an abandoned thread must not throw on its next read), and there is no handle behind them to leak.
    }

    private void Run()
    {
        while (true)
        {
            if (_queue.TryTake(out var item, FlushInterval))
            {
                WriteItem(item);
                // Take whatever else is waiting before touching the disk again: a busy minute is one flush, not thousands.
                while (_queue.TryTake(out item))
                {
                    WriteItem(item);
                }

                if (_flushRequested.IsSet)
                {
                    CompleteFlush();
                }

                continue;
            }

            if (_queue.IsCompleted)
            {
                break;
            }

            FlushStream();
            if (_flushRequested.IsSet)
            {
                CompleteFlush();
            }
        }

        FlushStream();
        if (_flushRequested.IsSet)
        {
            CompleteFlush();
        }
    }

    /// <summary>Writes out everything that was queued before the request, flushes, then lets <see cref="Flush"/> return.</summary>
    private void CompleteFlush()
    {
        while (_queue.TryTake(out var pending))
        {
            WriteItem(pending);
        }

        FlushStream();
        _flushRequested.Reset();
        _flushDone.Set();
    }

    /// <summary>One item taken from the queue: a line, or the flush token, which is only a wake-up and is never written.</summary>
    private void WriteItem(string item)
    {
        if (!ReferenceEquals(item, FlushToken))
        {
            Write(item);
        }
    }

    /// <summary>
    /// Runs work that must never throw: a logger's own failure is counted and reported, never propagated, and this is the
    /// one place that policy is stated. <see cref="Compress"/> keeps its own catch because it deletes its temp file
    /// before reporting.
    /// </summary>
    /// <param name="doing">What the work does, for the report (see <see cref="Failed"/>).</param>
    /// <param name="action">The work.</param>
    /// <param name="path">The file it works on, when it isn't the live file.</param>
    /// <param name="onFailure">What to do after a failure is reported, such as dropping the handle so the next line
    /// reopens it.</param>
    private void BestEffort(string doing, Action action, string? path = null, Action? onFailure = null)
    {
        try
        {
            action();
        }
#pragma warning disable CA1031 // A log line must never take the process (or a request's background drain) down.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Failed(doing, ex, path);
            onFailure?.Invoke();
        }
    }

    private void Write(string line)
    {
        _taken?.Set();
        _pause?.Wait();
        BestEffort(
            "write to",
            () =>
            {
                var bytes = Utf8.GetBytes(line);
                EnsureStream(bytes.Length + 1);
                if (_stream is null)
                {
                    // Opening failed (reported already): the line is lost, as it would be on a full disk.
                    return;
                }

                _stream.Write(bytes);
                _stream.WriteByte((byte)'\n');
                _length += bytes.Length + 1;
            },
            onFailure: CloseStream);
    }

    /// <summary>Rotates first when this line would pass the size limit or the UTC date has changed, then opens the live file if needed.</summary>
    private void EnsureStream(int lineBytes)
    {
        var today = _time.GetUtcNow().UtcDateTime.Date;
        if (_stream is not null && (_length + lineBytes > _settings.MaxFileSizeBytes || today != _openDate))
        {
            Rotate();
        }

        if (_stream is null)
        {
            Open();
        }
    }

    private void Open()
    {
        try
        {
            EnsureDirectory();
            FileStreamOptions options = new()
            {
                Mode = FileMode.Append,
                Access = FileAccess.Write,
                Share = FileShare.Read,
                BufferSize = 64 * 1024,
            };
            if (!OperatingSystem.IsWindows())
            {
                // Logs hold client IPs: owner-only, like the data directory's other files.
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            _stream = new FileStream(_livePath, options);
            _length = _stream.Length;
            _openDate = _time.GetUtcNow().UtcDateTime.Date;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            CloseStream();
            Failed("open", ex);
        }
    }

    /// <summary>Moves the live file aside under a timestamped name and hands it to the compression thread.</summary>
    private void Rotate()
    {
        FlushStream();
        CloseStream();
        BestEffort(
            "rotate",
            () =>
            {
                EnsureDirectory();
                var now = _time.GetUtcNow();
                if (!TryMoveAside(now, out var rotated) || rotated is not { } file)
                {
                    // Keep appending to the old file: the next line reopens it, and the next interval tries again.
                    return;
                }

                _compressions.TryAdd(file);
            });
    }

    /// <summary>
    /// Renames the live file to the first free timestamped name for <paramref name="now"/>. Two rotations in one second get <c>-2</c>, <c>-3</c>, ...
    /// </summary>
    private bool TryMoveAside(DateTimeOffset now, out string? rotated)
    {
        rotated = null;
        for (var sequence = 1; sequence <= 999; sequence++)
        {
            var candidate = LogFileNames.Rotated(_settings.Kind, now, sequence);
            var path = Path.Combine(_settings.Directory, candidate);
            if (File.Exists(path) || File.Exists(path + ".gz"))
            {
                continue;
            }

            try
            {
                // Never replaces an existing file: a name that appeared between the check and here fails the move instead.
                File.Move(_livePath, path);
                rotated = path;
                return true;
            }
            catch (IOException) when (File.Exists(path) || File.Exists(path + ".gz"))
            {
                // Another rotation in this second won the name; take the next one.
            }
        }

        LogFileLogs.RotateNameExhausted(_report, _livePath);
        return false;
    }

    /// <summary>The compression thread: recovery first, then one rotated file at a time, trimming after each.</summary>
    private void RunCompression()
    {
        Recover();
        while (_compressions.TryTake(out var path, Timeout.Infinite))
        {
            Compress(path);
            Trim();
        }
    }

    /// <summary>Startup work: stale temp files go, rotated files a previous run left uncompressed are gzipped, retention runs.</summary>
    private void Recover()
    {
        BestEffort(
            "tidy the files left from the last run of",
            () =>
            {
                if (!Directory.Exists(_settings.Directory))
                {
                    // Nothing was ever logged: the directory (and the live file) appear with the first line, so a process that never logs
                    // leaves no trace in the data directory.
                    return;
                }

                foreach (var temp in Directory.EnumerateFiles(_settings.Directory, "*.log.gz.tmp"))
                {
                    TryDelete(temp);
                }

                foreach (var name in Directory.EnumerateFiles(_settings.Directory))
                {
                    var fileName = Path.GetFileName(name);
                    if (!LogFileNames.TryParseRotated(fileName, out var kind, out _, out var compressed) || kind != _settings.Kind || compressed)
                    {
                        continue;
                    }

                    Compress(name);
                }
            });

        Trim();
    }

    /// <summary>
    /// Gzips <paramref name="path"/> to <c>....log.gz</c>: written to a temp file, given its name in one rename, and only then is the
    /// original deleted. A crash leaves either the original or the complete <c>.gz</c>, never a half one.
    /// </summary>
    private void Compress(string path)
    {
        var target = LogFileNames.Compressed(Path.GetFileName(path));
        target = Path.Combine(_settings.Directory, target);
        var temp = target + ".tmp";
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            if (File.Exists(target))
            {
                // A previous run finished the .gz and stopped before deleting the original.
                TryDelete(path);
                return;
            }

            FileStreamOptions create = new()
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 64 * 1024,
            };
            if (!OperatingSystem.IsWindows())
            {
                create.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (FileStream input = new(path, new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read }))
            using (FileStream output = new(temp, create))
            using (GZipStream gzip = new(output, CompressionLevel.SmallestSize))
            {
                input.CopyTo(gzip);
            }

            File.Move(temp, target, overwrite: false);
            TryDelete(path);
        }
#pragma warning disable CA1031 // One unreadable file must not stop the thread (or the rest of the files).
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // The temp file goes before the report, which is why this catch isn't BestEffort.
            TryDelete(temp);
            Failed("compress", ex, path);
        }
    }

    /// <summary>
    /// Applies the retention limits: rotated files are deleted oldest first, until at most <see cref="LogFileWriterSettings.RetainedFiles"/>
    /// remain, none is older than <see cref="LogFileWriterSettings.MaxAge"/>, and their total is at most
    /// <see cref="LogFileWriterSettings.MaxTotalSizeBytes"/>. The live file is never a candidate.
    /// </summary>
    private void Trim()
    {
        BestEffort(
            "trim the old files of",
            () =>
            {
                if (!Directory.Exists(_settings.Directory))
                {
                    return;
                }

                List<(string Path, long Size, DateTimeOffset At)> rotated = [];
                foreach (var path in Directory.EnumerateFiles(_settings.Directory))
                {
                    var name = Path.GetFileName(path);
                    if (!LogFileNames.TryParseRotated(name, out var kind, out var at, out _) || kind != _settings.Kind)
                    {
                        continue;
                    }

                    long size;
                    try
                    {
                        size = new FileInfo(path).Length;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        continue;
                    }

                    rotated.Add((path, size, at));
                }

                rotated.Sort(static (left, right) => left.At.CompareTo(right.At));
                var cutoff = _time.GetUtcNow() - _settings.MaxAge;
                var total = rotated.Sum(static file => file.Size);
                var remaining = rotated.Count;
                foreach (var (path, size, at) in rotated)
                {
                    if (remaining <= _settings.RetainedFiles && total <= _settings.MaxTotalSizeBytes && at >= cutoff)
                    {
                        break;
                    }

                    if (TryDelete(path))
                    {
                        remaining--;
                        total -= size;
                    }
                }
            });
    }

    private void FlushStream() => BestEffort("flush", () => _stream?.Flush(), onFailure: CloseStream);

    private void CloseStream()
    {
        BestEffort("close", () => _stream?.Dispose());
        _stream = null;
        _length = 0;
    }

    /// <summary>Creates the directory owner-only (0700, like the run directory and the vault's), and enforces it when it exists.</summary>
    private void EnsureDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(_settings.Directory);
            return;
        }

        const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Directory.CreateDirectory(_settings.Directory, OwnerOnly);
        if (File.GetUnixFileMode(_settings.Directory) != OwnerOnly)
        {
            File.SetUnixFileMode(_settings.Directory, OwnerOnly);
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Counts an I/O failure and reports it to the console at most once a minute, so a full disk doesn't flood the log.</summary>
    /// <param name="doing">What failed, as a verb phrase that reads before "the log file": "open", "write to", "compress".</param>
    /// <param name="exception">The failure.</param>
    /// <param name="path">The file it failed on, when it isn't the live file (a rotated one being compressed).</param>
    private void Failed(string doing, Exception exception, string? path = null)
    {
        Interlocked.Increment(ref _failures);
        lock (_reportLock)
        {
            var now = _time.GetUtcNow();
            if (now - _lastReport < ReportInterval)
            {
                return;
            }

            _lastReport = now;
        }

        LogFileLogs.WriteFailed(_report, doing, path ?? _livePath, exception);
    }
}
