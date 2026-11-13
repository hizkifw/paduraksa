using System.Buffers.Binary;
using System.Numerics;

namespace Paduraksa.Proxy.Analytics;

/// <summary>
/// A HyperLogLog sketch of how many distinct values were added (docs/design/observability.md "Traffic analytics"): 4 096 one-byte registers, so a standard
/// error of 1.04 / sqrt(4096), about 1.6 %, and 4 KiB while a bucket is being filled. It keeps no value, only the longest run of
/// leading zeros each register has seen, so what it stores can't be turned back into the addresses that were added.
/// </summary>
/// <remarks>
/// <para><b>Mergeable.</b> Two sketches merge by the register-wise maximum, which is the sketch of the union: a range's unique
/// visitors are the merge of its buckets', not a sum of their counts.</para>
/// <para><b>Stored small.</b> <see cref="Write"/> picks the smaller of a sparse list (a register's index and value, 3 bytes each) and
/// the dense registers, so a quiet bucket costs tens of bytes. Not thread-safe: the drain owns the open ones, and a reader works on a
/// copy.</para>
/// </remarks>
public sealed class HyperLogLog
{
    /// <summary>Index bits: 2^12 registers.</summary>
    public const int Precision = 12;

    /// <summary>The registers.</summary>
    public const int RegisterCount = 1 << Precision;

    private const byte DenseFormat = 1;
    private const byte SparseFormat = 2;
    private const int SparseEntryBytes = 3;

    private readonly byte[] _registers = new byte[RegisterCount];

    /// <summary>True until something is added.</summary>
    public bool IsEmpty
    {
        get
        {
            foreach (var register in _registers)
            {
                if (register != 0)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>Adds a value by its 64-bit hash, which must be uniformly distributed (a salted cryptographic hash is).</summary>
    public void Add(ulong hash)
    {
        var index = (int)(hash >> (64 - Precision));
        // The rank is the position of the first one bit in the rest; the guard bit keeps an all-zero rest at its maximum.
        var rest = (hash << Precision) | (1UL << (Precision - 1));
        var rank = (byte)(BitOperations.LeadingZeroCount(rest) + 1);
        if (rank > _registers[index])
        {
            _registers[index] = rank;
        }
    }

    /// <summary>Makes this the sketch of everything either one saw.</summary>
    public void Merge(HyperLogLog other)
    {
        ArgumentNullException.ThrowIfNull(other);
        for (var i = 0; i < RegisterCount; i++)
        {
            if (other._registers[i] > _registers[i])
            {
                _registers[i] = other._registers[i];
            }
        }
    }

    public HyperLogLog Clone()
    {
        HyperLogLog copy = new();
        _registers.CopyTo(copy._registers, 0);
        return copy;
    }

    /// <summary>
    /// The estimated number of distinct values: the harmonic mean estimate, with linear counting while many registers are still empty
    /// (where the harmonic mean is biased), which makes a small count nearly exact.
    /// </summary>
    public long Estimate()
    {
        double sum = 0;
        var zeros = 0;
        foreach (var register in _registers)
        {
            sum += Math.ScaleB(1.0, -register);
            if (register == 0)
            {
                zeros++;
            }
        }

        const double Alpha = 0.7213 / (1 + (1.079 / RegisterCount));
        var raw = Alpha * RegisterCount * RegisterCount / sum;
        if (raw <= 2.5 * RegisterCount && zeros > 0)
        {
            return (long)Math.Round(RegisterCount * Math.Log((double)RegisterCount / zeros));
        }

        return (long)Math.Round(raw);
    }

    /// <summary>Appends the sketch to <paramref name="destination"/>: a format byte, then the registers (dense) or a count and (index, value) pairs (sparse), whichever is smaller.</summary>
    internal void Write(ByteWriter destination)
    {
        var used = 0;
        foreach (var register in _registers)
        {
            if (register != 0)
            {
                used++;
            }
        }

        if (used * SparseEntryBytes + 2 < RegisterCount)
        {
            destination.Add(SparseFormat);
            destination.Add((byte)(used >> 8));
            destination.Add((byte)used);
            for (var i = 0; i < RegisterCount; i++)
            {
                if (_registers[i] != 0)
                {
                    destination.Add((byte)(i >> 8));
                    destination.Add((byte)i);
                    destination.Add(_registers[i]);
                }
            }
        }
        else
        {
            destination.Add(DenseFormat);
            destination.Add(_registers);
        }
    }

    /// <summary>Reads what <see cref="Write"/> wrote from the start of <paramref name="source"/>; false when it isn't a sketch. <paramref name="read"/> is how many bytes it took.</summary>
    internal static bool TryRead(ReadOnlySpan<byte> source, out HyperLogLog sketch, out int read)
    {
        sketch = new HyperLogLog();
        read = 0;
        if (source.IsEmpty)
        {
            return false;
        }

        switch (source[0])
        {
            case DenseFormat when source.Length >= 1 + RegisterCount:
                source.Slice(1, RegisterCount).CopyTo(sketch._registers);
                read = 1 + RegisterCount;
                return sketch.RegistersAreValid();
            case SparseFormat when source.Length >= 3:
                var used = BinaryPrimitives.ReadUInt16BigEndian(source[1..]);
                if (used > RegisterCount || source.Length < 3 + (used * SparseEntryBytes))
                {
                    return false;
                }

                for (var i = 0; i < used; i++)
                {
                    var entry = source.Slice(3 + (i * SparseEntryBytes), SparseEntryBytes);
                    var index = BinaryPrimitives.ReadUInt16BigEndian(entry);
                    if (index >= RegisterCount)
                    {
                        return false;
                    }

                    sketch._registers[index] = entry[2];
                }

                read = 3 + (used * SparseEntryBytes);
                return sketch.RegistersAreValid();
            default:
                return false;
        }
    }

    // A register can't exceed the 64 - Precision + 1 ranks a hash allows; anything above is a corrupt blob, and would otherwise
    // make Estimate read a nonsense (ScaleB of a huge negative is just 0, so it wouldn't even fail).
    private bool RegistersAreValid()
    {
        foreach (var register in _registers)
        {
            if (register > 64 - Precision + 1)
            {
                return false;
            }
        }

        return true;
    }
}
