// Linked into the test projects that assert what was logged (<Compile Include Link>), so it compiles into each assembly as an
// internal type of its own.
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Paduraksa.Tests.Shared;

/// <summary>An <see cref="ILogger"/> that keeps what it was given, in order, to assert what is (and is not) logged.</summary>
internal class ListLogger : ILogger
{
    private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

    public IReadOnlyList<(LogLevel Level, string Message)> Entries => [.. _entries];

    public IReadOnlyList<string> Messages => [.. _entries.Select(static e => e.Message)];

    public IReadOnlyList<string> At(LogLevel level) => [.. _entries.Where(e => e.Level == level).Select(static e => e.Message)];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        _entries.Enqueue((logLevel, formatter(state, exception)));
}

internal sealed class ListLogger<T> : ListLogger, ILogger<T>;

/// <summary>An <see cref="ILoggerFactory"/> whose loggers all write to one <see cref="ListLogger"/>.</summary>
internal sealed class ListLoggerFactory : ILoggerFactory
{
    private readonly ListLogger _logger = new();

    public IReadOnlyList<(LogLevel Level, string Message)> Entries => _logger.Entries;

    public ILogger CreateLogger(string categoryName) => _logger;

    public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();

    public void Dispose()
    {
    }
}
