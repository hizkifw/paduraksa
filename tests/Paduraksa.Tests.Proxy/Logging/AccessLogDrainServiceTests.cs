using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Paduraksa.Proxy.Logging;

namespace Paduraksa.Tests.Proxy.Logging;

/// <summary>
/// The background drain: it formats what the ring holds into the sink, flushes on shutdown, reports dropped
/// lines without repeating itself, and survives a sink that throws.
/// </summary>
public sealed class AccessLogDrainServiceTests
{
    [Fact]
    public void EverythingInTheRingIsFormattedIntoTheSink()
    {
        AccessLogRing ring = new(16);
        CapturingSink sink = new();
        AccessLogDrainService drain = new(ring, sink, AccessLogFormatOptions.Default, NullLogger<AccessLogDrainService>.Instance);
        Assert.True(ring.TryWrite(new() { Method = "GET", Path = "/one", Protocol = "HTTP/1.1", StatusCode = 200 }));
        Assert.True(ring.TryWrite(new() { Method = "POST", Path = "/two", Protocol = "HTTP/1.1", StatusCode = 201, Query = "?token=abc" }));

        drain.Drain();

        Assert.Equal(2, sink.Lines.Count);
        Assert.Contains("\"GET /one HTTP/1.1\" 200", sink.Lines[0], StringComparison.Ordinal);
        Assert.Contains("\"POST /two?token=REDACTED HTTP/1.1\" 201", sink.Lines[1], StringComparison.Ordinal);
        // The sink owns the newline; a line never carries one.
        Assert.All(sink.Lines, line => Assert.DoesNotContain("\n", line, StringComparison.Ordinal));
        Assert.Equal(0, ring.EntriesWritten - ring.EntriesRead);
    }

    [Fact]
    public async Task TheServiceDrainsWhileRunningAndFlushesWhenStopped()
    {
        AccessLogRing ring = new(16);
        CapturingSink sink = new();
        AccessLogDrainService service = new(ring, sink, AccessLogFormatOptions.Default, NullLogger<AccessLogDrainService>.Instance);
        await service.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(ring.TryWrite(new() { Method = "GET", Path = "/live" }));
        await sink.Written.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.True(ring.TryWrite(new() { Method = "GET", Path = "/at-shutdown" }));
        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.Contains(sink.Lines, line => line.Contains("/at-shutdown", StringComparison.Ordinal));
        Assert.True(sink.Flushed);
        service.Dispose();
    }

    [Fact]
    public void DropsAreReportedOnce()
    {
        AccessLogRing ring = new(2);
        CapturingSink sink = new();
        RecordingLogger logger = new();
        AccessLogDrainService drain = new(ring, sink, AccessLogFormatOptions.Default, logger);

        drain.Drain();
        Assert.True(ring.TryWrite(new() { Path = "/a" }));
        Assert.True(ring.TryWrite(new() { Path = "/b" }));
        Assert.False(ring.TryWrite(new() { Path = "/c" }));
        Assert.False(ring.TryWrite(new() { Path = "/d" }));

        drain.Drain();

        var (level, message) = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains("2 access-log lines were dropped", message, StringComparison.Ordinal);
        Assert.Equal(0, ring.Dropped);

        // Drops inside the reporting interval are counted, not reported again: the next report carries them.
        Assert.True(ring.TryWrite(new() { Path = "/e" }));
        Assert.True(ring.TryWrite(new() { Path = "/f" }));
        Assert.False(ring.TryWrite(new() { Path = "/g" }));
        drain.Drain();
        Assert.Single(logger.Entries);
        Assert.Equal(0, ring.Dropped);
    }

