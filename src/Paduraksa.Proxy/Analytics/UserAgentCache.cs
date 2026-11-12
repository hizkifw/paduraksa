namespace Paduraksa.Proxy.Analytics;

/// <summary>
/// A bounded least-recently-used cache of classified user agents, keyed by the raw string: real traffic has a few hundred distinct
/// agents and a million requests, so almost every request is a hit, and a flood of random agents costs the table its size and no more.
/// Not thread-safe: the drain is its one user.
/// </summary>
public sealed class UserAgentCache
{
    /// <summary>Agents kept by default.</summary>
    public const int DefaultCapacity = 4_096;

    private readonly Dictionary<string, LinkedListNode<(string Agent, UserAgentInfo Info)>> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Agent, UserAgentInfo Info)> _recent = new();
    private readonly int _capacity;

    public UserAgentCache(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    public int Count => _entries.Count;

    /// <summary>
    /// The classification of <paramref name="userAgent"/>. The cache is keyed on the first <see cref="UserAgentClassifier.MaxLength"/> characters,
    /// which is all the classifier reads, so an agent padded to 32 KB pins no more than 1 KiB. When <paramref name="classify"/> is false (the drain
    /// is behind) a miss is not classified, nor cached: it is <see cref="UserAgentInfo.Unclassified"/>, so a flood of unique agents costs a lookup.
    /// </summary>
    public UserAgentInfo Get(string? userAgent, bool classify = true)
    {
        if (string.IsNullOrEmpty(userAgent))
        {
            return UserAgentClassifier.Classify(null);
        }

        var capped = userAgent.AsSpan(0, Math.Min(userAgent.Length, UserAgentClassifier.MaxLength));
        var lookup = _entries.GetAlternateLookup<ReadOnlySpan<char>>();
        if (lookup.TryGetValue(capped, out var node))
        {
            _recent.Remove(node);
            _recent.AddFirst(node);
            return node.Value.Info;
        }

        if (!classify)
        {
            return UserAgentInfo.Unclassified;
        }

        var key = capped.Length == userAgent.Length ? userAgent : capped.ToString();
        var info = UserAgentClassifier.Classify(key);
        if (_entries.Count >= _capacity)
        {
            var oldest = _recent.Last!;
            _recent.RemoveLast();
            _entries.Remove(oldest.Value.Agent);
        }

        _entries[key] = _recent.AddFirst((key, info));
        return info;
    }
}
