using System.IO.Compression;
using System.Text;

using Paduraksa.Application.Logging;
using Paduraksa.Contracts.Logging;
using Paduraksa.Core.Logging;

namespace Paduraksa.Tests.Unit.Logging;

public sealed class LogFileStoreTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public LogFileStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void AMissingDirectoryListsNothingAndServesNothing()
    {
        LogFileStore store = new(Path.Combine(_directory, "not-there"));

        Assert.Empty(store.List());
        Assert.Null(store.ReadTail("error.log", 10));
        Assert.Null(store.OpenRead("error.log", out _));
    }

    [Fact]
    public void ItListsBothLogsWithTheirLiveFileFirstAndCompressedOnesMarked()
    {
        WriteLive("error.log", "warn: one\nwarn: two\n");
        var rotated = LogFileNames.Compressed(LogFileNames.Rotated(LogFileKind.Error, Noon.AddDays(-1)));
        WriteGzip(rotated, "warn: older\n");
        WriteLive("access.log", "GET / 200\n");
        File.WriteAllText(Path.Combine(_directory, "notes.txt"), "not a log");
        File.WriteAllText(Path.Combine(_directory, "error-2026.log"), "not a log either");

        var files = new LogFileStore(_directory).List();

        Assert.Equal(["access.log", "error.log", rotated], files.Select(file => file.Name));
        Assert.Equal(["access", "error", "error"], files.Select(file => file.Log));
        Assert.Equal(20, files[1].Size);
        Assert.False(files[1].Compressed);
        Assert.True(files[2].Compressed);
        Assert.True(files[2].LastModified > DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public void ALiveFileSortsBeforeItsRotatedOnesWhichAreNewestFirst()
    {
        WriteLive("error.log", "now\n");
        WriteGzip(LogFileNames.Compressed(LogFileNames.Rotated(LogFileKind.Error, Noon.AddDays(-2))), "two days ago\n");
        WriteGzip(LogFileNames.Compressed(LogFileNames.Rotated(LogFileKind.Error, Noon.AddDays(-1))), "yesterday\n");

        var files = new LogFileStore(_directory).List();

        Assert.Equal(["error.log", LogFileNames.Compressed(LogFileNames.Rotated(LogFileKind.Error, Noon.AddDays(-1))), LogFileNames.Compressed(LogFileNames.Rotated(LogFileKind.Error, Noon.AddDays(-2)))], files.Select(file => file.Name));
    }

    [Fact]
    public void RotationsWithinOneSecondAreOrderedByTheirSequence()
    {
        WriteLive("error.log", "now\n");
        WriteGzip(LogFileNames.Compressed(LogFileNames.Rotated(LogFileKind.Error, Noon)), "first\n");
        WriteGzip(LogFileNames.Compressed(LogFileNames.Rotated(LogFileKind.Error, Noon, 2)), "second\n");
        WriteGzip(LogFileNames.Compressed(LogFileNames.Rotated(LogFileKind.Error, Noon, 10)), "tenth\n");

        var files = new LogFileStore(_directory).List();

        Assert.Equal(
            [
                "error.log",
                LogFileNames.Compressed(LogFileNames.Rotated(LogFileKind.Error, Noon, 10)),
                LogFileNames.Compressed(LogFileNames.Rotated(LogFileKind.Error, Noon, 2)),
                LogFileNames.Compressed(LogFileNames.Rotated(LogFileKind.Error, Noon)),
            ],
            files.Select(file => file.Name));
    }

    [Fact]
    public void AnUnreadableDirectoryThrowsRatherThanLookingEmpty()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess)
        {
            // A privileged process reads a 0300 directory anyway, so the failure cannot be staged.
            return;
        }

        File.SetUnixFileMode(_directory, UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            LogFileStore store = new(_directory);

            Assert.ThrowsAny<Exception>(() => store.List());
        }
        finally
        {
            File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void ASingleHugeLineInAGzipIsNeverDecodedWhole()
    {
        // 64 Mi characters with no newline: a gzip bomb in one line. The reader stops at its cap and keeps one cut line, not 64 MiB.
        var name = LogFileNames.Compressed(LogFileNames.Rotated(LogFileKind.Access, Noon));
        using (var file = File.Create(Path.Combine(_directory, name)))
        using (GZipStream gzip = new(file, CompressionLevel.SmallestSize))
        {
            var chunk = Encoding.UTF8.GetBytes(new string('x', 1024 * 1024));
            for (var i = 0; i < 64; i++)
            {
                gzip.Write(chunk, 0, chunk.Length);
            }
        }

        var tail = new LogFileStore(_directory).ReadTail(name, 10)!;

        Assert.Single(tail.Lines);
        Assert.Equal(LogFileStore.MaxLineLength + 3, tail.Lines[0].Length);
        Assert.True(tail.Truncated);
    }

    [Fact]
    public void ATailIsTheLastLinesOldestFirst()
    {
        WriteLive("error.log", "one\ntwo\nthree\nfour\n");

        var tail = new LogFileStore(_directory).ReadTail("error.log", 2)!;

        Assert.Equal(["three", "four"], tail.Lines);
        Assert.True(tail.Truncated);
        Assert.Equal("error", tail.Log);
        Assert.Equal(19, tail.FileSize);
    }

    [Fact]
    public void TheWholeFileFitsWithoutBeingCalledTruncated()
    {
        WriteLive("error.log", "one\ntwo\n");

        var tail = new LogFileStore(_directory).ReadTail("error.log", 10)!;

        Assert.Equal(["one", "two"], tail.Lines);
        Assert.False(tail.Truncated);
    }

    [Fact]
    public void AFilterKeepsTheLastMatchingLinesAndDropsTheRest()
    {
        WriteLive("error.log", "warn: a\nerror: boom\nwarn: b\nERROR: BOOM AGAIN\n");

        var filtered = new LogFileStore(_directory).ReadTail("error.log", 10, "boom")!;
        var none = new LogFileStore(_directory).ReadTail("error.log", 10, "nothing like this")!;

        Assert.Equal(["error: boom", "ERROR: BOOM AGAIN"], filtered.Lines);
        Assert.Empty(none.Lines);
    }

    [Fact]
    public void ACappedTailKeepsTheEndOfTheFilteredLines()
    {
        WriteLive("error.log", string.Concat(Enumerable.Range(0, 50).Select(i => $"warn: {i}\n")));

        var tail = new LogFileStore(_directory).ReadTail("error.log", 5, "warn")!;

        Assert.Equal(["warn: 45", "warn: 46", "warn: 47", "warn: 48", "warn: 49"], tail.Lines);
        Assert.True(tail.Truncated);
    }

    [Fact]
    public void ACompressedFileIsDecompressedForItsTail()
    {
        var name = LogFileNames.Compressed(LogFileNames.Rotated(LogFileKind.Access, Noon));
        WriteGzip(name, "one\ntwo\nthree\n");

        var tail = new LogFileStore(_directory).ReadTail(name, 2)!;

        Assert.Equal("access", tail.Log);
        Assert.Equal(["two", "three"], tail.Lines);
        Assert.True(tail.Truncated);
    }

    [Fact]
    public void APlainFileIsReadFromItsEndNotFromTheStart()
    {
        // 300 bytes of window: the file is several times bigger, so the read starts in the middle and drops the partial line.
        LogFileStore store = new(_directory);
        StringBuilder text = new();
        for (var i = 0; i < 120_000; i++)
        {
            text.Append("warn: line ").Append(i.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        }

        WriteLive("error.log", text.ToString());
        var tail = store.ReadTail("error.log", 3)!;

        Assert.Equal(3, tail.Lines.Count);
        Assert.All(tail.Lines, line => Assert.StartsWith("warn: line ", line, StringComparison.Ordinal));
        Assert.True(tail.Truncated);
        Assert.True(tail.FileSize > LogFileStore.TailWindowBytes);
    }

    [Fact]
    public void AnOverlongLineIsCutForTheViewer()
    {
        WriteLive("error.log", new string('x', LogFileStore.MaxLineLength + 100) + "\n");

        var tail = new LogFileStore(_directory).ReadTail("error.log", 1)!;

        Assert.Equal(LogFileStore.MaxLineLength + 3, tail.Lines[0].Length);
        Assert.EndsWith("...", tail.Lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyFileHasNoLines()
    {
        WriteLive("error.log", string.Empty);

        var tail = new LogFileStore(_directory).ReadTail("error.log", 10)!;

        Assert.Empty(tail.Lines);
        Assert.False(tail.Truncated);
    }

    [Theory]
    [InlineData("../error.log")]
    [InlineData("../../etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData("logs/error.log")]
    [InlineData("error.log/")]
    [InlineData("notes.txt")]
    [InlineData("error-2026.log")]
    [InlineData("error.log ")]
    [InlineData(".")]
    [InlineData("..")]
    public void OnlyTheWritersOwnNamesAreServed(string name)
    {
        WriteLive("error.log", "warn: one\n");
        File.WriteAllText(Path.Combine(_directory, "notes.txt"), "not a log");

        LogFileStore store = new(_directory);

        Assert.Null(store.ReadTail(name, 10));
        Assert.Null(store.OpenRead(name, out _));
    }

    [Fact]
    public void ASymbolicLinkIsNotFollowedEvenWhenItsNameIsOne()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var outside = Path.Combine(_directory, "outside.txt");
        File.WriteAllText(outside, "warn: from outside\n");
        File.CreateSymbolicLink(Path.Combine(_directory, "error.log"), outside);

        LogFileStore store = new(_directory);

        Assert.Null(store.ReadTail("error.log", 10));
        Assert.Null(store.OpenRead("error.log", out _));
        Assert.Empty(store.List());
    }

    [Fact]
    public void OpenReadGivesTheWholeFileAndItsLength()
    {
        WriteLive("error.log", "one\ntwo\n");

        using var stream = new LogFileStore(_directory).OpenRead("error.log", out var length);
        using StreamReader reader = new(stream!);

        Assert.Equal(8, length);
        Assert.Equal("one\ntwo\n", reader.ReadToEnd());
    }

    [Fact]
    public void TheDirectoryIsAbsolute()
    {
        LogFileStore store = new(_directory);

        Assert.Equal(Path.GetFullPath(_directory), store.Directory);
        Assert.Throws<ArgumentException>(() => new LogFileStore(" "));
    }

    private void WriteLive(string name, string text) => File.WriteAllText(Path.Combine(_directory, name), text);

    private void WriteGzip(string name, string text)
    {
        using var file = File.Create(Path.Combine(_directory, name));
        using GZipStream gzip = new(file, CompressionLevel.Fastest);
        gzip.Write(Encoding.UTF8.GetBytes(text));
    }
}
