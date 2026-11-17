using Microsoft.Extensions.Logging;

namespace Paduraksa.Tests.Vault.Testing;

/// <summary>A fresh, empty directory under the system temp path, deleted again on dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("paduraksa-vault-tests-").FullName;

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose() => Directory.Delete(Path, recursive: true);
}

/// <summary>An <see cref="ILogger{TCategoryName}"/> that keeps what it was given, to assert what is (and is not) logged.</summary>
internal sealed class ListLogger<T> : ILogger<T>
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
