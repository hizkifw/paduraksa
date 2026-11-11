namespace Paduraksa.Core.Geo;

/// <summary>
/// The country a request came from, as Cloudflare names it in <c>CF-IPCountry</c>: a compact index, so a rule check is one bit
/// test and a request carries no string. The default value is <see cref="Unknown"/>.
/// </summary>
/// <remarks>
/// A letter pair <c>AA</c>-<c>ZZ</c> is slot <c>2 + 26 * (first - 'A') + (second - 'A')</c> (0..675 plus the two pseudo-slots below);
/// <see cref="Tor"/> (Cloudflare's <c>T1</c>) is its own slot so a rule can name it, while <c>XX</c> and everything that doesn't
/// read as a code are <see cref="Unknown"/>. Whether a pair is an assigned country is <see cref="Countries"/>'s question; a pair
/// that isn't one still parses, and matches no rule.
/// </remarks>
public readonly record struct CountryCode
{
    /// <summary>Slots: unknown, Tor and the 676 letter pairs.</summary>
    public const int SlotCount = 2 + 26 * 26;

    private readonly ushort _slot;

    private CountryCode(ushort slot) => _slot = slot;

    /// <summary>No country: <c>XX</c>, a missing header, or a value that isn't two letters.</summary>
    public static CountryCode Unknown => default;

    /// <summary>A Tor exit node (<c>T1</c>).</summary>
    public static CountryCode Tor => new(1);

    public int Slot => _slot;

    public bool IsUnknown => _slot == 0;

    public bool IsTor => _slot == 1;

    /// <summary>A letter pair, assigned or not.</summary>
    public bool IsLetterPair => _slot >= 2;

    /// <summary>Two ASCII letters in either case map to their code; <c>T1</c> to <see cref="Tor"/>; <c>XX</c> and anything else to
    /// <see cref="Unknown"/>. 0 B.</summary>
    public static CountryCode Parse(ReadOnlySpan<char> value)
    {
        if (value.Length != 2)
        {
            return Unknown;
        }

        if ((value[0] is 'T' or 't') && value[1] == '1')
        {
            return Tor;
        }

        if (!char.IsAsciiLetter(value[0]) || !char.IsAsciiLetter(value[1]))
        {
            return Unknown;
        }

        var first = char.ToLowerInvariant(value[0]) - 'a';
        var second = char.ToLowerInvariant(value[1]) - 'a';
        return first == 'x' - 'a' && second == first ? Unknown : new CountryCode((ushort)(2 + (26 * first) + second));
    }

    /// <summary>The wire form: <c>DE</c>, <c>T1</c> for Tor, <c>XX</c> for unknown.</summary>
    public override string ToString()
    {
        if (IsUnknown)
        {
            return "XX";
        }

        if (IsTor)
        {
            return "T1";
        }

        var pair = _slot - 2;
        return string.Create(2, pair, static (chars, pair) =>
        {
            chars[0] = (char)('A' + (pair / 26));
            chars[1] = (char)('A' + (pair % 26));
        });
    }
}
