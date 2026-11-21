using System.Buffers.Binary;
using System.Numerics;

namespace Paduraksa.Proxy.Honeypot;

/// <summary>
/// xoshiro256** seeded from 32 bytes of an HMAC. Written here instead of <see cref="Random"/> because the site's pages must stay
/// byte-identical across .NET versions, and only an algorithm we own makes that promise.
/// </summary>
internal struct SiteRandom
{
    private ulong _s0;
    private ulong _s1;
    private ulong _s2;
    private ulong _s3;

    public SiteRandom(ReadOnlySpan<byte> seed)
    {
        _s0 = BinaryPrimitives.ReadUInt64LittleEndian(seed);
        _s1 = BinaryPrimitives.ReadUInt64LittleEndian(seed[8..]);
        _s2 = BinaryPrimitives.ReadUInt64LittleEndian(seed[16..]);
        _s3 = BinaryPrimitives.ReadUInt64LittleEndian(seed[24..]);

        // The all-zero state is the one fixed point of the generator.
        if ((_s0 | _s1 | _s2 | _s3) == 0)
        {
            _s0 = 0x9E3779B97F4A7C15UL;
        }
    }

    /// <summary>A stateless 64-bit mixer (the splitmix64 finalizer), for flags that depend on a page ID and nothing else.</summary>
    public static ulong Mix(ulong value)
    {
        value += 0x9E3779B97F4A7C15UL;
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }

    public ulong NextUInt64()
    {
        var result = BitOperations.RotateLeft(_s1 * 5, 7) * 9;
        var t = _s1 << 17;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = BitOperations.RotateLeft(_s3, 45);
        return result;
    }

    public uint NextUInt32() => (uint)(NextUInt64() >> 32);

    /// <summary>A value in <c>[0, bound)</c>. The multiply-shift reduction has a bias below 2^-32 per value, which a trap site does not care about.</summary>
    public int Below(int bound) => (int)(((ulong)NextUInt32() * (uint)bound) >> 32);

    public int Between(int minInclusive, int maxInclusive) => minInclusive + Below(maxInclusive - minInclusive + 1);

    public bool Chance(int percent) => Below(100) < percent;
}
