namespace Paduraksa.Mcp;

/// <summary>
/// The children a server keeps (PLAN §11.2). Lazy: none until the first call, which pays a cold start once. Warm while in use: a child that finished a run waits
/// for the next. At most <see cref="SandboxLimits.MaxChildren"/> at once, spawned on demand, one run at a time in each. A child is despawned after
/// <c>idleTimeout</c> without a run, and replaced after a kill or after <see cref="SandboxLimits.MaxRunsPerChild"/> runs. Disposing the pool kills every child.
/// </summary>
public sealed class SandboxProcessPool : IAsyncDisposable
{
    private readonly SandboxChildCommand _command;
    private readonly SandboxLimits _limits;
    private readonly TimeSpan _idleTimeout;
    private readonly string _specJson;
    private readonly TimeProvider _time;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _slots;
    private readonly Lock _gate = new();
    private readonly Stack<ChildProcess> _idle = new();
    private readonly List<ChildProcess> _all = [];
    private readonly CancellationTokenSource _disposing = new();
    private ChildSpawner? _spawner;
    private ChildCgroup? _cgroup;
    private ITimer? _sweeper;

    /// <summary>A fresh random run id for each call; tests replace it, to play the child's side by hand.</summary>
    internal Func<string> RunIds { get; set; } = () => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));
    private bool _probed;
    private bool _hardeningReported;
    private bool _disposed;

    public SandboxProcessPool(SandboxChildCommand command, SandboxLimits limits, TimeSpan idleTimeout, string specJson, TimeProvider? time = null, Action<string>? log = null)
    {
        _command = command;
        _limits = limits;
        _idleTimeout = idleTimeout;
        _specJson = specJson;
        _time = time ?? TimeProvider.System;
        _log = log ?? (_ => { });
        _slots = new SemaphoreSlim(limits.MaxChildren);
    }

    /// <summary>Children alive now, idle or running.</summary>
    public int ChildCount
    {
        get
        {
            lock (_gate)
            {
                return _all.Count;
            }
        }
    }

    /// <summary>The process IDs of the children alive now (tests and the measurements).</summary>
    public IReadOnlyList<int> ChildProcessIds
    {
        get
        {
            lock (_gate)
            {
                return [.. _all.Select(c => c.Pid)];
            }
        }
    }

    public async Task<SandboxOutcome> RunAsync(string tool, string code, RequestPolicy policy, IApiDispatcher dispatcher, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (code.Length > _limits.MaxCodeChars)
        {
            return SandboxOutcome.Error($"The code is {code.Length:N0} characters; the limit is {_limits.MaxCodeChars:N0}. Send less code.");
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            return SandboxOutcome.Error("The code is empty. Send an async arrow function, e.g. async () => { ... }.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposing.Token);
        if (!await _slots.WaitAsync(_limits.AcquireTimeout, linked.Token).ConfigureAwait(false))
        {
            return SandboxOutcome.Error("Other scripts are running; try again in a moment.");
        }

        ChildProcess? child = null;
        try
        {
            child = await AcquireAsync(linked.Token).ConfigureAwait(false);
            RequestBridge bridge = new(policy, dispatcher, _limits);
            var outcome = await child.RunAsync(tool, code, bridge, RunIds(), linked.Token).ConfigureAwait(false);
            Release(child);
            child = null;
            return outcome;
        }
        catch (SandboxProtocolException ex)
        {
            return SandboxOutcome.Error(ex.Message);
        }
        catch (OperationCanceledException) when (_disposing.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return SandboxOutcome.Error("The script was stopped: the server is shutting down.");
        }
        finally
        {
            if (child is not null)
            {
                await Discard(child).ConfigureAwait(false);
            }

            _slots.Release();
        }
    }

    private async Task<ChildProcess> AcquireAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            ChildProcess? candidate;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_idle.TryPop(out candidate))
                {
                    break;
                }
            }

            if (candidate.IsUsable && await candidate.StopIdleWatchAsync().ConfigureAwait(false) && candidate.IsUsable)
            {
                return candidate;
            }

            lock (_gate)
            {
                _all.Remove(candidate);
            }

            if (candidate.KillReason is { } reason)
            {
                _log($"A sandbox child was dropped ({reason}).");
            }

            _ = Task.Run(candidate.DisposeAsync);
        }

        lock (_gate)
        {
            if (!_probed)
            {
                _probed = true;
                _spawner = new ChildSpawner();
                var (cgroup, status) = ChildCgroup.Probe();
                _cgroup = cgroup;
                _log($"Sandbox children: cgroup limits: {status}.");
            }
        }

        var started = await ChildProcess.StartAsync(_command, _limits, _specJson, _spawner!, _cgroup, _log, _time, cancellationToken).ConfigureAwait(false);
        foreach (var line in started.Hardening.Where(l => l.Contains("not ", StringComparison.Ordinal)))
        {
            _log($"Sandbox child hardening: {line}.");
        }

        // Once, in full: an operator who reads the log should be able to see what a child holds without the failures having to say so.
        bool first;
        lock (_gate)
        {
            first = !_hardeningReported;
            _hardeningReported = true;
        }

        if (first)
        {
            _log($"Sandbox child hardening applied: {string.Join("; ", started.Hardening)}.");
        }

        lock (_gate)
        {
            if (_disposed)
            {
                _ = Task.Run(started.DisposeAsync);
                throw new OperationCanceledException();
            }

            _all.Add(started);
            _sweeper ??= _time.CreateTimer(Sweep, null, Period, Period);
        }

        return started;
    }

    /// <summary>How often the idle children are looked at: for time idle and for CPU used while idle, so never longer than a few seconds.</summary>
    private TimeSpan Period => TimeSpan.FromTicks(Math.Clamp((_idleTimeout / 4).Ticks, TimeSpan.FromSeconds(1).Ticks, TimeSpan.FromSeconds(5).Ticks));

    /// <summary>CPU a warm child may use between two looks without being asked to: its runtime settles after a run.</summary>
    private static readonly TimeSpan IdleCpuAllowance = TimeSpan.FromMilliseconds(400);

    private void Release(ChildProcess child)
    {
        child.LastUsed = _time.GetUtcNow();
        lock (_gate)
        {
            if (child.IsUsable && !_disposed)
            {
                child.StartIdleWatch();
                _idle.Push(child);
                return;
            }
        }

        _ = Discard(child);
    }

    private async Task Discard(ChildProcess child)
    {
        lock (_gate)
        {
            _all.Remove(child);
        }

        if (child.KillReason is { } reason)
        {
            _log($"A sandbox child was killed ({reason}); the next call starts a new one.");
        }

        await child.DisposeAsync().ConfigureAwait(false);
    }

    private void Sweep(object? state)
    {
        List<(ChildProcess Child, string Why)> gone = [];
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            List<ChildProcess> keep = [];
            foreach (var child in _idle.Reverse())
            {
                if (now - child.LastUsed >= _idleTimeout)
                {
                    gone.Add((child, "despawned after being idle"));
                }
                else if (!child.IsUsable)
                {
                    gone.Add((child, "no longer usable"));
                }
                else if (child.BurnedCpuWhileIdle(IdleCpuAllowance))
                {
                    child.Kill("it used CPU while it was idle");
                    gone.Add((child, "killed: it used CPU while it was idle"));
                }
                else
                {
                    keep.Add(child);
                }
            }

            _idle.Clear();
            foreach (var child in keep)
            {
                _idle.Push(child);
            }

            foreach (var (child, _) in gone)
            {
                _all.Remove(child);
            }
        }

        foreach (var (child, why) in gone)
        {
            _log($"A sandbox child was {why}.");
            _ = child.DisposeAsync().AsTask();
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<ChildProcess> children;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            children = [.. _all];
            _all.Clear();
            _idle.Clear();
        }

        await _disposing.CancelAsync().ConfigureAwait(false);
        if (_sweeper is not null)
        {
            await _sweeper.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var child in children)
        {
            child.Kill("the sandbox was shut down");
        }

        await Task.WhenAll(children.Select(c => c.DisposeAsync().AsTask())).ConfigureAwait(false);
        _spawner?.Dispose();
        _disposing.Dispose();
    }
}
