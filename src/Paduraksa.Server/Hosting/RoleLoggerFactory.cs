using System.Collections;
using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

namespace Paduraksa.Server.Hosting;

/// <summary>
/// Per-host view of the one shared <see cref="ILoggerFactory"/>. Categories stay untouched (so
/// <c>Logging:LogLevel</c> filters work exactly as usual); every entry instead carries an
/// <c>App</c> scope property (<c>core</c>/<c>public</c>/<c>admin</c>). The simple console formatter
/// prints it as <c>=&gt; app=public</c>; the JSON formatter emits it as a property.
/// Doesn't own the inner factory: disposing it is a no-op, <see cref="SharedServices"/> owns the real one.
/// Entries the optional <see cref="StartupLogFilter"/> rejects are dropped.
/// </summary>
internal sealed class RoleLoggerFactory : ILoggerFactory
{
    private readonly ILoggerFactory _inner;
    private readonly AppScope _scope;
    private readonly StartupLogFilter? _startupLogFilter;
    private readonly ConcurrentDictionary<string, ILogger> _loggers = new(StringComparer.Ordinal);

    public RoleLoggerFactory(ILoggerFactory inner, HostRole role, StartupLogFilter? startupLogFilter = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _scope = new AppScope(role.ToLogName());
        _startupLogFilter = startupLogFilter;
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(
            categoryName,
            static (category, self) => new RoleLogger(self._inner.CreateLogger(category), self._scope, category, self._startupLogFilter),
            this);

    /// <summary>Providers are configured once, centrally; an app adding its own would only see part of the process.</summary>
    public void AddProvider(ILoggerProvider provider) =>
        throw new NotSupportedException("Logging providers are configured once for the whole process (SharedServices).");

    public void Dispose()
    {
        // Intentionally empty: the shared factory is disposed exactly once by SharedServices.
    }

    private sealed class RoleLogger(ILogger inner, AppScope scope, string category, StartupLogFilter? startupLogFilter) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!inner.IsEnabled(logLevel) || (startupLogFilter?.ShouldDrop(category, eventId, exception) ?? false))
            {
                return;
            }

            using IDisposable? _ = inner.BeginScope(scope);
            inner.Log(logLevel, eventId, state, exception, formatter);
        }
    }

    /// <summary>Structured scope state: one <c>App</c> property, rendered as <c>app=&lt;role&gt;</c>.</summary>
    private sealed class AppScope(string app) : IReadOnlyList<KeyValuePair<string, object?>>
    {
        private readonly KeyValuePair<string, object?> _property = new("App", app);
        private readonly string _text = "app=" + app;

        public int Count => 1;

        public KeyValuePair<string, object?> this[int index] =>
            index == 0 ? _property : throw new ArgumentOutOfRangeException(nameof(index));

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            yield return _property;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() => _text;
    }
}
