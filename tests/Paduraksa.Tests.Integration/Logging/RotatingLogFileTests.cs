using System.Diagnostics;

using System.IO.Compression;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using Paduraksa.Core.Logging;
using Paduraksa.Server.Logging;

namespace Paduraksa.Tests.Integration.Logging;

public sealed class RotatingLogFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    private string Directory => Path.Combine(_root, "logs");

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void LinesAreAppendedInOrderAndFlushed()
    {
        using var writer = Start(Clock());

        writer.WriteLine("first");
        writer.WriteLine("second".AsSpan());
        writer.WriteLine("third");
        writer.Flush();

        Assert.Equal(["first", "second", "third"], Read(LivePath()));
    }

    [Fact]
    public void DisposeFlushesWhatIsStillQueued()
    {
        var writer = Start(Clock());
        writer.WriteLine("last words");
        writer.Dispose();

        Assert.Equal(["last words"], Read(LivePath()));
    }

    [Fact]
    public void AFlushWakesTheWriterInsteadOfWaitingForItsTick()
    {
        using var writer = Start(Clock());
        writer.WriteLine("warm");
        writer.Flush(); // the writer is now idle, waiting for work: what a host's stop finds

        // The writer's idle wait is one RotatingLogFile.FlushInterval (a second). A flush that only set the request and waited
        // for that wait to run out would take about half a second each time, so five of them would take about two and a half
        // seconds, with one over a second most runs. Five flushes that wake the writer take a few milliseconds.
        const int Flushes = 5;
        long longest = 0;
        long total = 0;
        for (var i = 0; i < Flushes; i++)
        {
            var started = Stopwatch.GetTimestamp();
            writer.Flush();
            var elapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            longest = Math.Max(longest, elapsed);
            total += elapsed;
        }

        Assert.True(total < 1000, $"five flushes took {total} ms in all (longest {longest} ms)");
    }

    [Fact]
    public void LinesWrittenBeforeAFlushAreOnDiskWhenItReturns()
    {
        using var writer = Start(Clock());
        writer.WriteLine("on disk when the flush returns");

        writer.Flush();

        // Read through a handle of its own while the writer still holds the file open: the flush put the bytes on disk; it did
        // not leave them in the writer's buffer for the next tick.
        using FileStream file = new(LivePath(), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using StreamReader reader = new(file, Encoding.UTF8);
        Assert.Equal("on disk when the flush returns", reader.ReadLine());
    }

    [Fact]
    public async Task ConcurrentWritersKeepEachWritersOrder()
    {
        using var writer = Start(Clock());
        const int Threads = 4;
        const int Lines = 50;
        using Barrier barrier = new(Threads);

        Task[] writing = [.. Enumerable.Range(0, Threads).Select(thread => Task.Run(() =>
        {
            barrier.SignalAndWait();
            for (var line = 0; line < Lines; line++)
            {
                writer.WriteLine($"thread {thread} line {line}");
            }
        }))];
        await Task.WhenAll(writing).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        writer.Flush();

        var written = Read(LivePath());
        Assert.Equal(Threads * Lines, written.Count);
        for (var thread = 0; thread < Threads; thread++)
        {
            int[] sequence = [.. written.Where(l => l.StartsWith($"thread {thread} ", StringComparison.Ordinal)).Select(l => int.Parse(l.Split(' ')[3], System.Globalization.CultureInfo.InvariantCulture))];
            Assert.Equal(Enumerable.Range(0, Lines), sequence);
        }
    }

    [Fact]
    public void ItRotatesBySizeAndCompressesInTheBackgroundWithoutLosingALine()
    {
        using var writer = Start(Clock(), maxFileSize: 80);
        string[] lines = [.. Enumerable.Range(0, 24).Select(i => $"warn: a line of log number {i:D2}")];

        foreach (var line in lines)
        {
            writer.WriteLine(line);
        }

        writer.Flush();
        WaitUntil(() => Rotated().Count >= 2 && Rotated().All(path => path.EndsWith(".gz", StringComparison.Ordinal)), "rotation and compression");

        List<string> found = [.. Read(LivePath()), .. Rotated().Order(StringComparer.Ordinal).SelectMany(ReadGzip)];
        Assert.Equal(lines.Order(StringComparer.Ordinal), found.Order(StringComparer.Ordinal));
        foreach (var file in Rotated())
        {
            Assert.InRange(ReadGzip(file).Sum(line => line.Length + 1), 1, 80);
        }
    }

    [Fact]
    public void ItRotatesWhenTheUtcDateChanges()
    {
        var clock = Clock();
        using var writer = Start(clock);

        writer.WriteLine("before midnight");
        writer.Flush();
        clock.Now = clock.Now.AddDays(1);
        var afterMidnight = clock.Now;
        writer.WriteLine("after midnight");
        writer.Flush();

        WaitUntil(() => Rotated().Any(path => path.EndsWith(".gz", StringComparison.Ordinal)), "the daily rotation");
        var dated = LogFileNames.Rotated(LogFileKind.Error, afterMidnight) + ".gz";

        Assert.Contains(dated, Rotated().Select(Path.GetFileName));
        Assert.Equal(["before midnight"], ReadGzip(Path.Combine(Directory, dated)));
        Assert.Equal(["after midnight"], Read(LivePath()));
    }

    [Fact]
    public void TwoRotationsInTheSameSecondGetDistinctNames()
    {
        using var writer = Start(Clock(), maxFileSize: 16);

        writer.WriteLine("a line long enough to rotate");
        writer.WriteLine("another line, same second");
        writer.WriteLine("and a third one");
        writer.Flush();
        WaitUntil(() => Rotated().Count(path => path.EndsWith(".gz", StringComparison.Ordinal)) >= 2, "two rotations");
        WaitUntil(() => Rotated().Count >= 2, "both files");

        string[] names = [.. Rotated().Select(Path.GetFileName).Order(StringComparer.Ordinal)!];
        Assert.Contains(LogFileNames.Rotated(LogFileKind.Error, Clock().Now) + ".gz", names);
        Assert.Contains(LogFileNames.Rotated(LogFileKind.Error, Clock().Now, 2) + ".gz", names);
    }

    [Fact]
    public void StartupFinishesLeftoverRotatedFilesAndRemovesStaleTemps()
    {
        System.IO.Directory.CreateDirectory(Directory);
        var leftover = LogFileNames.Rotated(LogFileKind.Error, Clock().Now.AddHours(-1));
        System.IO.File.WriteAllText(Path.Combine(Directory, leftover), "warn: from a previous run\n");
        System.IO.File.WriteAllText(Path.Combine(Directory, leftover + ".gz.tmp"), "half a gzip");
        System.IO.File.WriteAllText(Path.Combine(Directory, "error-20260101T000000Z.log.gz.tmp"), "half a gzip");

        using var writer = Start(Clock());
        WaitUntil(() => System.IO.File.Exists(Path.Combine(Directory, leftover + ".gz")) && !System.IO.File.Exists(Path.Combine(Directory, leftover)), "the leftover to be compressed");

        Assert.Equal(["warn: from a previous run"], ReadGzip(Path.Combine(Directory, leftover + ".gz")));
        Assert.Empty(System.IO.Directory.EnumerateFiles(Directory, "*.tmp"));
    }

    [Fact]
    public void RetentionKeepsTheNewestFilesUpToTheCount()
    {
        SeedRotated(5);
        using var writer = Start(Clock(), retainedFiles: 2, maxAge: TimeSpan.FromDays(3650));

        WaitUntil(() => Rotated().Count == 2, "retention by count");

        string[] kept = [.. Rotated().Select(Path.GetFileName).Order(StringComparer.Ordinal)!];
        Assert.Equal(
            [
                LogFileNames.Compressed(LogFileNames.Rotated(LogFileKind.Error, Clock().Now.AddDays(-2))),
                LogFileNames.Compressed(LogFileNames.Rotated(LogFileKind.Error, Clock().Now.AddDays(-1))),
            ],
            kept);
    }

    [Fact]
    public void RetentionDeletesFilesOlderThanTheMaximumAge()
    {
        SeedRotated(3);
        using var writer = Start(Clock(), maxAge: TimeSpan.FromDays(2));

        WaitUntil(() => Rotated().Count == 2, "retention by age");
        Assert.All(
            Rotated().Select(Path.GetFileName),
            name => Assert.True(LogFileNames.TryParseRotated(name, out _, out var at, out _) && at >= Clock().Now.AddDays(-2), name));
    }

    [Fact]
    public void RetentionKeepsTheTotalSizeUnderItsLimit()
    {
        SeedRotated(4);
        using var writer = Start(Clock(), maxTotalSize: 10);

        // The sweep deletes the oldest files one at a time, so a count that dropped doesn't mean the sweep is done.
        WaitUntil(() => RotatedTotalSize() <= 10, "retention by total size");

        Assert.True(Rotated().Count < 4);
    }

    [Fact]
    public void AnIoFailureIsCountedAndTheNextAttemptSucceeds()
    {
        // The logs path is a file, so every open fails. Nothing is thrown at the caller.
        System.IO.Directory.CreateDirectory(_root);
        System.IO.File.WriteAllText(Directory, "not a directory");

        using var writer = Start(Clock());
        writer.WriteLine("a line that can't be written");
        writer.WriteLine("another one");
        writer.Flush();
        WaitUntil(() => writer.Failures > 0, "the failure to be counted");

        // Put the directory right: the next line opens the live file, and the line after that is on disk.
        System.IO.File.Delete(Directory);
        System.IO.Directory.CreateDirectory(Directory);
        writer.WriteLine("written once the disk is back");
        writer.Flush();

        Assert.Equal(["written once the disk is back"], Read(LivePath()));
    }

    [Fact]
    public void AFullQueueDropsLinesAndCountsThemRatherThanBlocking()
    {
        // Given before the threads start: the writer signals that it has taken the first line, then holds before writing it, so the queue's
        // capacity fills deterministically instead of depending on the scheduler.
        using ManualResetEventSlim pause = new(false);
        using ManualResetEventSlim taken = new(false);
        using var writer = Start(Clock(), seams: new RotatingLogFileTestSeams { Pause = pause, Taken = taken });

        writer.WriteLine("stalled");
        Assert.True(taken.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        for (var i = 0; i < 10_000; i++)
        {
            writer.WriteLine($"dropped or queued {i}");
        }

        Assert.True(writer.DroppedLines > 0);
        Assert.Empty(Read(LivePath()));
        pause.Set();
    }

    [Fact]
    public void AWriteFailureClosesTheFileSoTheNextLineReopensIt()
    {
        if (!OperatingSystem.IsLinux() || !System.IO.File.Exists("/dev/full"))
        {
            // Writing to /dev/full fails with a full disk's error, which is the only way to make one write fail and the next succeed.
            return;
        }

        System.IO.Directory.CreateDirectory(Directory);
        var live = LivePath();
        System.IO.File.CreateSymbolicLink(live, "/dev/full");

        using var writer = Start(Clock());
        writer.WriteLine("a line a full disk refuses");
        WaitUntil(() => writer.Failures > 0, "the write failure to be counted");

        // The failed write dropped the handle: point the name at a real file, and the next line that gets through opens that. The writer may
        // still hold the /dev/full handle for one more attempt, so the write is retried until a line lands (and stops at the first).
        System.IO.File.Delete(live);
        System.IO.File.WriteAllText(live, string.Empty);
        WaitUntil(
            () =>
            {
                writer.WriteLine("written once there is space");
                writer.Flush();
                return Read(live).Count > 0;
            },
            "the reopened file to take a line");

        Assert.Equal(["written once there is space"], Read(live));
    }

    [Fact]
    public void ARotationWhoseRenameFailsKeepsAppendingAndTriesAgain()
    {
        // A directory where the first rotated name would go: File.Move onto it fails, so the rotation fails and the live file stays live.
        var clock = Clock();
        System.IO.Directory.CreateDirectory(Directory);
        var blocked = Path.Combine(Directory, LogFileNames.Rotated(LogFileKind.Error, clock.Now));
        System.IO.Directory.CreateDirectory(blocked);

        using var writer = Start(clock, maxFileSize: 24);
        writer.WriteLine("a line long enough to rotate");
        writer.WriteLine("a second line, still live");
        writer.Flush();
        WaitUntil(() => writer.Failures > 0, "the failed rename to be counted");
        Assert.Equal(["a line long enough to rotate", "a second line, still live"], Read(LivePath()));

        // Free the name: the next line rotates the file as it should, and the compression thread gzips it.
        System.IO.Directory.Delete(blocked);
        writer.WriteLine("a third line, after the retry");
        writer.Flush();
        WaitUntil(() => Rotated().Any(file => file.EndsWith(".gz", StringComparison.Ordinal)), "the rotation to succeed after the retry");
        Assert.Equal(["a third line, after the retry"], Read(LivePath()));
        List<string> compressed = [.. Rotated().Where(file => file.EndsWith(".gz", StringComparison.Ordinal)).SelectMany(ReadGzip)];
        Assert.Contains("a line long enough to rotate", compressed);
        Assert.Contains("a second line, still live", compressed);
    }

    [Fact]
    public void RetentionNeverDeletesTheLiveFile()
    {
        System.IO.Directory.CreateDirectory(Directory);
        System.IO.File.WriteAllText(LivePath(), string.Concat(Enumerable.Range(0, 200).Select(i => $"warn: line {i}\n")));
        SeedRotated(3);

        // A total budget smaller than the live file alone: the rotated files go, the live one stays.
        using var writer = Start(Clock(), maxTotalSize: 10, maxAge: TimeSpan.FromDays(3650));
        WaitUntil(() => Rotated().Count == 0, "retention to empty the rotated files");

        Assert.True(System.IO.File.Exists(LivePath()));
        Assert.Equal(200, Read(LivePath()).Count);
    }

    [Fact]
    public async Task FlushingWhileDisposingNeverThrows()
    {
        // The events and queues are not disposed while the threads may still touch them, and a Flush that loses the race returns.
        var writer = Start(Clock());
        writer.WriteLine("one last line");
        Task[] flushing = [.. Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                writer.Flush();
            }
        }))];

        writer.Dispose();
        await Task.WhenAll(flushing).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        writer.Dispose();
        writer.Flush();
    }

    [Fact]
    public void ItRefusesToWriteAfterDisposeWithoutThrowing()
    {
        var writer = Start(Clock());
        writer.Dispose();

        writer.WriteLine("too late");
        writer.Flush();

        Assert.False(System.IO.File.Exists(LivePath()));
    }

    private static TestClock Clock() => new(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero));

    private RotatingLogFile Start(
        TestClock clock,
        long maxFileSize = 1024 * 1024,
        int retainedFiles = 100,
        TimeSpan? maxAge = null,
        long maxTotalSize = 1024L * 1024 * 1024,
        RotatingLogFileTestSeams? seams = null) =>
        new(
            new LogFileWriterSettings(LogFileKind.Error, Directory, maxFileSize, retainedFiles, maxAge ?? TimeSpan.FromDays(1), maxTotalSize),
            clock,
            NullLogger.Instance,
            seams);

    private string LivePath() => Path.Combine(Directory, LogFileNames.Live(LogFileKind.Error));

    /// <summary>The rotated files now in the directory (both the uncompressed ones and the gzips).</summary>
    private List<string> Rotated() =>
        System.IO.Directory.Exists(Directory)
            ? [.. System.IO.Directory.EnumerateFiles(Directory).Where(path => LogFileNames.TryParseRotated(Path.GetFileName(path), out _, out _, out _))]
            : [];

    private long RotatedTotalSize() =>
        Rotated().Sum(static path =>
        {
            // The sweep may delete a file between the listing and this read.
            var file = new FileInfo(path);
            return file.Exists ? file.Length : 0;
        });

    /// <summary>Five rotated, compressed files from five days ago and one day ago, then a gap for the retention tests.</summary>
    private void SeedRotated(int count)
    {
        System.IO.Directory.CreateDirectory(Directory);
        for (var day = 1; day <= count; day++)
        {
            var at = Clock().Now.AddDays(-day);
            var name = LogFileNames.Compressed(LogFileNames.Rotated(LogFileKind.Error, at));
            using var file = System.IO.File.Create(Path.Combine(Directory, name));
            using GZipStream gzip = new(file, CompressionLevel.Fastest);
            var bytes = Encoding.UTF8.GetBytes($"warn: day {day}\n");
            gzip.Write(bytes, 0, bytes.Length);
        }
    }

    private static List<string> Read(string path) =>
        System.IO.File.Exists(path) ? [.. System.IO.File.ReadAllLines(path)] : [];

    private static List<string> ReadGzip(string path)
    {
        using var file = System.IO.File.OpenRead(path);
        using GZipStream gzip = new(file, CompressionMode.Decompress);
        using StreamReader reader = new(gzip, Encoding.UTF8);
        List<string> lines = [];
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }

    /// <summary>Waits for background work (a flush interval, a gzip, retention) up to a generous bound; the assertion names what it waited for.</summary>
    internal static void WaitUntil(Func<bool> condition, string what, int seconds = 20)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(seconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            Thread.Sleep(25);
        }

        Assert.Fail($"Timed out waiting for {what}.");
    }

    /// <summary>A clock the test moves by hand; a rotation's name and the daily check read it.</summary>
    internal sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
