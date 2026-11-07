using System.Globalization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Paduraksa.Proxy.Snapshot;

using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Model;

namespace Paduraksa.Proxy.Yarp;

/// <summary>How a publish ended.</summary>
public enum PublishStatus
{
    /// <summary>The candidate is live under <see cref="PublishResult.Generation"/>.</summary>
    Published,

    /// <summary>A newer candidate arrived before this one started; only the latest is published (coalescing).</summary>
    Superseded,

    /// <summary>YARP's <see cref="IConfigValidator"/> rejected the candidate; nothing was handed to YARP.</summary>
    Invalid,

    /// <summary>YARP reported <c>ConfigurationApplyingFailed</c>; the previous YARP config was restored.</summary>
    Rejected,

    /// <summary>YARP didn't report an outcome in time. The previous snapshot stays live; YARP's state is in doubt.</summary>
    TimedOut,

    /// <summary>Anything else (e.g. YARP applied but its endpoints never matched the candidate).</summary>
    Failed,
}

/// <summary>Outcome of <see cref="SnapshotPublisher.PublishAsync"/>.</summary>
public sealed record PublishResult(PublishStatus Status, long Generation, IReadOnlyList<string> Errors)
{
    public bool Succeeded => Status == PublishStatus.Published;

    internal static PublishResult Published(long generation) => new(PublishStatus.Published, generation, []);

    internal static PublishResult Superseded { get; } = new(PublishStatus.Superseded, 0, []);

    internal static PublishResult Fail(PublishStatus status, string error) => new(status, 0, [error]);
}

