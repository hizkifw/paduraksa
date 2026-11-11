namespace Paduraksa.Core.Geo;

/// <summary>
/// A set of <see cref="CountryCode"/>s as one bitset (678 bits in 11 words), compiled once per snapshot: a membership check is a
/// single bit test and allocates nothing.
/// </summary>
public sealed class CountrySet
{
    private const int Words = (CountryCode.SlotCount + 63) / 64;

    private readonly ulong[] _bits;

    private CountrySet(ulong[] bits, int count)
    {
        _bits = bits;
        Count = count;
    }

    public static CountrySet Empty { get; } = new(new ulong[Words], 0);

    public int Count { get; }

    public bool IsEmpty => Count == 0;

    public static CountrySet Create(IEnumerable<CountryCode> codes)
    {
        ArgumentNullException.ThrowIfNull(codes);
        var bits = new ulong[Words];
        var count = 0;
        foreach (var code in codes)
        {
            ref var word = ref bits[code.Slot >> 6];
            var mask = 1UL << (code.Slot & 63);
            if ((word & mask) == 0)
            {
                word |= mask;
                count++;
            }
        }

        return count == 0 ? Empty : new CountrySet(bits, count);
    }

    public bool Contains(CountryCode code) => (_bits[code.Slot >> 6] & (1UL << (code.Slot & 63))) != 0;
}
