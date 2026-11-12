using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Paduraksa.Mcp;

/// <summary>
/// One child process as the parent holds it (PLAN §11.2): the pipes, the watchdog of a run, and the kill. The parent is the guard: any breach of a limit, any
/// frame that is not what the protocol says, the child exiting or closing its pipe ends the run with a fixed message, kills the process tree, and takes this
/// child out of use. What the child printed on stderr is kept as a short tail for the log and never given to a caller.
/// </summary>
internal sealed class ChildProcess : IAsyncDisposable
{
    private const int StderrTail = 2048;

    /// <summary>Characters of stderr a child may write in its whole life.</summary>
    private const long StderrBudget = 1024 * 1024;

    private readonly Process _process;
    private readonly Stream _toChild;
    private readonly Stream _fromChild;
    private readonly SandboxLimits _limits;
    private readonly Action<string> _log;
    private readonly string? _cgroupPath;
    private readonly StringBuilder _stderr = new();
    private readonly Task _stderrDrain;
    private string? _killReason;
    private bool _recycle;
    private int _disposed;
    private CancellationTokenSource? _idleCts;
    private Task<int>? _idleRead;
    private readonly byte[] _idleByte = new byte[1];
    private long _stderrBytes;
    private TimeSpan _idleCpu;

    private ChildProcess(Process process, SandboxLimits limits, Action<string> log, string? cgroupPath)
    {
        _process = process;
        _toChild = process.StandardInput.BaseStream;
        _fromChild = process.StandardOutput.BaseStream;
        _limits = limits;
        _log = log;
        _cgroupPath = cgroupPath;
        Pid = process.Id;
        _stderrDrain = Task.Run(DrainStderr);
    }

    public int Pid { get; }

    public int Runs { get; private set; }

    public DateTimeOffset LastUsed { get; set; }

    public IReadOnlyList<string> Hardening { get; private set; } = [];

    /// <summary>False once the child has been killed or has exited: it takes no more runs.</summary>
    public bool IsUsable => _killReason is null && !_recycle && Volatile.Read(ref _disposed) == 0 && !_process.HasExited && Runs < _limits.MaxRunsPerChild && CpuLifetimeLeft();

    public string? KillReason => _killReason;

