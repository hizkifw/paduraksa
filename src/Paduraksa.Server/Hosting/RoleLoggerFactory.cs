using System.Collections;
using System.Collections.Concurrent;

using Paduraksa.Server.Logging;

namespace Paduraksa.Server.Hosting;

/// <summary>
/// Per-host view of the one shared <see cref="ILoggerFactory"/>. Categories stay untouched (so
/// <c>Logging:LogLevel</c> filters work exactly as usual); every entry instead carries an
/// <c>App</c> scope property (<c>core</c>/<c>public</c>/<c>admin</c>). The simple console formatter
/// prints it as <c>=&gt; app=public</c>; the JSON formatter emits it as a property.
/// Doesn't own the inner factory: disposing it is a no-op, <see cref="SharedServices"/> owns the real one.
/// Entries the optional <see cref="StartupLogFilter"/> rejects are dropped, and a client going away (<see cref="PeerGoneLogFilter"/>)
/// is written at Debug as one line.
/// </summary>
internal sealed class RoleLoggerFactory : ILoggerFactory
{
    private readonly ILoggerFactory _inner;
    private readonly AppScope _scope;
    private readonly StartupLogFilter? _startupLogFilter;
    private readonly PeerGoneLogFilter? _peerGone;
    private readonly ConcurrentDictionary<string, ILogger> _loggers = new(StringComparer.Ordinal);

    public RoleLoggerFactory(ILoggerFactory inner, HostRole role, StartupLogFilter? startupLogFilter = null, PeerGoneLogFilter? peerGone = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _scope = new AppScope(role.ToLogName());
        _startupLogFilter = startupLogFilter;
        _peerGone = peerGone;
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(
            categoryName,
            static (category, self) => new RoleLogger(self._inner.CreateLogger(category), self._scope, category, self._startupLogFilter, self._peerGone),
            this);

    /// <summary>Providers are configured once, centrally; an app adding its own would only see part of the process.</summary>
    public void AddProvider(ILoggerProvider provider) =>
        throw new NotSupportedException("Logging providers are configured once for the whole process (SharedServices).");

    public void Dispose()
    {
        // Intentionally empty: the shared factory is disposed exactly once by SharedServices.
    }

    private sealed class RoleLogger(ILogger inner, AppScope scope, string category, StartupLogFilter? startupLogFilter, PeerGoneLogFilter? peerGone) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            // Classified before the level check, so a client going away is counted even when Debug is off.
            if (peerGone is not null && peerGone.IsPeerGone(category, logLevel, exception, out var cause))
            {
                if (inner.IsEnabled(LogLevel.Debug))
                {
                    using var quiet = inner.BeginScope(scope);
                    inner.Log(LogLevel.Debug, eventId, state, null, (s, e) => $"{formatter(s, exception)} The client went away: {cause}");
                }

                return;
            }

            if (!inner.IsEnabled(logLevel) || (startupLogFilter?.ShouldDrop(category, eventId, exception) ?? false))
            {
                return;
            }

            using var _ = inner.BeginScope(scope);
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
