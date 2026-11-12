namespace Paduraksa.Proxy.Analytics;

/// <summary>
/// Counts occurrences of a small, mostly fixed set of labels (status codes, methods, browser versions) in fixed memory: at most
/// <see cref="Capacity"/> distinct labels, and whatever arrives after the table is full is counted together as <see cref="Other"/>.
/// Exact for every label it holds. Not thread-safe: the drain owns the open ones.
/// </summary>
public sealed class BoundedCounter
{
    private const int MaxLabelBytes = 512;

    private readonly Dictionary<string, long> _counts = new(StringComparer.Ordinal);

    public BoundedCounter(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
    }

    public int Capacity { get; }

    /// <summary>What was counted after the table was full.</summary>
    public long Other { get; private set; }

    public IReadOnlyDictionary<string, long> Counts => _counts;

    public long Total
    {
        get
        {
            var total = Other;
            foreach (var count in _counts.Values)
            {
                total += count;
            }

            return total;
        }
    }

    public void Add(string label, long amount = 1)
    {
        ArgumentNullException.ThrowIfNull(label);
        if (amount <= 0)
        {
            return;
        }

        ref var slot = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_counts, label);
        if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref slot))
        {
            slot += amount;
        }
        else if (_counts.Count < Capacity)
        {
            _counts[label] = amount;
        }
        else
        {
            Other += amount;
        }
    }

    public void MergeFrom(BoundedCounter other)
    {
        ArgumentNullException.ThrowIfNull(other);
        foreach (var (label, count) in other._counts)
        {
            Add(label, count);
        }

        Other += other.Other;
    }

    public BoundedCounter Clone()
    {
        BoundedCounter copy = new(Capacity);
        copy.MergeFrom(this);
        return copy;
    }

    /// <summary>The labels, most counted first (ties by label).</summary>
    public IReadOnlyList<KeyValuePair<string, long>> Ranked()
    {
        List<KeyValuePair<string, long>> all = new(_counts);
        all.Sort(static (a, b) => b.Value != a.Value ? b.Value.CompareTo(a.Value) : string.CompareOrdinal(a.Key, b.Key));
        return all;
    }

    internal void Write(ByteWriter destination)
    {
        destination.Varint(Capacity);
        destination.Varint(Other);
        destination.Varint(_counts.Count);
        foreach (var (label, count) in _counts)
        {
            destination.Text(label);
            destination.Varint(count);
        }
    }

    internal static BoundedCounter? Read(ref ByteReader reader)
    {
        var capacity = reader.Count(4_096);
        var other = reader.Varint();
        var size = reader.Count(4_096);
        if (reader.Failed || capacity < 1 || size > capacity)
        {
            return null;
        }

        BoundedCounter counter = new(capacity) { Other = other };
        for (var i = 0; i < size; i++)
        {
            var label = reader.Text(MaxLabelBytes);
            var count = reader.Varint();
            if (reader.Failed || !counter._counts.TryAdd(label, count))
            {
                return null;
            }
        }

        return counter;
    }
}
