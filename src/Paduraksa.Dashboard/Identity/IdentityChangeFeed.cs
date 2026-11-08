namespace Paduraksa.Dashboard.Identity;

/// <summary>
/// Tells every open dashboard page that an identity change was made through the dashboard (a user created or changed, a token issued or
/// revoked, a session ended), so lists follow without a timer. One per process. Changes made through the API or the CLI are not seen
/// here; a page shows them on its next load.
/// </summary>
public sealed class IdentityChangeFeed
{
    private readonly Lock _gate = new();
    private Func<Task>[] _watchers = [];

    /// <summary>Calls <paramref name="onChanged"/> after every change from now on, until the handle is disposed.</summary>
    public IDisposable Watch(Func<Task> onChanged)
    {
        ArgumentNullException.ThrowIfNull(onChanged);
        lock (_gate)
        {
            _watchers = [.. _watchers, onChanged];
        }

        return new Subscription(this, onChanged);
    }

    /// <summary>Announces a change. Watchers run in the background; one that fails doesn't stop the others.</summary>
    public void Publish()
    {
        foreach (Func<Task> watcher in _watchers)
        {
            _ = RunAsync(watcher);
        }
    }

    private static async Task RunAsync(Func<Task> watcher)
    {
        try
        {
            await watcher().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A page that failed to refresh keeps what it shows; its next load reads afresh.
        }
    }

    private void Remove(Func<Task> onChanged)
    {
        lock (_gate)
        {
            _watchers = [.. _watchers.Where(w => !ReferenceEquals(w, onChanged))];
        }
    }

    private sealed class Subscription(IdentityChangeFeed feed, Func<Task> onChanged) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                feed.Remove(onChanged);
            }
        }
    }
}
