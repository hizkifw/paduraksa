namespace Paduraksa.Proxy.Net;

/// <summary>
/// An immutable set of CIDR blocks (IPv4 and IPv6 mixed), built once on a cold path and queried with
/// <see cref="Contains"/> at 0 B and without locks.
/// </summary>
/// <remarks>
/// <para><b>Structure: merged, sorted, disjoint <c>[first, last]</c> ranges in two <see cref="UInt128"/> arrays,
/// searched with a binary search.</b> The builder sorts the blocks and merges overlapping and adjacent ones, so
/// duplicates and nested blocks normalise away and the set's size is the number of distinct runs. A lookup is
/// ⌈log₂ n⌉ + 1 comparisons over two contiguous arrays: S3 measured 7.7 ns for 2,000 CIDRs.</para>
/// <para>A binary radix trie (PLAN §7.3's first idea) would cost up to 128 dependent pointer (or index) hops per
/// lookup and several nodes per block; it only pays off for longest-prefix-match with per-prefix values, which
/// an allow/deny membership test doesn't need. If per-block values are ever needed (e.g. which rule matched),
/// keep this layout and add a parallel value array, splitting rather than merging overlaps.</para>
/// <para>IPv4 blocks live in the IPv4-mapped range (see <see cref="IpNetwork"/>).</para>
/// </remarks>
public sealed class CidrSet
{
    private readonly UInt128[] _firsts;
    private readonly UInt128[] _lasts;

    private CidrSet(UInt128[] firsts, UInt128[] lasts)
    {
        _firsts = firsts;
        _lasts = lasts;
    }

    /// <summary>The empty set: <see cref="Contains"/> is always false.</summary>
    public static CidrSet Empty { get; } = new([], []);

    /// <summary>Number of disjoint ranges after merging (not the number of blocks added).</summary>
    public int RangeCount => _firsts.Length;

    public bool IsEmpty => _firsts.Length == 0;

    /// <summary>Whether <paramref name="address"/> is inside any block. Allocation-free, thread-safe.</summary>
    public bool Contains(IpKey address)
    {
        var value = address.Value;
        var firsts = _firsts;
        int lo = 0, hi = firsts.Length - 1;

        // Find the last range whose first address is <= value.
        while (lo <= hi)
        {
            var mid = (int)((uint)(lo + hi) >> 1);
            if (firsts[mid] <= value)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return hi >= 0 && value <= _lasts[hi];
    }

    /// <summary>The merged ranges, for display and diagnostics (allocates).</summary>
    public IReadOnlyList<(IpKey First, IpKey Last)> GetRanges()
    {
        var ranges = new (IpKey, IpKey)[_firsts.Length];
        for (var i = 0; i < ranges.Length; i++)
        {
            ranges[i] = (new IpKey(_firsts[i]), new IpKey(_lasts[i]));
        }

        return ranges;
    }

    /// <summary>Builds a set from CIDR strings, throwing <see cref="FormatException"/> on the first invalid one.</summary>
    public static CidrSet Parse(IEnumerable<string> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        var builder = new Builder();
        foreach (var block in blocks)
        {
            builder.Add(block);
        }

        return builder.Build();
    }

    /// <summary>Collects blocks, then <see cref="Build"/>s an immutable <see cref="CidrSet"/>. Not thread-safe;
    /// cold path only.</summary>
    public sealed class Builder
    {
        private readonly List<(UInt128 First, UInt128 Last)> _ranges = [];

        /// <summary>Number of blocks added so far (before merging).</summary>
        public int Count => _ranges.Count;

        /// <summary>Adds a parsed block.</summary>
        public Builder Add(IpNetwork network)
        {
            _ranges.Add((network.First.Value, network.Last.Value));
            return this;
        }

        /// <summary>Adds a single address (a /32 or /128).</summary>
        public Builder Add(IpKey address)
        {
            _ranges.Add((address.Value, address.Value));
            return this;
        }

        /// <summary>Adds <c>address/prefix</c> or a bare address; throws <see cref="FormatException"/> with the
        /// reason when invalid (see <see cref="IpNetwork"/> for the rules).</summary>
        public Builder Add(string block) => Add(IpNetwork.Parse(block));

        /// <summary>Adds <c>address/prefix</c> or a bare address; false (and nothing added) when invalid.</summary>
        public bool TryAdd(ReadOnlySpan<char> block)
        {
            if (!IpNetwork.TryParse(block, out var network))
            {
                return false;
            }

            Add(network);
            return true;
        }

        /// <summary>Sorts and merges overlapping or adjacent blocks into the immutable set.</summary>
        public CidrSet Build()
        {
            if (_ranges.Count == 0)
            {
                return Empty;
            }

            var sorted = _ranges.ToArray();
            Array.Sort(sorted, static (a, b) => a.First.CompareTo(b.First));

            var firsts = new List<UInt128>(sorted.Length);
            var lasts = new List<UInt128>(sorted.Length);
            var (first, last) = sorted[0];
            for (var i = 1; i < sorted.Length; i++)
            {
                var next = sorted[i];
                // Overlapping or adjacent (careful: last + 1 overflows at the top of the space).
                if (last == UInt128.MaxValue || next.First <= last + 1)
                {
                    if (next.Last > last)
                    {
                        last = next.Last;
                    }

                    continue;
                }

                firsts.Add(first);
                lasts.Add(last);
                (first, last) = next;
            }

            firsts.Add(first);
            lasts.Add(last);
            return new CidrSet([.. firsts], [.. lasts]);
        }
    }
}
