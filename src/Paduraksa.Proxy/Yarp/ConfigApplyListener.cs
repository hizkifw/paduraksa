using System.Collections.Concurrent;

using Yarp.ReverseProxy.Configuration;

namespace Paduraksa.Proxy.Yarp;

/// <summary>Turns YARP's <see cref="IConfigChangeListener"/> callbacks into one awaitable outcome per <see
/// cref="IProxyConfig.RevisionId"/>.</summary>
internal sealed class ConfigApplyListener : IConfigChangeListener
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Exception?>> _waiters = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers interest in <paramref name="revisionId"/> <b>before</b> the config is handed to YARP (it may apply
    /// inline). Completes with <see langword="null"/> when applied, or with YARP's exception when it was rejected.
    /// </summary>
    public Task<Exception?> Expect(string revisionId)
    {
        TaskCompletionSource<Exception?> waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiters[revisionId] = waiter;
        return waiter.Task;
    }

    /// <summary>Drops a waiter that will never be completed (e.g. after a timeout).</summary>
    public void Forget(string revisionId) => _waiters.TryRemove(revisionId, out _);

    public void ConfigurationLoadingFailed(IProxyConfigProvider configProvider, Exception exception)
    {
    }

    public void ConfigurationLoaded(IReadOnlyList<IProxyConfig> proxyConfigs)
    {
    }

    public void ConfigurationApplyingFailed(IReadOnlyList<IProxyConfig> proxyConfigs, Exception exception) => Complete(proxyConfigs, exception);

    public void ConfigurationApplied(IReadOnlyList<IProxyConfig> proxyConfigs) => Complete(proxyConfigs, null);

    private void Complete(IReadOnlyList<IProxyConfig> configs, Exception? error)
    {
        ArgumentNullException.ThrowIfNull(configs);
        foreach (IProxyConfig config in configs)
        {
            if (_waiters.TryRemove(config.RevisionId, out TaskCompletionSource<Exception?>? waiter))
            {
                waiter.TrySetResult(error);
            }
        }
    }
}
