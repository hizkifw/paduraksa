using System.Globalization;
using System.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Paduraksa.Core.Logging;

namespace Paduraksa.Server.Logging;

/// <summary>
/// The process's own log, on disk: every entry at or above <c>Paduraksa:Logs:ErrorLevel</c> (Warning by default) is queued to
/// <c>&lt;logs dir&gt;/error.log</c> in plain text, alongside the console. The level is the provider's own, so it does not
/// follow <c>Logging:LogLevel</c>; a line has the console's shape (UTC timestamp, level, category and event ID, the scopes
/// that carry <c>app=core|public|admin</c>, the message, then any exception indented with its stack trace).
/// <para>
/// The provider never blocks a logging call and never recurses: whole entries go into the writer's bounded queue (a full
/// queue drops them and the writer counts them), and entries from the logging infrastructure's own categories are skipped, so
/// a report about the error log can't be written into the error log.
/// </para>
/// </summary>
internal sealed class ErrorLogProvider : ILoggerProvider, ISupportExternalScope
{
    /// <summary>Entries from this namespace are the log files' own reports; they go to the console only.</summary>
    internal const string OwnCategoryPrefix = "Paduraksa.Server.Logging";

    private readonly RotatingLogFile _writer;
    private IExternalScopeProvider? _scopes;

    public ErrorLogProvider(RotatingLogFile writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
    }

    public RotatingLogFile Writer => _writer;

    public ILogger CreateLogger(string categoryName) => new ErrorLogger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider? scopeProvider) => _scopes = scopeProvider;

    /// <summary>Nothing: the writer belongs to the process's log files, which dispose it after the factory is done with the provider.</summary>
    public void Dispose()
    {
    }

    private sealed class ErrorLogger(ErrorLogProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => provider._scopes?.Push(state) ?? NullScope.Instance;

        /// <summary>
        /// Always true: the minimum level is a filter rule of this provider (see <c>SharedServices.CreateLoggerFactory</c>), which the
        /// factory checks before it calls <see cref="Log{TState}"/>. Filtering here too would only duplicate it, and the provider
        /// type's own filter is what keeps the level independent of the console's.
        /// </summary>
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            // Like the console: a missing formatter is nothing to write, never an exception thrown into the caller's log call.
            if (formatter is null || logLevel == LogLevel.None || category.StartsWith(ErrorLogProvider.OwnCategoryPrefix, StringComparison.Ordinal))
            {
                return;
            }

            provider._writer.WriteLine(FormatEntry(provider._writer.Time, provider._scopes, logLevel, eventId, category, formatter(state, exception) ?? string.Empty, exception));
        }
    }

    /// <summary>
    /// One entry: <c>2026-11-09T12:00:00.000Z warn: Paduraksa.X[12] =&gt; app=core: message</c>, then the exception indented on the lines
    /// below (its stack trace included).
    /// </summary>
    internal static string FormatEntry(
        TimeProvider time,
        IExternalScopeProvider? scopes,
        LogLevel level,
        EventId eventId,
        string category,
        string message,
        Exception? exception)
    {
        StringBuilder line = new(128);
        line.Append(time.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z' ", CultureInfo.InvariantCulture));
        line.Append(LevelName(level)).Append(": ").Append(category)
            .Append('[').Append(eventId.Id.ToString(CultureInfo.InvariantCulture)).Append(']');

        if (ScopeText(scopes) is { Length: > 0 } scope)
        {
            line.Append(" => ").Append(scope);
        }

        line.Append(": ").Append(message.ReplaceLineEndings(" "));
        if (exception is not null)
        {
            foreach (string exceptionLine in exception.ToString().Split('\n'))
            {
                line.Append('\n').Append("    ").Append(exceptionLine.TrimEnd('\r'));
            }
        }

        return line.ToString();
    }

    /// <summary>The scope values, as the console shows them: the app first (<c>app=core</c>), then anything else that has text.</summary>
    private static string ScopeText(IExternalScopeProvider? scopes)
    {
        if (scopes is null)
        {
            return string.Empty;
        }

        List<string> values = [];
        scopes.ForEachScope(static (scope, list) => AddScope(scope, list), values);
        return string.Join(" ", values);
    }

    private static void AddScope(object? scope, List<string> values)
    {
        if (scope is IReadOnlyList<KeyValuePair<string, object?>> pairs)
        {
            foreach (KeyValuePair<string, object?> pair in pairs)
            {
                string? rendered = pair.Key switch
                {
                    "App" => "app=" + Format(pair.Value),
                    _ => pair.Value?.ToString(),
                };
                if (!string.IsNullOrEmpty(rendered))
                {
                    values.Add(rendered);
                }
            }

            return;
        }

        if (scope?.ToString() is { Length: > 0 } text)
        {
            values.Add(text);
        }
    }

    private static string Format(object? value) => value switch
    {
        null => string.Empty,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>The console's level names, lower case and four letters where it has them.</summary>
    internal static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "trce",
        LogLevel.Debug => "dbug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "fail",
        LogLevel.Critical => "crit",
        _ => "none",
    };

    /// <summary>A scope on a logger without a scope provider (a provider used on its own, in a test).</summary>
    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
