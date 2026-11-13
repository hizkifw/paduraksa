using Paduraksa.Proxy.Metrics;

namespace Paduraksa.Proxy.Analytics;

/// <summary>One tracked key of a <see cref="HeavyHitters"/> sketch.</summary>
/// <param name="Key">The tracked text.</param>
/// <param name="Count">An upper bound on the weight the key had.</param>
/// <param name="Error">How far <paramref name="Count"/> may be above the truth: the weight is at least <c>Count - Error</c>.</param>
/// <param name="Requests">Requests seen since the key was tracked (a lower bound on its requests, equal to them while it was never evicted).</param>
/// <param name="Bytes">Declared response bytes seen since the key was tracked.</param>
/// <param name="ServerErrors">5xx answers seen since the key was tracked.</param>
/// <param name="Latency">The latency histogram (<see cref="LatencyBuckets"/>) of the requests seen since the key was tracked, or null when the sketch keeps none.</param>
public sealed record HeavyHitter(string Key, long Count, long Error, long Requests, long Bytes, long ServerErrors, long[]? Latency)
{
    /// <summary>The 95th-percentile latency in microseconds of the requests seen since the key was tracked; 0 without any.</summary>
    public long P95Micros => Latency is null ? 0 : LatencyBuckets.Quantile(Latency, 0.95);
}

/// <summary>
/// A Space-Saving heavy-hitters sketch (Metwally, Agrawal and El Abbadi): the most frequent keys of a stream in fixed memory,
/// <see cref="Capacity"/> keys at most (docs/design/observability.md "Traffic analytics"). A key that is tracked carries a count and an error: its weight is
/// between <c>Count - Error</c> and <c>Count</c>. A key that isn't tracked weighs at most <see cref="Floor"/>, so a key heavier than
/// the floor is always in the sketch.
/// </summary>
/// <remarks>
/// <para><b>Weighted.</b> <see cref="Add"/> takes a weight, so the same structure ranks paths by requests and by bytes.</para>
/// <para><b>Merging</b> (Agarwal et al., mergeable summaries) keeps the bound: a key missing from one side is taken at that side's
/// floor, both in its count and its error, so the merged count is still an upper bound and <c>Count - Error</c> still a lower one.
/// The merged floor is the sum of the floors, raised to the heaviest key dropped when the result has to be cut back to
/// <see cref="Capacity"/>. Merging a range's buckets therefore adds their errors: the bound on a key is the sum of its buckets'.</para>
/// <para><b>Payload.</b> Requests, bytes, server errors and the latency histogram are exact for what was seen <i>since the key was
/// tracked</i> and say nothing of what an evicted key had before. Not thread-safe: the drain owns the open ones.</para>
/// </remarks>
public sealed class HeavyHitters
{
    private const int MaxCapacity = 4_096;
    private const int MaxKeyBytes = 1_024;

    private readonly Dictionary<string, int> _index;
    private readonly string[] _keys;
    private readonly long[] _count;
    private readonly long[] _error;
    private readonly long[] _requests;
    private readonly long[] _bytes;
    private readonly long[] _serverErrors;
    private readonly long[]?[] _latency;
    private int _size;

