using System.Collections.Immutable;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Paduraksa.Contracts.Commands;

namespace Paduraksa.Application.Commands;

/// <summary>
/// The operational counterpart of <see cref="ConfigChangeFeed"/>: one <see cref="OperationalChange"/> per committed change to a
/// certificate, ACME account, DNS provider or ACME order (whatever <see cref="ConfigService"/> commits with an audit event but without a
/// revision), so the dashboard's lists refresh when something changes instead of polling. Handlers are called on the committing thread,
/// after the commit, in subscription order; they must return quickly (a page posts the refresh to its own dispatcher), and one that throws
/// is logged and does not stop the others or the writer.
/// </summary>
public sealed class OperationalChangeFeed
{
    private readonly Lock _lock = new();
    private readonly ILogger _logger;
    private ImmutableArray<Action<OperationalChange>> _handlers = [];

    public OperationalChangeFeed(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>Calls <paramref name="handler"/> for every change from now on; dispose the result to stop.</summary>
    public IDisposable Subscribe(Action<OperationalChange> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_lock)
        {
            _handlers = _handlers.Add(handler);
        }

        return new Subscription(this, handler);
    }

    /// <summary>Tells every subscriber; never throws.</summary>
    internal void Publish(OperationalChange change)
    {
        ImmutableArray<Action<OperationalChange>> handlers;
        lock (_lock)
        {
            handlers = _handlers;
        }

        foreach (var handler in handlers)
        {
            try
            {
                handler(change);
            }
#pragma warning disable CA1031 // A subscriber's failure must never reach the writer that committed the change.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                OperationalChangeFeedLog.HandlerFailed(_logger, change.Action, ex.GetType().Name);
            }
        }
    }

    private void Unsubscribe(Action<OperationalChange> handler)
    {
        lock (_lock)
        {
            _handlers = _handlers.Remove(handler);
        }
    }

    private sealed class Subscription(OperationalChangeFeed feed, Action<OperationalChange> handler) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                feed.Unsubscribe(handler);
            }
        }
    }
}

internal static partial class OperationalChangeFeedLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "A subscriber to the operational change feed failed on {Action} ({ErrorType}).")]
    public static partial void HandlerFailed(ILogger logger, string action, string errorType);
}
