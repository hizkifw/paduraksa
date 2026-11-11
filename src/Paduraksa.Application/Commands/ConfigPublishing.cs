using Microsoft.Extensions.Logging;
using Paduraksa.Application.Audit;
using Paduraksa.Application.Compilation;
using Paduraksa.Application.Config;
using Paduraksa.Application.LastKnownGood;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Yarp;

namespace Paduraksa.Application.Commands;

/// <summary>
/// What is live and how a commit reaches it: queues snapshots with the publisher, waits for the outcome a command reports, records
/// the last-known-good document once a revision is live, and keeps the live config's warnings, its validity timer and the store's
/// <see cref="Status"/>.
/// </summary>
/// <remarks>
/// The publisher only publishes the latest snapshot: when a newer commit supersedes this one's, the command waits for the newer
/// publish (which carries this change too) and reports that outcome. A failed publish doesn't undo the commit: the previous
/// snapshot stays live, the failure is logged and audited (<c>config.publish</c>), and the change goes live with the next
/// commit's publish or the next start, which compiles the latest revision. There is no retry.
/// </remarks>
internal sealed partial class ConfigPublishing : IDisposable
{
    // Timer periods are limited to about 49 days; a longer wait fires early, republishes, and schedules the rest.
    private static readonly TimeSpan MaxTimerDelay = TimeSpan.FromDays(20);

    private readonly SnapshotPublisher _publisher;
    private readonly TimeProvider _timeProvider;
    private readonly ConfigServiceOptions _options;
    private readonly LastKnownGoodFile _lastKnownGood;
    private readonly ConfigAudit _audit;
    private readonly SemaphoreSlim _writer;
    private readonly ILogger _logger;

    // The validity timer: one at a time, replaced whenever a newer snapshot goes live (see WentLiveWith).
    private readonly object _liveLock = new();
    private long _liveGeneration;
    private ITimer? _validityTimer;

    private ConfigStoreStatus _status = ConfigStoreStatus.NotStarted;
    private volatile IReadOnlyList<ConfigWarning> _warnings = [];
    private volatile PublishTicket? _latest;
    private volatile bool _disposed;

    public ConfigPublishing(
        SnapshotPublisher publisher, TimeProvider timeProvider, ConfigServiceOptions options, LastKnownGoodFile lastKnownGood,
        ConfigAudit audit, SemaphoreSlim writer, ILoggerFactory loggerFactory)
    {
        _publisher = publisher;
        _timeProvider = timeProvider;
        _options = options;
        _lastKnownGood = lastKnownGood;
        _audit = audit;
        _writer = writer;
        _logger = loggerFactory.CreateLogger<ConfigPublishing>();
    }

    /// <summary>A certificate's validity boundary passed: the live config has to be compiled and published again.</summary>
    public event Action? ValidityChanged;

    public bool IsDisposed => _disposed;

    /// <summary>Where the running config came from and whether commands are accepted.</summary>
    public ConfigStoreStatus Status => Volatile.Read(ref _status);

    /// <summary>The warnings of the snapshot that is live (startup, a commit, a certificate change); empty before the first one.</summary>
    public IReadOnlyList<ConfigWarning> Warnings => _warnings;

    /// <summary>The latest queued publish; written under the writer lock, right after it is queued.</summary>
    public PublishTicket? Latest
    {
        get => _latest;
        set => _latest = value;
    }

    public void SetStatus(ConfigStoreStatus status) => Volatile.Write(ref _status, status);

    public void Dispose()
    {
        _disposed = true;
        lock (_liveLock)
        {
            _validityTimer?.Dispose();
            _validityTimer = null;
        }
    }

