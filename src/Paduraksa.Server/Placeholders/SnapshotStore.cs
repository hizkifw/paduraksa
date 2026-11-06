namespace Paduraksa.Server.Placeholders;

/// <summary>
/// PLACEHOLDER (M1): stands in for <c>Paduraksa.Proxy</c>'s <c>SnapshotStore</c> holding the immutable
/// <c>RuntimeSnapshot</c> (PLAN §6.2). Only the shape matters here: one process-wide instance, a
/// <see langword="volatile"/> reference that readers load lock-free, and a generation number that travels
/// with the snapshot so a reader can never pair snapshot N with generation N+1.
/// </summary>
internal sealed class SnapshotStore(TimeProvider timeProvider)
{
    private readonly Lock _publishLock = new();
    private volatile PlaceholderSnapshot? _current;

    /// <summary>The live snapshot, or <see langword="null"/> before the first publish.</summary>
    public PlaceholderSnapshot? Current => _current;

    /// <summary>Publishes a new (empty) snapshot. Writers are serialized; readers never block.</summary>
    public PlaceholderSnapshot Publish()
    {
        lock (_publishLock)
        {
            long next = (_current?.Generation ?? 0) + 1;
            PlaceholderSnapshot snapshot = new(next, timeProvider.GetUtcNow());
            _current = snapshot;
            return snapshot;
        }
    }
}

/// <summary>PLACEHOLDER (M1): replaced by the compiled <c>RuntimeSnapshot</c>.</summary>
internal sealed record PlaceholderSnapshot(long Generation, DateTimeOffset PublishedAt);
