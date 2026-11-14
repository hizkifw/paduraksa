using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Paduraksa.Application.Audit;
using Paduraksa.Application.Commands;
using Paduraksa.Contracts.Notifications;
using Paduraksa.Core.Model;
using Paduraksa.Core.Options;
using Paduraksa.Proxy.Yarp;

namespace Paduraksa.Application.Notifications;

/// <summary>What the dashboard and the API read: every checked upstream's live health, replaced whole once a second by <see cref="HealthMonitor"/>.</summary>
public sealed class UpstreamHealthBoard
{
    private volatile IReadOnlyList<UpstreamHealthView> _views = [];

    internal void Update(IReadOnlyList<UpstreamHealthView> views) => _views = views;

    /// <summary>Every upstream the proxy knows, with its destinations; empty before the first look or while there is no proxy.</summary>
    public IReadOnlyList<UpstreamHealthView> All() => _views;

    public UpstreamHealthView? Find(UpstreamId id)
    {
        foreach (var view in _views)
        {
            if (view.Id == id)
            {
                return view;
            }
        }

        return null;
    }
}

/// <summary>
/// Watches YARP's destination health from outside the request path: reads it once a second, runs it through the <see cref="HealthTracker"/>,
/// keeps the <see cref="UpstreamHealthBoard"/> current and, for each change worth reporting, writes a system audit entry and publishes the
/// event to the notification dispatcher.
/// </summary>
public sealed partial class HealthMonitor : BackgroundService
{
    private readonly IUpstreamHealth _health;
    private readonly ConfigService _config;
    private readonly INotificationEvents _events;
    private readonly UpstreamHealthBoard _board;
    private readonly LiveOptions<NotificationOptions> _live;

    // Read on every use: the server settings may replace them while the process runs.
    private NotificationOptions Options => _live.Current;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly HealthTracker _tracker;

    internal HealthMonitor(
        IUpstreamHealth health,
        ConfigService config,
        INotificationEvents events,
        UpstreamHealthBoard board,
        LiveOptions<NotificationOptions> options,
        TimeProvider time,
        ILogger<HealthMonitor> logger)
    {
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _health = health;
        _config = config;
        _events = events;
        _board = board;
        _live = options;
        _time = time;
        _logger = logger;
        _tracker = new HealthTracker(options);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(Options.HealthPollInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await TickAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogTickFailed(ex);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
    }

    /// <summary>One look: update the board, then report what changed.</summary>
    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        var (changes, views) = _tracker.Observe(_health.ReadAll(), _time.GetUtcNow());
        _board.Update(views);
        if (changes.Count == 0)
        {
            return;
        }

        var names = await UpstreamNamesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var change in changes)
        {
            var name = names.GetValueOrDefault(change.Upstream) ?? change.Upstream.ToString();
            await _config.TryAppendAuditAsync(AuditOf(change)).ConfigureAwait(false);
            _events.Publish(NotificationText.Health(change, name, Options.FlapWindow, Options.FlapSettle));
        }
    }

    private async Task<Dictionary<UpstreamId, string>> UpstreamNamesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var current = await _config.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            return current.Document.Upstreams.ToDictionary(u => u.Id, u => u.Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogNamesFailed(ex.GetType().Name);
            return [];
        }
    }

    private static AuditEntry AuditOf(HealthChange change)
    {
        var scope = change.Scope == HealthScope.Upstream ? "upstream" : "upstream.destination";
        var action = change.Flapping ? $"{scope}.flapping" : $"{scope}.{(change.Down ? "down" : "up")}";
        return new AuditEntry(Actor.System, Channel.System, action, change.Down || change.Flapping ? AuditOutcome.Failed : AuditOutcome.Success)
        {
            Target = change.Upstream.ToString(),
            Details = AuditDetails.Json(w =>
            {
                if (change.Destination is { } destination)
                {
                    w.WriteString("destination", destination.ToString());
                    w.WriteString("address", change.Address);
                }

                if (change.Reason is not null)
                {
                    w.WriteString("reason", change.Reason);
                }

                w.WriteString("since", change.Since.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture));
                if (change.Settled)
                {
                    w.WriteBoolean("settled", true);
                }
            }),
        };
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The upstream health watcher failed a look; it tries again.")]
    private partial void LogTickFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Upstream names could not be read for a health change ({Type}); IDs are used.")]
    private partial void LogNamesFailed(string type);
}