    public HeavyHitters(int capacity, bool keepsLatency = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, MaxCapacity);
        Capacity = capacity;
        KeepsLatency = keepsLatency;
        _index = new Dictionary<string, int>(capacity, StringComparer.Ordinal);
        _keys = new string[capacity];
        _count = new long[capacity];
        _error = new long[capacity];
        _requests = new long[capacity];
        _bytes = new long[capacity];
        _serverErrors = new long[capacity];
        _latency = new long[]?[capacity];
    }

    public int Capacity { get; }

    public bool KeepsLatency { get; }

    /// <summary>Keys tracked now.</summary>
    public int Size => _size;

    /// <summary>The most any key that isn't tracked can weigh; 0 while nothing was ever dropped, when the sketch is exact.</summary>
    public long Floor { get; private set; }

    /// <summary>Counts one request for <paramref name="key"/>: <paramref name="weight"/> in the ranking, and the request in the payload.</summary>
    /// <param name="key">The text to count.</param>
    /// <param name="weight">What the key gains in the ranking: 1 to rank by requests, the response size to rank by bytes.</param>
    /// <param name="responseBytes">The declared response size, or a negative number when unknown.</param>
    /// <param name="serverError">Whether the answer was a 5xx.</param>
    /// <param name="latencyMicros">The request's total time, or a negative number when not timed.</param>
    public void Add(string key, long weight, long responseBytes, bool serverError, long latencyMicros)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (weight <= 0)
        {
            return;
        }

        if (_index.TryGetValue(key, out var slot))
        {
            _count[slot] += weight;
        }
        else if (_size < Capacity)
        {
            slot = _size++;
            _keys[slot] = key;
            _index[key] = slot;
            _count[slot] = weight;
            Reset(slot);
        }
        else
        {
            slot = LowestSlot();
            Floor = _count[slot];
            _index.Remove(_keys[slot]);
            _keys[slot] = key;
            _index[key] = slot;
            _error[slot] = _count[slot];
            _count[slot] += weight;
            Reset(slot, keepError: true);
        }

        _requests[slot]++;
        if (responseBytes > 0)
        {
            _bytes[slot] += responseBytes;
        }

        if (serverError)
        {
            _serverErrors[slot]++;
        }

        if (KeepsLatency && latencyMicros >= 0)
        {
            (_latency[slot] ??= new long[LatencyBuckets.Count])[LatencyBuckets.IndexOf(latencyMicros)]++;
        }
    }

    /// <summary>Folds <paramref name="other"/> in, keeping the bound described on the type, and cuts back to <see cref="Capacity"/>.</summary>
    public void MergeFrom(HeavyHitters other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other._size == 0 && other.Floor == 0)
        {
            return;
        }

        if (TryMergeInPlace(other))
        {
            return;
        }

        Dictionary<string, Slot> merged = new(_size + other._size, StringComparer.Ordinal);
        for (var i = 0; i < _size; i++)
        {
            var theirs = other._index.TryGetValue(_keys[i], out var j) ? j : -1;
            merged[_keys[i]] = theirs >= 0
                ? Slot.Of(this, i).Plus(Slot.Of(other, theirs))
                : Slot.Of(this, i).WithFloor(other.Floor);
        }

        for (var j = 0; j < other._size; j++)
        {
            if (!_index.ContainsKey(other._keys[j]))
            {
                merged[other._keys[j]] = Slot.Of(other, j).WithFloor(Floor).Owned();
            }
        }

        var floor = Floor + other.Floor;
        List<KeyValuePair<string, Slot>> kept = new(merged);
        if (kept.Count > Capacity)
        {
            kept.Sort(static (a, b) => b.Value.Count != a.Value.Count ? b.Value.Count.CompareTo(a.Value.Count) : string.CompareOrdinal(a.Key, b.Key));
            floor = Math.Max(floor, kept[Capacity].Value.Count);
            kept.RemoveRange(Capacity, kept.Count - Capacity);
        }

        Clear();
        Floor = floor;
        foreach (var (key, slot) in kept)
        {
            var at = _size++;
            _keys[at] = key;
            _index[key] = at;
            _count[at] = slot.Count;
            _error[at] = slot.Error;
            _requests[at] = slot.Requests;
            _bytes[at] = slot.Bytes;
            _serverErrors[at] = slot.ServerErrors;
            _latency[at] = slot.Latency;
        }
    }

    // The common case of a long range's merge: the union still fits, so it is a pass over each side with nothing allocated, where the
    // general merge below builds a table of the union and sorts it. Same arithmetic: a key one side lacks is taken at that side's floor.
    private bool TryMergeInPlace(HeavyHitters other)
    {
        var added = 0;
        for (var j = 0; j < other._size; j++)
        {
            if (!_index.ContainsKey(other._keys[j]))
            {
                added++;
            }
        }

        if (_size + added > Capacity)
        {
            return false;
        }

        var ownFloor = Floor;
        var originalSize = _size;
        for (var i = 0; i < originalSize; i++)
        {
            if (other._index.TryGetValue(_keys[i], out var j))
            {
                _count[i] += other._count[j];
                _error[i] += other._error[j];
                _requests[i] += other._requests[j];
                _bytes[i] += other._bytes[j];
                _serverErrors[i] += other._serverErrors[j];
                if (other._latency[j] is { } theirs)
                {
                    var mine = _latency[i] ??= new long[LatencyBuckets.Count];
                    for (var b = 0; b < mine.Length; b++)
                    {
                        mine[b] += theirs[b];
                    }
                }
            }
            else
            {
                _count[i] += other.Floor;
                _error[i] += other.Floor;
            }
        }

        for (var j = 0; j < other._size; j++)
        {
            if (_index.ContainsKey(other._keys[j]))
            {
                continue;
            }

            var at = _size++;
            _keys[at] = other._keys[j];
            _index[_keys[at]] = at;
            _count[at] = other._count[j] + ownFloor;
            _error[at] = other._error[j] + ownFloor;
            _requests[at] = other._requests[j];
            _bytes[at] = other._bytes[j];
            _serverErrors[at] = other._serverErrors[j];
            _latency[at] = (long[]?)other._latency[j]?.Clone();
        }

        Floor = ownFloor + other.Floor;
        return true;
    }

    /// <summary>The heaviest <paramref name="limit"/> keys, heaviest first (ties by key).</summary>
    public IReadOnlyList<HeavyHitter> Top(int limit)
    {
        List<HeavyHitter> all = new(_size);
        for (var i = 0; i < _size; i++)
        {
            all.Add(new HeavyHitter(_keys[i], _count[i], _error[i], _requests[i], _bytes[i], _serverErrors[i], _latency[i]));
        }

        all.Sort(static (a, b) => b.Count != a.Count ? b.Count.CompareTo(a.Count) : string.CompareOrdinal(a.Key, b.Key));
        return limit < all.Count ? all.GetRange(0, limit) : all;
    }

    public HeavyHitters Clone()
    {
        HeavyHitters copy = new(Capacity, KeepsLatency);
        copy.CopyFrom(this);
        return copy;
    }

    internal void Write(ByteWriter destination)
    {
        destination.Varint(Capacity);
        destination.Add(KeepsLatency ? (byte)1 : (byte)0);
        destination.Varint(Floor);
        destination.Varint(_size);
        for (var i = 0; i < _size; i++)
        {
            destination.Text(_keys[i]);
            destination.Varint(_count[i]);
            destination.Varint(_error[i]);
            destination.Varint(_requests[i]);
            destination.Varint(_bytes[i]);
            destination.Varint(_serverErrors[i]);
            if (KeepsLatency)
            {
                var histogram = _latency[i];
                var used = 0;
                for (var b = 0; histogram is not null && b < histogram.Length; b++)
                {
                    if (histogram[b] != 0)
                    {
                        used++;
                    }
                }

                destination.Varint(used);
                for (var b = 0; histogram is not null && b < histogram.Length; b++)
                {
                    if (histogram[b] != 0)
                    {
                        destination.Varint(b);
                        destination.Varint(histogram[b]);
                    }
                }
            }
        }
    }

    internal static HeavyHitters? Read(ref ByteReader reader)
    {
        var capacity = reader.Count(MaxCapacity);
        var keepsLatency = reader.Byte() == 1;
        var floor = reader.Varint();
        var size = reader.Count(MaxCapacity);
        if (reader.Failed || capacity < 1 || size > capacity)
        {
            return null;
        }

        HeavyHitters sketch = new(capacity, keepsLatency) { Floor = floor };
        for (var i = 0; i < size; i++)
        {
            var key = reader.Text(MaxKeyBytes);
            sketch._keys[i] = key;
            sketch._count[i] = reader.Varint();
            sketch._error[i] = reader.Varint();
            sketch._requests[i] = reader.Varint();
            sketch._bytes[i] = reader.Varint();
            sketch._serverErrors[i] = reader.Varint();
            if (keepsLatency)
            {
                var used = reader.Count(LatencyBuckets.Count);
                if (used > 0)
                {
                    var histogram = new long[LatencyBuckets.Count];
                    for (var u = 0; u < used; u++)
                    {
                        var bucket = reader.Count(LatencyBuckets.Count - 1);
                        histogram[bucket] = reader.Varint();
                    }

                    sketch._latency[i] = histogram;
                }
            }

            if (reader.Failed || !sketch._index.TryAdd(key, i))
            {
                return null;
            }
        }

        sketch._size = size;
        return sketch;
    }

    private void CopyFrom(HeavyHitters other)
    {
        Floor = other.Floor;
        for (var i = 0; i < other._size; i++)
        {
            _keys[i] = other._keys[i];
            _index[_keys[i]] = i;
            _count[i] = other._count[i];
            _error[i] = other._error[i];
            _requests[i] = other._requests[i];
            _bytes[i] = other._bytes[i];
            _serverErrors[i] = other._serverErrors[i];
            _latency[i] = (long[]?)other._latency[i]?.Clone();
        }

        _size = other._size;
    }

    private void Clear()
    {
        _index.Clear();
        Array.Clear(_keys, 0, _size);
        Array.Clear(_latency, 0, _size);
        _size = 0;
        Floor = 0;
    }

    private void Reset(int slot, bool keepError = false)
    {
        if (!keepError)
        {
            _error[slot] = 0;
        }

        _requests[slot] = 0;
        _bytes[slot] = 0;
        _serverErrors[slot] = 0;
        _latency[slot] = null;
    }

    private int LowestSlot()
    {
        var lowest = 0;
        for (var i = 1; i < _size; i++)
        {
            if (_count[i] < _count[lowest])
            {
                lowest = i;
            }
        }

        return lowest;
    }

    private readonly record struct Slot(long Count, long Error, long Requests, long Bytes, long ServerErrors, long[]? Latency)
    {
        public static Slot Of(HeavyHitters sketch, int i) =>
            new(sketch._count[i], sketch._error[i], sketch._requests[i], sketch._bytes[i], sketch._serverErrors[i], sketch._latency[i]);

        public Slot Owned() => Latency is null ? this : this with { Latency = (long[])Latency.Clone() };

        public Slot WithFloor(long floor) => this with { Count = Count + floor, Error = Error + floor };

        public Slot Plus(Slot other)
        {
            long[]? latency = Latency;
            if (other.Latency is not null)
            {
                latency = Latency is null ? (long[])other.Latency.Clone() : (long[])Latency.Clone();
                if (Latency is not null)
                {
                    for (var i = 0; i < latency.Length; i++)
                    {
                        latency[i] += other.Latency[i];
                    }
                }
            }

            return new(Count + other.Count, Error + other.Error, Requests + other.Requests, Bytes + other.Bytes, ServerErrors + other.ServerErrors, latency);
        }
    }
}
