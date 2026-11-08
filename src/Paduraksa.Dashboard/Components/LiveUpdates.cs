using Paduraksa.Application.Commands;
using Paduraksa.Contracts.Commands;

namespace Paduraksa.Dashboard.Components;

/// <summary>
/// Keeps a page's list current without polling (PLAN §12: live by default): it listens to the operational change feed (certificates, ACME
/// accounts, DNS providers, orders) and, when asked, to the config change feed (a host that starts or stops using a certificate), and calls
/// the page's refresh once per burst of changes. The refresh runs through the page's own dispatcher (the callback is the page's
/// <c>InvokeAsync</c>); a failing refresh is the page's to report. Dispose it with the page.
/// </summary>
public sealed class LiveUpdates : IDisposable
{
    /// <summary>How long a burst of changes is gathered before one refresh (an issuance commits several in a row).</summary>
    public static readonly TimeSpan Coalesce = TimeSpan.FromMilliseconds(300);

    private readonly Func<Func<Task>, Task> _dispatch;
    private readonly Func<Task> _refresh;
    private readonly Func<OperationalChange, bool> _relevant;
    private readonly IDisposable _operational;
    private readonly ConfigChangeSubscription? _config;
    private readonly CancellationTokenSource _stopping = new();
    private int _pending;

    private LiveUpdates(ConfigService config, bool followConfig, Func<OperationalChange, bool> relevant, Func<Func<Task>, Task> dispatch, Func<Task> refresh)
    {
        _dispatch = dispatch;
        _refresh = refresh;
        _relevant = relevant;
        _operational = config.OperationalChanges.Subscribe(OnOperational);
        if (followConfig)
        {
            _config = config.Changes.Subscribe();
            _ = FollowConfigAsync(_config, _stopping.Token);
        }
    }

    /// <summary>
    /// Starts listening. <paramref name="dispatch"/> is the component's <c>InvokeAsync</c>; <paramref name="refresh"/> reloads what it
    /// shows. <paramref name="actionPrefixes"/> limits which operational changes count (audit action prefixes such as <c>certificate.</c>);
    /// none means all.
    /// </summary>
    public static LiveUpdates Start(
        ConfigService config, Func<Func<Task>, Task> dispatch, Func<Task> refresh, bool followConfig = false, params string[] actionPrefixes)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(dispatch);
        ArgumentNullException.ThrowIfNull(refresh);
        return new LiveUpdates(
            config,
            followConfig,
            change => actionPrefixes.Length == 0 || actionPrefixes.Any(prefix => change.Action.StartsWith(prefix, StringComparison.Ordinal)),
            dispatch,
            refresh);
    }

    public void Dispose()
    {
        _operational.Dispose();
        _config?.Dispose();
        _stopping.Cancel();
        _stopping.Dispose();
    }

    private void OnOperational(OperationalChange change)
    {
        if (_relevant(change))
        {
            Schedule();
        }
    }

    private async Task FollowConfigAsync(ConfigChangeSubscription subscription, CancellationToken stopping)
    {
        try
        {
            while (await subscription.Reader.WaitToReadAsync(stopping).ConfigureAwait(false))
            {
                while (subscription.Reader.TryRead(out _))
                {
                }

                Schedule();
            }
        }
        catch (OperationCanceledException)
        {
            // The page went away.
        }
    }

    /// <summary>One refresh per burst: the first change of a burst starts the wait, the rest ride along.</summary>
    private void Schedule()
    {
        if (Interlocked.Exchange(ref _pending, 1) == 1)
        {
            return;
        }

        CancellationToken stopping;
        try
        {
            stopping = _stopping.Token;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        _ = RunAsync(stopping);
    }

    private async Task RunAsync(CancellationToken stopping)
    {
        try
        {
            await Task.Delay(Coalesce, stopping).ConfigureAwait(false);
            Volatile.Write(ref _pending, 0);
            await _dispatch(_refresh).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Disposed meanwhile.
        }
        catch (ObjectDisposedException)
        {
            // The circuit is gone.
        }
    }
}