    /// <summary>
    /// Queues <paramref name="snapshot"/>. When the publish goes live, its warnings become <see cref="Warnings"/> and a
    /// republish is scheduled for when the certificate inventory it was compiled against next changes with the clock
    /// (<see cref="ConfigCompilation.ValidityChangesAt"/>), so a certificate that expires or becomes valid takes effect
    /// without any other trigger.
    /// </summary>
    public Task<PublishResult> Start(RuntimeSnapshot snapshot, ConfigCompilation compilation)
    {
        Task<PublishResult> publishing;
        try
        {
            publishing = _publisher.PublishAsync(snapshot);
        }
        catch (ArgumentException ex)
        {
            // A bound candidate: a compiler bug. Reported like any failed publish, with a sentence and the trace ID rather than the
            // exception's own text (the log has it).
            LogPublishRefused(ex);
            return Task.FromResult(new PublishResult(
                PublishStatus.Failed,
                0,
                [
                    "The candidate snapshot was refused by the publisher, so nothing went live. The details are in the server log under this response's trace ID."
                ]));
        }

        _ = publishing.ContinueWith(
            task =>
            {
                if (task.IsCompletedSuccessfully)
                {
                    if (task.Result.Status == PublishStatus.Published)
                    {
                        WentLiveWith(task.Result.Generation, compilation);
                    }

                    return;
                }

                // The publisher answered with a failed result for the failures it knows; only a cancel or a thrown fault gets
                // here, and either would otherwise leave the publish unrecorded.
                if (task.IsCanceled)
                {
                    LogPublishCancelled();
                }
                else
                {
                    LogPublishFaulted(task.Exception!.GetBaseException());
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return publishing;
    }

    /// <summary>A publish of generation <paramref name="generation"/> is live: record its warnings and schedule its
    /// validity timer, unless a later generation got there first.</summary>
    private void WentLiveWith(long generation, ConfigCompilation compilation)
    {
        lock (_liveLock)
        {
            if (generation < _liveGeneration || _disposed)
            {
                return;
            }

            _liveGeneration = generation;
            _warnings = compilation.Warnings;
            _validityTimer?.Dispose();
            _validityTimer = null;

            if (compilation.ValidityChangesAt is not { } at)
            {
                return;
            }

            var due = at - _timeProvider.GetUtcNow();
            due = due < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) :
                due > MaxTimerDelay ? MaxTimerDelay : due;
            _validityTimer = _timeProvider.CreateTimer(static state => ((ConfigPublishing)state!).ValidityChanged?.Invoke(),
                this, due, Timeout.InfiniteTimeSpan);
        }
    }

    public async Task<ConfigCommandResult> FinishAsync(Execution execution, CommandOrigin origin)
    {
        var started = _timeProvider.GetTimestamp();

        TimeSpan Remaining()
        {
            var left = _options.PublishWait - _timeProvider.GetElapsedTime(started);
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }

        var own = execution.Publish!;
        var committed = execution.Result;
        if (await WaitAsync(own.Task, Remaining()).ConfigureAwait(false) is not { } result)
        {
            LogPublishPending(own.Revision.Seq, own.Revision.Id, _options.PublishWait);
            _ = AuditLateAsync(own, origin);
            return committed with
            {
                Publish = new PublishOutcome(PublishOutcomeStatus.Pending, 0, [], own.Revision.Id)
            };
        }

        if (result.Status != PublishStatus.Superseded)
        {
            var outcome = Map(result, own.Revision.Id);
            if (outcome.Succeeded)
            {
                WentLive(own);
                return committed with { Publish = outcome };
            }

            await AuditPublishFailureAsync(own.Revision, outcome, origin).ConfigureAwait(false);
            return committed with { Status = ConfigCommandStatus.PublishFailed, Publish = outcome };
        }

        // Superseded: a newer commit's publish carries this change, so its outcome is this one's. Its owner audits it.
        var followed = own;
        while (true)
        {
            // The newer ticket is set under the writer lock right after it is queued, so read it under the lock.
            if (!await _writer.WaitAsync(Remaining()).ConfigureAwait(false))
            {
                return committed with
                {
                    Publish = new PublishOutcome(PublishOutcomeStatus.Pending, 0, [], followed.Revision.Id)
                };
            }

            PublishTicket? newer;
            try
            {
                newer = _latest;
            }
            finally
            {
                _writer.Release();
            }

            if (newer is null || ReferenceEquals(newer, followed)
                              || await WaitAsync(newer.Task, Remaining()).ConfigureAwait(false) is not { } latest)
            {
                return committed with
                {
                    Publish = new PublishOutcome(PublishOutcomeStatus.Pending, 0, [],
                        (newer ?? followed).Revision.Id)
                };
            }

            if (latest.Status == PublishStatus.Superseded)
            {
                followed = newer;
                continue;
            }

            var outcome = Map(latest, newer.Revision.Id);
            if (outcome.Succeeded)
            {
                WentLive(newer); // before this returns; its own command does it too
            }

            return committed with
            {
                Status = outcome.Succeeded ? ConfigCommandStatus.Committed : ConfigCommandStatus.PublishFailed,
                Publish = outcome,
            };
        }
    }

    private async Task<PublishResult?> WaitAsync(Task<PublishResult> publishing, TimeSpan timeout)
    {
        try
        {
            return await publishing.WaitAsync(timeout, _timeProvider).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// <summary>Follows a publish that was still pending when its command returned: on success, <see cref="WentLive"/>;
    /// on failure, logs and audits it.</summary>
    public async Task AuditLateAsync(PublishTicket ticket, CommandOrigin origin)
    {
        var result = await ticket.Task.ConfigureAwait(false); // the publisher completes its tasks, never faults them
        if (result.Status == PublishStatus.Published)
        {
            WentLive(ticket);
            return;
        }

        if (result.Status == PublishStatus.Superseded)
        {
            return; // the newer publish's command reports it
        }

        await AuditPublishFailureAsync(ticket.Revision, Map(result, ticket.Revision.Id), origin).ConfigureAwait(false);
    }

    private async Task AuditPublishFailureAsync(RevisionRef revision, PublishOutcome outcome, CommandOrigin origin)
    {
        LogPublishFailed(revision.Seq, revision.Id, outcome.Status, string.Join("; ", outcome.Errors));
        AuditEntry entry = new(origin.Actor, origin.Channel, "config.publish", AuditOutcome.Failed)
        {
            Target = revision.Id.IsEmpty ? null : revision.Id.ToString(),
            RevisionId = revision.Id.IsEmpty ? null : revision.Id,
            ViaAdminProxy = origin.ViaAdminProxy,
            Details = AuditDetails.Json(w =>
            {
                w.WriteString("status", DocumentNames.Of(outcome.Status));
                w.WriteStartArray("errors");
                foreach (var error in outcome.Errors.Take(ConfigAudit.MaxAuditedCodes))
                {
                    w.WriteStringValue(error.Length > ConfigAudit.MaxAuditedErrorLength ? error[..ConfigAudit.MaxAuditedErrorLength] : error);
                }

                w.WriteEndArray();
            }),
        };
        await _audit.TryAppendAsync(entry).ConfigureAwait(false);
    }

    private static PublishOutcome Map(PublishResult result, ConfigRevisionId revision) => new(
        result.Status switch
        {
            PublishStatus.Published => PublishOutcomeStatus.Published,
            PublishStatus.Invalid => PublishOutcomeStatus.Invalid,
            PublishStatus.Rejected => PublishOutcomeStatus.Rejected,
            PublishStatus.TimedOut => PublishOutcomeStatus.TimedOut,
            _ => PublishOutcomeStatus.Failed,
        },
        result.Generation,
        result.Errors,
        revision);

    /// <summary>
    /// A commit's publish succeeded: records its document as last-known-good in the background and ends a degraded start that
    /// still accepted commands. Idempotent, since a superseded command may run it for the newer publish too.
    /// </summary>
    private void WentLive(PublishTicket ticket)
    {
        if (ticket.LastKnownGood is { } entry)
        {
            _lastKnownGood.Offer(entry);
        }

        var status = Status;
        if (ticket.FromDatabase && !ticket.Revision.Id.IsEmpty && status.IsDegraded && status.AcceptsCommands
            && ReferenceEquals(Interlocked.CompareExchange(ref _status, new ConfigStoreStatus(ConfigSource.Database, ticket.Revision), status), status))
        {
            LogRecovered(ticket.Revision.Seq, ticket.Revision.Id);
        }
    }

    /// <summary>A command refused because the store is unusable; not audited (the audit log lives in that store).</summary>
    public ConfigCommandResult Unavailable(ConfigCommand command, ConfigStoreStatus status)
    {
        LogRefused(command.GetType().Name, status.Problem ?? "unknown");
        return new ConfigCommandResult(
            ConfigCommandStatus.Unavailable,
            Errors:
            [
                new ConfigError(
                    string.Empty,
                    ConfigService.StoreUnavailableCode,
                    $"The config store is unavailable, so changes are refused: {status.Problem}. Paduraksa is serving the last-known-good config"
                    + $" ({status.Revision}); fix or restore the database and restart."),
            ]);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recovered from a degraded start: revision #{Seq} ({Revision}) from the database is live.")]
    private partial void LogRecovered(long seq, ConfigRevisionId revision);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused {Command}: the config store is unavailable ({Problem}).")]
    internal partial void LogRefused(string command, string problem);

    [LoggerMessage(Level = LogLevel.Error,
        Message =
            "Config revision #{Seq} ({Revision}) is committed but publishing it failed ({Status}): {Errors}. The previous snapshot stays live; the change goes live with the next successful publish or start.")]
    private partial void LogPublishFailed(long seq, ConfigRevisionId revision, PublishOutcomeStatus status,
        string errors);

    [LoggerMessage(Level = LogLevel.Error,
        Message =
            "The publisher refused the candidate snapshot (an ArgumentException, so a compiler bug); nothing went live.")]
    private partial void LogPublishRefused(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning,
        Message =
            "Config revision #{Seq} ({Revision}) is committed but not live after {Wait}; it is still queued for publishing.")]
    private partial void LogPublishPending(long seq, ConfigRevisionId revision, TimeSpan wait);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Publishing the config snapshot was cancelled; the previous snapshot stays live.")]
    private partial void LogPublishCancelled();

    [LoggerMessage(Level = LogLevel.Warning,
        Message =
            "Publishing the config snapshot threw instead of returning a result; the previous snapshot stays live.")]
    private partial void LogPublishFaulted(Exception exception);
}
