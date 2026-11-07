namespace Paduraksa.Server.Placeholders;

/// <summary>
/// PLACEHOLDER (M7): process-wide health flags behind <c>/healthz</c> and <c>/readyz</c> (PLAN §11.3).
/// Today it only knows whether all listeners are bound and whether shutdown has begun. The config store's state
/// (degraded or not) is <c>ConfigService.Status</c>; a degraded start is still ready (see <c>AdminApp</c>).
/// TODO(M3): readiness includes "vault unlocked".
/// </summary>
internal sealed class HealthState
{
    private volatile bool _listenersBound;
    private volatile bool _stopping;

    /// <summary>Ready = every listener is bound and the process isn't draining.</summary>
    public bool IsReady => _listenersBound && !_stopping;

    public bool IsStopping => _stopping;

    /// <summary>Called by the host once both web apps have started.</summary>
    public void MarkListenersBound() => _listenersBound = true;

    /// <summary>Called by the host as the very first shutdown step, so probes see "not ready" during the drain.</summary>
    public void MarkStopping() => _stopping = true;
}
