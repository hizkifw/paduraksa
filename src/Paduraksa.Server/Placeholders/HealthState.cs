using Paduraksa.Application.Hosting;
using Paduraksa.Application.Issuance;

namespace Paduraksa.Server.Placeholders;

/// <summary>
/// PLACEHOLDER (M7): process-wide health flags behind <c>/healthz</c> and <c>/readyz</c> (PLAN §11.3).
/// Today it only knows whether all listeners are bound and whether shutdown has begun. The config store's state
/// (degraded or not) is <c>ConfigService.Status</c>; a degraded start is still ready (see <c>AdminApp</c>).
/// The vault needs no flag: the core host starts it before anything listens, and a vault that can't start stops the
/// process (PLAN §3, §8), so "listeners bound" already implies it.
/// </summary>
internal sealed class HealthState : IProcessReadiness
{
    private readonly TaskCompletionSource _boundSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _listenersBound;
    private volatile bool _stopping;

    /// <summary>Ready = every listener is bound and the process isn't draining.</summary>
    public bool IsReady => _listenersBound && !_stopping;

    public bool IsStopping => _stopping;

    /// <summary>Called by the host once both web apps have started.</summary>
    public void MarkListenersBound()
    {
        _listenersBound = true;
        _boundSignal.TrySetResult();
    }

    /// <summary>Called by the host as the very first shutdown step, so probes see "not ready" during the drain.</summary>
    public void MarkStopping() => _stopping = true;

    /// <inheritdoc />
    public Task WaitUntilReadyAsync(CancellationToken cancellationToken) => _boundSignal.Task.WaitAsync(cancellationToken);
}