    [Fact]
    public void AFailingSinkIsReportedAndTheDrainKeepsGoing()
    {
        AccessLogRing ring = new(16);
        CapturingSink sink = new() { FailFirst = 1 };
        RecordingLogger logger = new();
        AccessLogDrainService drain = new(ring, sink, AccessLogFormatOptions.Default, logger);
        ring.TryWrite(new() { Path = "/one" });
        ring.TryWrite(new() { Path = "/two" });

        drain.Drain();

        // The failed line is skipped; the next one is written, and the failure is reported once.
        var line = Assert.Single(sink.Lines);
        Assert.Contains("/two", line, StringComparison.Ordinal);
        var (level, message) = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains("access log isn't being written", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATailThatThrowsIsReportedAndTheFileStillGetsEveryLine()
    {
        // The tail converts an entry for a listening subscriber with its own options; a list of sensitive names that throws makes
        // that conversion fail on every entry with a query. The file's formatter has the working options.
        AccessLogRing ring = new(16);
        CapturingSink sink = new();
        RecordingLogger logger = new();
        AccessLogTail tail = new(8, new AccessLogFormatOptions { SensitiveQueryKeys = new ThrowingList() });
        using var subscription = tail.Subscribe(AccessLogTailFilter.All, 0)!;
        AccessLogDrainService drain = new(ring, sink, AccessLogFormatOptions.Default, logger, tail);
        for (var i = 1; i <= 3; i++)
        {
            Assert.True(ring.TryWrite(new() { Method = "GET", Protocol = "HTTP/1.1", Path = "/p" + i, Query = "?a=" + i }));
        }

        drain.Drain();

        Assert.Equal(3, sink.Lines.Count);
        for (var i = 1; i <= 3; i++)
        {
            Assert.Contains($"\"GET /p{i}?a={i} HTTP/1.1\"", sink.Lines[i - 1], StringComparison.Ordinal);
        }

        Assert.False(subscription.Reader.TryRead(out _));
        // Reported once for the three failures (the drain's report interval), as a failing sink is.
        var (level, message) = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains("access log isn't being written", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFlushThatFailsAtShutdownIsReportedButNotThrown()
    {
        RecordingLogger logger = new();
        AccessLogDrainService drain = new(new AccessLogRing(16), new CapturingSink { FailFlush = true }, AccessLogFormatOptions.Default, logger);

        drain.Flush();

        Assert.Contains(logger.Entries, entry => entry.Message.Contains("access log isn't being written", StringComparison.Ordinal));
    }

    [Fact]
    public void APassThatFailsIsReportedAndTheNextPassCarriesOn()
    {
        // A formatter bug rather than a failing sink: an impossible timestamp makes DateTime throw, outside the per-line
        // sink guard. The drain is a hosted service of the core host, so the pass must be caught, reported and survived.
        AccessLogRing ring = new(16);
        CapturingSink sink = new();
        RecordingLogger logger = new();
        AccessLogDrainService drain = new(ring, sink, AccessLogFormatOptions.Default, logger);
        Assert.True(ring.TryWrite(new() { TimestampUtcTicks = DateTime.MaxValue.Ticks + 1, Path = "/bad" }));
        Assert.True(ring.TryWrite(new() { TimestampUtcTicks = DateTime.UtcNow.Ticks, Method = "GET", Protocol = "HTTP/1.1", Path = "/good" }));

        drain.Drain();

        var (level, message) = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains("access log isn't being written", message, StringComparison.Ordinal);

        // The pass stopped at the bad entry (it is consumed, so the ring isn't stuck); the next pass writes the rest.
        drain.Drain();
        var line = Assert.Single(sink.Lines);
        Assert.Contains("\"GET /good HTTP/1.1\"", line, StringComparison.Ordinal);
        Assert.Single(logger.Entries);
    }

    /// <summary>A list whose enumeration fails: what the formatter's redaction walks for every query.</summary>
    private sealed class ThrowingList : IReadOnlyList<string>
    {
        public int Count => 1;

        public string this[int index] => throw new InvalidOperationException("the sensitive-name list is unreadable");

        public IEnumerator<string> GetEnumerator() => throw new InvalidOperationException("the sensitive-name list is unreadable");

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>Keeps the lines the drain hands it.</summary>
    private sealed class CapturingSink : IAccessLogSink
    {
        private readonly List<string> _lines = [];

        public TaskCompletionSource Written { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int FailFirst { get; set; }

        public bool FailFlush { get; init; }

        public bool Flushed { get; private set; }

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lines)
                {
                    return [.. _lines];
                }
            }
        }

        public void WriteLine(ReadOnlySpan<char> line)
        {
            if (FailFirst > 0)
            {
                FailFirst--;
                throw new IOException("the disk is full");
            }

            lock (_lines)
            {
                _lines.Add(line.ToString());
            }

            Written.TrySetResult();
        }

        public void Flush()
        {
            if (FailFlush)
            {
                throw new IOException("the disk is full");
            }

            Flushed = true;
        }
    }

    /// <summary>Records what the drain reports, at any level.</summary>
    private sealed class RecordingLogger : ILogger<AccessLogDrainService>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get
            {
                lock (_entries)
                {
                    return [.. _entries];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
