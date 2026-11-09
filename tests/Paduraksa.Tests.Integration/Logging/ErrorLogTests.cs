using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Paduraksa.Core.Logging;
using Paduraksa.Server.Configuration;
using Paduraksa.Server.Hosting;
using Paduraksa.Server.Logging;

namespace Paduraksa.Tests.Integration.Logging;

/// <summary>
/// The error log: every entry at its own minimum level, in plain text with the app's scope and an exception's stack, queued so a
/// logging call never waits for the disk.
/// </summary>
public sealed class ErrorLogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    private string LogsDirectory => Path.Combine(_root, "logs");

    private string ErrorLog => Path.Combine(LogsDirectory, LogFileNames.ErrorLive);

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
    public void AnEntryHasTheConsoleShapeWithTheAppScopeAndTheExceptionBelowIt()
    {
        FixedClock clock = new(new DateTimeOffset(2026, 11, 9, 12, 0, 0, 500, TimeSpan.Zero));
        ScopeProvider scopes = new();
        using (scopes.Push(new List<KeyValuePair<string, object?>> { new("App", "public") }))
        {
            string entry = ErrorLogProvider.FormatEntry(
                clock,
                scopes,
                LogLevel.Error,
                new EventId(12, "Something"),
                "Paduraksa.Proxy.Gate.GateMiddleware",
                "the gate said no\r\nand a second line",
                Thrown());

            string[] lines = entry.Split('\n');
            Assert.Equal("2026-11-09T12:00:00.500Z fail: Paduraksa.Proxy.Gate.GateMiddleware[12] => app=public: the gate said no and a second line", lines[0]);
            Assert.StartsWith("    System.InvalidOperationException: boom", lines[1], StringComparison.Ordinal);
            Assert.Contains(lines, line => line.TrimStart().StartsWith("at ", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void AnEntryWithoutAScopeOrAnExceptionIsOneLine()
    {
        string entry = ErrorLogProvider.FormatEntry(
            new FixedClock(DateTimeOffset.UnixEpoch), null, LogLevel.Warning, default, "Category", "one line", null);

        Assert.Equal("1970-01-01T00:00:00.000Z warn: Category[0]: one line", entry);
    }

    [Theory]
    [InlineData(LogLevel.Trace, "trce")]
    [InlineData(LogLevel.Debug, "dbug")]
    [InlineData(LogLevel.Information, "info")]
    [InlineData(LogLevel.Warning, "warn")]
    [InlineData(LogLevel.Error, "fail")]
    [InlineData(LogLevel.Critical, "crit")]
    public void LevelNamesMatchTheConsole(LogLevel level, string name) => Assert.Equal(name, ErrorLogProvider.LevelName(level));

    [Fact]
    public async Task TheErrorLogKeepsWarningAndAboveByDefault()
    {
        await using SharedServices shared = await StartAsync(new() { ["Logging:LogLevel:Default"] = "Information" });
        ILogger logger = new RoleLoggerFactory(shared.LoggerFactory, HostRole.Core).CreateLogger("Paduraksa.Tests.Sample");

        Write(logger, LogLevel.Information, "an information line");
        Write(logger, LogLevel.Warning, "a warning line");
        Write(logger, LogLevel.Error, "an error line");
        await shared.DisposeAsync();

        List<string> lines = Read();
        Assert.DoesNotContain(lines, line => line.Contains("an information line", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("warn: Paduraksa.Tests.Sample[0] => app=core: a warning line", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("fail: Paduraksa.Tests.Sample[0] => app=core: an error line", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ItsLevelIsItsOwnAndFollowsTheConsoleNowhere()
    {
        // The console drops everything below Critical; the error log is told to keep Information, and does.
        await using SharedServices shared = await StartAsync(
            new() { ["Logging:LogLevel:Default"] = "Critical" },
            logs => logs.ErrorLevel = LogLevel.Information);
        ILogger logger = new RoleLoggerFactory(shared.LoggerFactory, HostRole.Admin).CreateLogger("Paduraksa.Tests.Sample");

        Write(logger, LogLevel.Information, "kept in the file only");
        await shared.DisposeAsync();

        Assert.Contains(Read(), line => line.Contains("info: Paduraksa.Tests.Sample[0] => app=admin: kept in the file only", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnEventIdAndAnExceptionReachTheFileWithTheirStack()
    {
        await using SharedServices shared = await StartAsync(new(), logs => logs.ErrorLevel = LogLevel.Error);
        ILogger logger = shared.LoggerFactory.CreateLogger("Paduraksa.Tests.Throwing");

        logger.Log(LogLevel.Error, new EventId(7, "Boom"), "it broke", Thrown(), static (state, _) => state);
        await shared.DisposeAsync();

        List<string> lines = Read();
        Assert.Contains(lines, line => line.Contains("fail: Paduraksa.Tests.Throwing[7]: it broke", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("InvalidOperationException: boom", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.TrimStart().StartsWith("at ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheLoggingInfrastructureDoesNotWriteToTheFileAboutItself()
    {
        await using SharedServices shared = await StartAsync(new(), logs => logs.ErrorLevel = LogLevel.Information);
        ILogger infrastructure = shared.LoggerFactory.CreateLogger("Paduraksa.Server.Logging.RotatingLogFile");

        Write(infrastructure, LogLevel.Error, "a report about the log files themselves");
        await shared.DisposeAsync();

        Assert.DoesNotContain(Read(), line => line.Contains("a report about the log files themselves", StringComparison.Ordinal));
    }

    [Fact]
    public void AFullQueueDropsEntriesRatherThanBlockingTheCaller()
    {
        using ManualResetEventSlim pause = new(false);
        using ManualResetEventSlim taken = new(false);
        RotatingLogFile writer = new(
            new LogFileWriterSettings(LogFileKind.Error, LogsDirectory, 1024 * 1024, 10, TimeSpan.FromDays(1), 1024 * 1024),
            new FixedClock(DateTimeOffset.UnixEpoch),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            new RotatingLogFileTestSeams { Pause = pause, Taken = taken });
        using (writer)
        {
            using ILoggerFactory factory = LoggerFactory.Create(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddProvider(new ErrorLogProvider(writer));
            });
            ILogger logger = factory.CreateLogger("Paduraksa.Tests.Load");

            writer.WriteLine("taken and stalled");
            Assert.True(taken.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            for (int i = 0; i < 20_000; i++)
            {
                Write(logger, LogLevel.Warning, "an entry under load");
            }

            Assert.True(writer.DroppedLines > 0);
            pause.Set();
        }
    }

    private async Task<SharedServices> StartAsync(Dictionary<string, string?> settings, Action<PaduraksaLogOptions>? configureLogs = null)
    {
        ConfigurationManager configuration = new();
        configuration.AddInMemoryCollection(settings);
        PaduraksaOptions options = new() { DataDirectory = Path.Combine(_root, "data") };
        options.Logs.ResolvedDirectory = LogsDirectory;
        configureLogs?.Invoke(options.Logs);
        SharedServices shared = SharedServices.Create(configuration, options);
        // The writers are background threads; a turn of the scheduler lets the provider's first entry land in the queue.
        await Task.Yield();
        return shared;
    }

    /// <summary>An exception that really was thrown, so it carries the stack trace a crashed request's would.</summary>
    private static InvalidOperationException Thrown()
    {
        try
        {
            throw new InvalidOperationException("boom");
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }
    }

    /// <summary>The raw logger call: the tests choose the level, the event ID and the exception, which a source-generated method can't.</summary>
    private static void Write(ILogger logger, LogLevel level, string message, Exception? exception = null) =>
        logger.Log(level, default, message, exception, static (state, _) => state);

    private List<string> Read() => System.IO.File.Exists(ErrorLog) ? [.. System.IO.File.ReadAllLines(ErrorLog)] : [];

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>What a logger factory gives its providers: the scopes open on the current flow.</summary>
    private sealed class ScopeProvider : IExternalScopeProvider
    {
        private readonly AsyncLocal<Scope?> _current = new();

        public void ForEachScope<TState>(Action<object?, TState> callback, TState state)
        {
            for (Scope? scope = _current.Value; scope is not null; scope = scope.Parent)
            {
                callback(scope.Value, state);
            }
        }

        public IDisposable Push(object? scope)
        {
            Scope pushed = new(scope, _current.Value);
            _current.Value = pushed;
            return new Popper(this, pushed);
        }

        private sealed record Scope(object? Value, Scope? Parent);

        private sealed class Popper(ScopeProvider owner, Scope scope) : IDisposable
        {
            public void Dispose()
            {
                if (ReferenceEquals(owner._current.Value, scope))
                {
                    owner._current.Value = scope.Parent;
                }
            }
        }
    }
}
