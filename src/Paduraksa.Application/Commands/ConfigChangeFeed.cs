using System.Collections.Immutable;
using System.Threading.Channels;

using Paduraksa.Contracts.Commands;

namespace Paduraksa.Application.Commands;

/// <summary>
/// The change feed: one <see cref="ConfigCommitted"/> per committed revision, in commit order, to
/// every subscriber. Each subscriber reads its own bounded channel, so a slow one
/// never holds up the writer or the others: when it falls behind by <see cref="Capacity"/> notifications it loses the
/// oldest, and should re-read the current config when the sequence numbers it sees skip.
/// </summary>
/// <remarks>
/// To follow the config without missing a commit, subscribe <i>before</i> reading the current config
/// (<see cref="ConfigService.GetCurrentAsync"/>), then skip notifications up to the revision that read returned.
/// </remarks>
public sealed class ConfigChangeFeed
{
    /// <summary>How many unread notifications a subscriber keeps.</summary>
    public const int Capacity = 256;

    private readonly Lock _lock = new();
    private ImmutableArray<Channel<ConfigCommitted>> _subscribers = [];

    /// <summary>A new subscription, starting with the next commit. Dispose it to unsubscribe.</summary>
    public ConfigChangeSubscription Subscribe()
    {
        var channel = Channel.CreateBounded<ConfigCommitted>(
            new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        lock (_lock)
        {
            _subscribers = _subscribers.Add(channel);
        }

        return new ConfigChangeSubscription(channel.Reader, () => Unsubscribe(channel));
    }

    /// <summary>Hands <paramref name="committed"/> to every subscriber; never blocks. Called by the single writer.</summary>
    internal void Publish(ConfigCommitted committed)
    {
        ImmutableArray<Channel<ConfigCommitted>> subscribers;
        lock (_lock)
        {
            subscribers = _subscribers;
        }

        foreach (var channel in subscribers)
        {
            channel.Writer.TryWrite(committed);
        }
    }

    private void Unsubscribe(Channel<ConfigCommitted> channel)
    {
        lock (_lock)
        {
            _subscribers = _subscribers.Remove(channel);
        }

        channel.Writer.TryComplete();
    }
}

/// <summary>A subscription to the <see cref="ConfigChangeFeed"/>: read <see cref="Reader"/>, dispose to stop.</summary>
public sealed class ConfigChangeSubscription : IDisposable
{
    private Action? _unsubscribe;

    internal ConfigChangeSubscription(ChannelReader<ConfigCommitted> reader, Action unsubscribe)
    {
        Reader = reader;
        _unsubscribe = unsubscribe;
    }

    /// <summary>The notifications, in commit order; completes once the subscription is disposed.</summary>
    public ChannelReader<ConfigCommitted> Reader { get; }

    public void Dispose() => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
}