/// <summary>Tunables of <see cref="SnapshotPublisher"/>.</summary>
public sealed class SnapshotPublisherOptions
{
    /// <summary>How long to wait for YARP's <c>ConfigurationApplied</c>/<c>ConfigurationApplyingFailed</c>.</summary>
    public TimeSpan ApplyTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long to wait for YARP's endpoints to match an applied config (normally they already do).</summary>
    public TimeSpan EndpointWait { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// The single writer that makes candidate snapshots live (PLAN §6.1 step 6, §6.2, spike S2):
/// <list type="number">
/// <item>validate the candidate's YARP config with YARP's own <see cref="IConfigValidator"/>;</item>
/// <item>hand it to YARP through <see cref="SnapshotProxyConfigProvider"/> (skipped when YARP already runs equal content);</item>
/// <item>await <c>ConfigurationApplied</c> or <c>ConfigurationApplyingFailed</c> for that <c>RevisionId</c>, with a timeout;</item>
/// <item>capture YARP's new endpoints by route ID into the candidate's slot table;</item>
/// <item>swap <see cref="SnapshotStore"/>. Requests see the new snapshot from here on; in-flight ones keep the old one.</item>
/// </list>
/// <para><b>Coalescing:</b> one publish runs at a time. A candidate that is still queued when a newer one arrives is
/// completed as <see cref="PublishStatus.Superseded"/>, so a burst of edits costs one YARP reload, not one each.</para>
/// <para><b>Before YARP exists</b> (the core host publishes before the public app starts, PLAN §3): a candidate with no
/// YARP content is published at once. Any other candidate waits, without a timeout, until <see cref="Attach"/>
/// validates it and hands it to YARP's initial load, so the first request already sees it. <b>Startup code must
/// therefore never await such a publish</b>: <see cref="Attach"/> runs while the public app starts, which happens only
/// after the core host's start has returned. Queue it, return, and let readiness (<see cref="SnapshotStore.HasPublished"/>)
/// report when it's live.</para>
/// <para>A process-lifetime singleton, shared by every app; it holds no per-request state. Disposing it stops the
/// writer: queued and in-flight publishes complete as <see cref="PublishStatus.Failed"/>, later ones fail at once.</para>
/// </summary>
public sealed partial class SnapshotPublisher : IAsyncDisposable
{
    private static readonly PublishResult Stopped = PublishResult.Fail(PublishStatus.Failed, "The publisher has stopped.");
    private readonly CancellationTokenSource _stopping = new();
    private Task _worker = Task.CompletedTask;
    private bool _disposed;
    private readonly Lock _lock = new();
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SnapshotPublisherOptions _options;
    private Pending? _queued;
    private bool _workerRunning;
    private Attachment? _attachment;
    private long _attempt;

    // What YARP is known to run; null when unknown (after a timeout). Equal content skips the YARP round trip.
    private YarpConfig? _yarpApplied = YarpConfig.Empty;

    public SnapshotPublisher(SnapshotStore store, ILoggerFactory? loggerFactory = null, TimeProvider? timeProvider = null, SnapshotPublisherOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        Store = store;
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<SnapshotPublisher>();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = options ?? new SnapshotPublisherOptions();
    }

    public SnapshotStore Store { get; }

    /// <summary>Registered as the public app's <see cref="IProxyConfigProvider"/> by <c>AddPaduraksaProxy</c>.</summary>
    internal SnapshotProxyConfigProvider ConfigProvider { get; } = new();

    /// <summary>Registered as one of the public app's <see cref="IConfigChangeListener"/>s by <c>AddPaduraksaProxy</c>.</summary>
    internal ConfigApplyListener ApplyListener { get; } = new();

    public bool IsAttached
    {
        get
        {
            lock (_lock)
            {
                return _attachment is not null;
            }
        }
    }

    /// <summary>
    /// Queues <paramref name="candidate"/> (an unbound snapshot from the compiler) and completes when it's live, failed,
    /// or superseded. Never throws for a bad candidate: the result says what happened, and on any failure the
    /// previous snapshot stays live. <paramref name="cancellationToken"/> only stops the wait, not the publish.
    /// </summary>
    public Task<PublishResult> PublishAsync(RuntimeSnapshot candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Generation != 0 || candidate.YarpEndpoints.Length != 0)
        {
            throw new ArgumentException("Publish an unbound candidate (generation 0, no endpoints); the publisher binds it.", nameof(candidate));
        }

        Pending pending = new(candidate);
        lock (_lock)
        {
            _queued?.Complete(PublishResult.Superseded);
            _queued = null;
            if (_disposed)
            {
                pending.Complete(Stopped);
            }
            else if (_attachment is null && candidate.Yarp.IsEmpty)
            {
                // Nothing for YARP to build. YARP's initial load will read this (empty) config.
                ConfigProvider.Update(YarpConfig.Empty, NextRevisionId(candidate));
                pending.Complete(Swap(candidate, []));
            }
            else
            {
                _queued = pending;
                StartWorkerIfNeeded();
            }
        }

        return cancellationToken.CanBeCanceled ? pending.Task.WaitAsync(cancellationToken) : pending.Task;
    }

    /// <summary>
    /// Runs YARP's <see cref="IConfigValidator"/> over the candidate's routes and clusters, plus a route → cluster
    /// reference check (dry-run, PLAN §6.1 step 4). Needs YARP: before <see cref="Attach"/> it reports exactly that.
    /// </summary>
    public async Task<IReadOnlyList<string>> ValidateAsync(RuntimeSnapshot candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        IConfigValidator? validator;
        lock (_lock)
        {
            validator = _attachment?.Validator;
        }

        return validator is null
            ? ["YARP isn't running yet, so the YARP config can't be validated."]
            : await ValidateCoreAsync(validator, candidate.Yarp).ConfigureAwait(false);
    }

    /// <summary>
    /// Connects the publisher to the public app's YARP. Called once, from the endpoint setup:
    /// <paramref name="mapReverseProxy"/> must call <c>MapReverseProxy</c> (whose initial load reads
    /// <see cref="ConfigProvider"/>) and return its convention builder and the <see cref="EndpointDataSource"/> it
    /// added. A candidate queued before this point is validated and handed to that initial load, then published.
    /// </summary>
    /// <returns>The convention builder <paramref name="mapReverseProxy"/> returned.</returns>
    public ReverseProxyConventionBuilder Attach(
        IConfigValidator validator,
        Func<(ReverseProxyConventionBuilder Builder, EndpointDataSource Endpoints)> mapReverseProxy)
    {
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(mapReverseProxy);

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_attachment is not null)
            {
                throw new InvalidOperationException("The publisher is already attached to a YARP instance.");
            }

            Pending? pending = _queued;
            _queued = null;
            if (pending is not null)
            {
                // Startup is synchronous here (YARP's own initial load blocks the same way).
                IReadOnlyList<string> errors = ValidateCoreAsync(validator, pending.Candidate.Yarp).GetAwaiter().GetResult();
                if (errors.Count > 0)
                {
                    LogInvalid(_logger, pending.Candidate.Revision, string.Join("; ", errors));
                    pending.Complete(new PublishResult(PublishStatus.Invalid, 0, errors));
                    pending = null;
                }
                else
                {
                    ConfigProvider.Update(pending.Candidate.Yarp, NextRevisionId(pending.Candidate));
                }
            }

            ReverseProxyConventionBuilder builder;
            EndpointDataSource endpoints;
            try
            {
                (builder, endpoints) = mapReverseProxy();
            }
            catch (Exception ex)
            {
                pending?.Complete(PublishResult.Fail(PublishStatus.Rejected, ex.Message));
                throw;
            }

            _attachment = new Attachment(validator, endpoints);
            _yarpApplied = ConfigProvider.CurrentContent;
            if (pending is not null)
            {
                pending.Complete(TryCaptureEndpoints(endpoints, pending.Candidate, out Endpoint[] captured, out string? problem)
                    ? Swap(pending.Candidate, captured)
                    : PublishResult.Fail(PublishStatus.Failed, problem!));
            }

            StartWorkerIfNeeded();
            return builder;
        }
    }

    /// <summary>
    /// Stops the writer: fails anything queued, cancels a publish waiting on YARP, and waits for the worker to finish.
    /// The live snapshot stays in the store. Idempotent.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Task worker;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _queued?.Complete(Stopped);
            _queued = null;
            worker = _worker;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        await worker.ConfigureAwait(false);
        _stopping.Dispose();
    }

    /// <summary>Callers hold <see cref="_lock"/>.</summary>
    private void StartWorkerIfNeeded()
    {
        if (_queued is not null && _attachment is not null && !_workerRunning && !_disposed)
        {
            _workerRunning = true;
            _worker = Task.Run(RunWorkerAsync, CancellationToken.None);
        }
    }

    private async Task RunWorkerAsync()
    {
        while (true)
        {
            Pending? next;
            Attachment attachment;
            lock (_lock)
            {
                next = _queued;
                _queued = null;
                if (next is null)
                {
                    _workerRunning = false;
                    return;
                }

                attachment = _attachment!;
            }

            PublishResult result;
            try
            {
                result = await PublishAttachedAsync(attachment, next.Candidate).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                result = Stopped;
            }
#pragma warning disable CA1031 // One failed publish must not stop the writer; the caller gets the error.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogPublishFailed(_logger, next.Candidate.Revision, ex);
                result = PublishResult.Fail(PublishStatus.Failed, ex.Message);
            }

            next.Complete(result);
        }
    }

    private async Task<PublishResult> PublishAttachedAsync(Attachment attachment, RuntimeSnapshot candidate)
    {
        IReadOnlyList<string> errors = await ValidateCoreAsync(attachment.Validator, candidate.Yarp).ConfigureAwait(false);
        if (errors.Count > 0)
        {
            LogInvalid(_logger, candidate.Revision, string.Join("; ", errors));
            return new PublishResult(PublishStatus.Invalid, 0, errors);
        }

        if (_yarpApplied is null || !_yarpApplied.ContentEquals(candidate.Yarp))
        {
            PublishResult? failure = await ApplyToYarpAsync(candidate.Yarp, NextRevisionId(candidate)).ConfigureAwait(false);
            if (failure is not null)
            {
                await RestoreYarpAsync().ConfigureAwait(false);
                return failure;
            }
        }

        // YARP has signalled; its endpoints normally match already (spike S2: 140 of 140). Allow a short grace.
        long started = _timeProvider.GetTimestamp();
        Endpoint[] endpoints;
        string? problem;
        while (!TryCaptureEndpoints(attachment.Endpoints, candidate, out endpoints, out problem))
        {
            if (_timeProvider.GetElapsedTime(started) >= _options.EndpointWait)
            {
                LogEndpointsMismatch(_logger, candidate.Revision, problem!);
                return PublishResult.Fail(PublishStatus.Failed, problem!);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(5), _timeProvider, _stopping.Token).ConfigureAwait(false);
        }

        lock (_lock)
        {
            return Swap(candidate, endpoints);
        }
    }

    /// <summary>Steps 6a/6b. Returns <see langword="null"/> when YARP applied the config.</summary>
    private async Task<PublishResult?> ApplyToYarpAsync(YarpConfig content, string revisionId)
    {
        Task<Exception?> outcome = ApplyListener.Expect(revisionId);
        ConfigProvider.Update(content, revisionId);
        Exception? rejected;
        try
        {
            rejected = await outcome.WaitAsync(_options.ApplyTimeout, _timeProvider, _stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopping: YARP's outcome no longer matters, and it's unknown.
            ApplyListener.Forget(revisionId);
            _yarpApplied = null;
            throw;
        }
        catch (TimeoutException)
        {
            ApplyListener.Forget(revisionId);
            _yarpApplied = null;
            LogApplyTimedOut(_logger, revisionId, _options.ApplyTimeout);
            return PublishResult.Fail(PublishStatus.TimedOut, $"YARP didn't apply revision {revisionId} within {_options.ApplyTimeout}.");
        }

        if (rejected is not null)
        {
            LogRejected(_logger, revisionId, rejected);
            return PublishResult.Fail(PublishStatus.Rejected, $"YARP rejected revision {revisionId}: {rejected.Message}");
        }

        _yarpApplied = content;
        return null;
    }

    /// <summary>
    /// After a rejection the provider still holds the rejected config while YARP runs the previous one; put the
    /// previous one back so the two agree again. Best effort: a failure leaves YARP's state marked unknown.
    /// </summary>
    private async Task RestoreYarpAsync()
    {
        YarpConfig restore = Store.Current.Yarp;
        _yarpApplied = null;
        string revisionId = string.Create(CultureInfo.InvariantCulture, $"{Store.Current.Revision}#restore#{Interlocked.Increment(ref _attempt)}");
        PublishResult? failure = await ApplyToYarpAsync(restore, revisionId).ConfigureAwait(false);
        if (failure is not null)
        {
            LogRestoreFailed(_logger, revisionId, string.Join("; ", failure.Errors));
        }
    }

    /// <summary>Step 6c: one endpoint per slot, taken from YARP's data source by route ID and checked by value.</summary>
    private static bool TryCaptureEndpoints(EndpointDataSource source, RuntimeSnapshot candidate, out Endpoint[] endpoints, out string? problem)
    {
        endpoints = new Endpoint[candidate.RouteIdBySlot.Length];
        problem = null;
        if (endpoints.Length == 0)
        {
            return true;
        }

        Dictionary<string, RouteConfig> wanted = new(candidate.Yarp.Routes.Count, StringComparer.OrdinalIgnoreCase);
        foreach (RouteConfig route in candidate.Yarp.Routes)
        {
            wanted[route.RouteId] = route;
        }

        Dictionary<string, Endpoint> live = new(StringComparer.OrdinalIgnoreCase);
        foreach (Endpoint endpoint in source.Endpoints)
        {
            if (endpoint.Metadata.GetMetadata<RouteModel>() is { } model
                && wanted.TryGetValue(model.Config.RouteId, out RouteConfig? expected)
                && expected.Equals(model.Config))
            {
                live[model.Config.RouteId] = endpoint;
            }
        }

        for (int slot = 0; slot < endpoints.Length; slot++)
        {
            string routeId = candidate.RouteIdBySlot[slot];
            if (!live.TryGetValue(routeId, out Endpoint? endpoint))
            {
                problem = $"YARP has no endpoint for route '{routeId}' matching the published config.";
                return false;
            }

            endpoints[slot] = endpoint;
        }

        return true;
    }

    /// <summary>Step 6d. Callers hold <see cref="_lock"/>, which makes the generation assignment single-writer.</summary>
    private PublishResult Swap(RuntimeSnapshot candidate, Endpoint[] endpoints)
    {
        long generation = Store.Current.Generation + 1;
        Store.Publish(candidate.Bind(generation, endpoints));
        LogPublished(_logger, generation, candidate.Revision, candidate.HostsByIndex.Length, candidate.RouteIdBySlot.Length);
        return PublishResult.Published(generation);
    }

    private static async Task<IReadOnlyList<string>> ValidateCoreAsync(IConfigValidator validator, YarpConfig config)
    {
        List<string> errors = [];
        HashSet<string> clusterIds = new(StringComparer.OrdinalIgnoreCase);
        foreach (ClusterConfig cluster in config.Clusters)
        {
            if (!clusterIds.Add(cluster.ClusterId))
            {
                errors.Add($"cluster '{cluster.ClusterId}': duplicate cluster ID");
            }

            foreach (Exception error in await validator.ValidateClusterAsync(cluster).ConfigureAwait(false))
            {
                errors.Add($"cluster '{cluster.ClusterId}': {error.Message}");
            }
        }

        HashSet<string> routeIds = new(StringComparer.OrdinalIgnoreCase);
        foreach (RouteConfig route in config.Routes)
        {
            if (!routeIds.Add(route.RouteId))
            {
                errors.Add($"route '{route.RouteId}': duplicate route ID");
            }

            if (route.ClusterId is not null && !clusterIds.Contains(route.ClusterId))
            {
                errors.Add($"route '{route.RouteId}': cluster '{route.ClusterId}' doesn't exist");
            }

            foreach (Exception error in await validator.ValidateRouteAsync(route).ConfigureAwait(false))
            {
                errors.Add($"route '{route.RouteId}': {error.Message}");
            }
        }

        return errors;
    }

    private string NextRevisionId(RuntimeSnapshot candidate) =>
        string.Create(CultureInfo.InvariantCulture, $"{candidate.Revision}#{Interlocked.Increment(ref _attempt)}");

    [LoggerMessage(Level = LogLevel.Information, Message = "Published snapshot generation {Generation} (revision {Revision}, {Hosts} hosts, {Routes} routes)")]
    private static partial void LogPublished(ILogger logger, long generation, string revision, int hosts, int routes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Snapshot for revision {Revision} failed YARP validation: {Errors}")]
    private static partial void LogInvalid(ILogger logger, string revision, string errors);

    [LoggerMessage(Level = LogLevel.Error, Message = "YARP rejected config {RevisionId}; the previous snapshot stays live")]
    private static partial void LogRejected(ILogger logger, string revisionId, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "YARP didn't apply config {RevisionId} within {Timeout}; the previous snapshot stays live")]
    private static partial void LogApplyTimedOut(ILogger logger, string revisionId, TimeSpan timeout);

    [LoggerMessage(Level = LogLevel.Error, Message = "Restoring YARP's previous config ({RevisionId}) failed: {Error}")]
    private static partial void LogRestoreFailed(ILogger logger, string revisionId, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "YARP's endpoints don't match the snapshot for revision {Revision}: {Problem}")]
    private static partial void LogEndpointsMismatch(ILogger logger, string revision, string problem);

    [LoggerMessage(Level = LogLevel.Error, Message = "Publishing the snapshot for revision {Revision} failed")]
    private static partial void LogPublishFailed(ILogger logger, string revision, Exception exception);

    private sealed record Attachment(IConfigValidator Validator, EndpointDataSource Endpoints);

    private sealed class Pending(RuntimeSnapshot candidate)
    {
        private readonly TaskCompletionSource<PublishResult> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RuntimeSnapshot Candidate { get; } = candidate;

        public Task<PublishResult> Task => _done.Task;

        public void Complete(PublishResult result) => _done.TrySetResult(result);
    }
}