    public static async Task<ChildProcess> StartAsync(
        SandboxChildCommand command, SandboxLimits limits, string specJson, ChildSpawner spawner, ChildCgroup? cgroup, Action<string> log, TimeProvider time, CancellationToken cancellationToken)
    {
        ProcessStartInfo info = new(command.FileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = "/",
        };
        foreach (var argument in command.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        // The child gets almost nothing of the server's environment: no key, no token, no data directory.
        info.Environment.Clear();
        foreach (var name in new[] { "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_ARM64", "PATH", "LD_LIBRARY_PATH" })
        {
            if (Environment.GetEnvironmentVariable(name) is { } value)
            {
                info.Environment[name] = value;
            }
        }

        info.Environment["DOTNET_GCHeapHardLimit"] = "0x" + limits.ChildHeapBytes.ToString("x", CultureInfo.InvariantCulture);
        info.Environment["DOTNET_gcServer"] = "0";
        info.Environment["DOTNET_gcConcurrent"] = "0";
        info.Environment["DOTNET_TieredPGO"] = "0";
        info.Environment["DOTNET_TieredCompilation"] = "1";
        info.Environment["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "1";
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        info.Environment["DOTNET_NOLOGO"] = "1";
        info.Environment["PADURAKSA_MCP_CPU_SECONDS"] = limits.ChildCpuLifetimeSeconds.ToString(CultureInfo.InvariantCulture);

        var process = await spawner.StartAsync(info).ConfigureAwait(false);
        var cgroupPath = cgroup?.Place(process.Id, limits.ChildHeapBytes * 2, 100, 512);
        ChildProcess child = new(process, limits, log, cgroupPath) { LastUsed = time.GetUtcNow() };
        try
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startup.CancelAfter(limits.StartupTimeout);
            var ready = await FrameCodec.ReadAsync(child._fromChild, FrameCodec.MaxFromChild, startup.Token).ConfigureAwait(false)
                ?? throw new SandboxProtocolException("The child exited before it was ready.");
            using var message = Messages.Parse(ready);
            if (Messages.TypeOf(message) != "ready")
            {
                throw new SandboxProtocolException("The child's first frame is not 'ready'.");
            }

            child.Hardening = [.. message.RootElement.GetProperty("hardening").EnumerateArray().Select(e => e.GetString() ?? string.Empty).Take(16)];
            await FrameCodec.WriteAsync(child._toChild, Messages.Build(w =>
            {
                w.WriteString("t", "spec");
                w.WriteString("spec", specJson);
            }), FrameCodec.MaxToChild, startup.Token).ConfigureAwait(false);
            return child;
        }
        catch (Exception ex)
        {
            // Every way out kills what was started, a caller who gave up while it started included: the process is not tracked by anything else yet.
            child.Kill("it did not start");
            await child.DisposeAsync().ConfigureAwait(false);
            if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            log($"A sandbox child did not start: {ex.GetType().Name}. Its stderr: {child.StderrText()}");
            throw new SandboxProtocolException("The sandbox could not start.");
        }
    }

    public async Task<SandboxOutcome> RunAsync(string tool, string code, RequestBridge bridge, string runId, CancellationToken caller)
    {
        Runs++;
        if (!await StopIdleWatchAsync().ConfigureAwait(false))
        {
            Kill("it sent data while it was idle");
            return SandboxOutcome.Error($"The script was stopped: {_killReason}.", 0);
        }

        RunState state = new(runId);
        var cpuAtStart = ProcStats.CpuTime(Pid) ?? TimeSpan.Zero;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(caller);
        var watchdog = Task.Run(() => WatchAsync(state.Clock, cpuAtStart, stop.Token), stop.Token);
        try
        {
            await FrameCodec.WriteAsync(_toChild, RunProtocol.RunFrame(tool, code, _limits, runId), FrameCodec.MaxToChild, stop.Token).ConfigureAwait(false);
            var exchange = await RunProtocol.ExchangeAsync(_fromChild, _toChild, bridge, _limits, state, _log, stop.Token).ConfigureAwait(false);
            _recycle |= exchange.Recycle;
            return exchange.Outcome;
        }
        catch (OperationCanceledException) when (caller.IsCancellationRequested)
        {
            Kill("the caller went away");
            throw;
        }
        catch (RunBreachException ex)
        {
            Kill(ex.Reason);
            return SandboxOutcome.Error($"The script was stopped: {_killReason}.", state.Requests);
        }
        catch (Exception ex) when (ex is SandboxProtocolException or IOException or JsonException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            if (_killReason is null)
            {
                Kill(ex is SandboxProtocolException ? "it broke the protocol" : "the sandbox process ended");
                if (_process.HasExited)
                {
                    _log($"A sandbox child stopped unexpectedly ({ex.GetType().Name}). Its stderr: {StderrText()}");
                }
            }

            return SandboxOutcome.Error($"The script was stopped: {_killReason}.", state.Requests);
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            try
            {
                await watchdog.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // the run ended before the watchdog's first tick
            }
        }
    }

    private async Task WatchAsync(RunClock clock, TimeSpan cpuAtStart, CancellationToken cancellationToken)
    {
        var total = Stopwatch.StartNew();
        try
        {
            using PeriodicTimer timer = new(_limits.PollInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_process.HasExited)
                {
                    return;
                }

                if (clock.Active > _limits.ScriptTime)
                {
                    Kill($"it ran for more than {_limits.ScriptTime.TotalSeconds:0.#} s");
                }
                else if (total.Elapsed > _limits.TotalTime)
                {
                    Kill($"the call took more than {_limits.TotalTime.TotalSeconds:0.#} s in all");
                }
                else if (ProcStats.ResidentBytes(Pid) is var rss and > 0 && rss > _limits.ChildRssBytes)
                {
                    Kill($"it used more than {_limits.ChildRssBytes / (1024 * 1024)} MiB of memory");
                }
                else if (ProcStats.CpuTime(Pid) is { } cpu && cpu - cpuAtStart > _limits.CpuTime)
                {
                    Kill($"it used more than {_limits.CpuTime.TotalSeconds:0.#} s of CPU time");
                }

                if (_killReason is not null)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // the run ended
        }
    }

    /// <summary>
    /// While the child waits in the pool the parent keeps one read pending on its stdout. A child has nothing to say between runs, so a byte, or the end of the
    /// stream, is a breach: the child is killed at once and never handed out. Frames queued while idle would otherwise be taken for answers to the next run.
    /// </summary>
    public void StartIdleWatch()
    {
        _idleCpu = ProcStats.CpuTime(Pid) ?? TimeSpan.Zero;
        CancellationTokenSource cts = new();
        var read = _fromChild.ReadAsync(_idleByte.AsMemory(), cts.Token).AsTask();
        _idleCts = cts;
        _idleRead = read;
        _ = read.ContinueWith(
            _ =>
            {
                if (!cts.IsCancellationRequested && _killReason is null)
                {
                    _log("A sandbox child sent data, or ended, while it was idle and was killed.");
                    Kill("it sent data while it was idle");
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>Whether the child used more than <paramref name="epsilon"/> of CPU since the last look while it waited for work (a spin, a loop that outlived its run).</summary>
    public bool BurnedCpuWhileIdle(TimeSpan epsilon)
    {
        var now = ProcStats.CpuTime(Pid) ?? _idleCpu;
        var burned = now - _idleCpu > epsilon;
        _idleCpu = now;
        return burned;
    }

    /// <returns><c>false</c> when the child said anything, or ended, while it was idle.</returns>
    public async Task<bool> StopIdleWatchAsync()
    {
        var cts = _idleCts;
        var read = _idleRead;
        _idleCts = null;
        _idleRead = null;
        if (cts is null || read is null)
        {
            return _killReason is null;
        }

        await cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await read.ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException)
        {
            return _killReason is null;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return false;
        }
        finally
        {
            cts.Dispose();
        }
    }

    /// <summary>Kills the process tree; the first reason is the one the caller is told.</summary>
    public void Kill(string reason)
    {
        if (Interlocked.CompareExchange(ref _killReason, reason, null) is not null)
        {
            return;
        }

        try
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(2000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // already gone
        }

    }

    /// <summary>The child is asked to leave, and killed if it does not.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_killReason is null && !_process.HasExited)
        {
            Kill("the sandbox was shut down");
        }

        try
        {
            await _process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // not seen to end; the kill was sent
        }

        await _stderrDrain.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        _process.Dispose();
        if (_cgroupPath is not null)
        {
            ChildCgroup.Remove(_cgroupPath);
        }
    }

    private bool CpuLifetimeLeft() => (ProcStats.CpuTime(Pid) ?? TimeSpan.Zero).TotalSeconds < _limits.ChildCpuLifetimeSeconds * 0.5;

    private string StderrText()
    {
        lock (_stderr)
        {
            return _stderr.ToString();
        }
    }

    private async Task DrainStderr()
    {
        var buffer = new char[512];
        try
        {
            int read;
            while ((read = await _process.StandardError.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                lock (_stderr)
                {
                    _stderr.Append(buffer, 0, read);
                    if (_stderr.Length > StderrTail)
                    {
                        _stderr.Remove(0, _stderr.Length - StderrTail);
                    }
                }

                // A child that floods stderr costs the parent a core reading it; it has no business writing more than a few lines in its life.
                if ((_stderrBytes += read) > StderrBudget)
                {
                    _log($"A sandbox child wrote more than {StderrBudget / 1024} KiB to stderr and was killed. The end of it: {StderrText()}");
                    Kill("it wrote too much to stderr");
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // the process is gone
        }
    }
}
