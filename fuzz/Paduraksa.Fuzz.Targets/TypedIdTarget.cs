using System.Buffers.Binary;
using System.Numerics;
using System.Text;

using Paduraksa.TypedIds;

namespace Paduraksa.Fuzz;

// Declared here, one per kind and with prefixes of different lengths, so the target doesn't depend on Paduraksa's own model.
[TypedId("zz", IdKind.UuidV7)]
internal readonly partial record struct FuzzV7Id;

[TypedId("fuzzy", IdKind.UuidV4)]
internal readonly partial record struct FuzzV4Id;

[TypedId("fuzzlong", IdKind.Int64Ordered)]
internal readonly partial record struct FuzzLongId;

/// <summary>
/// The parser of the typed IDs (<c>Paduraksa.TypedIds</c>), which reads admin route parameters and cookies. The input's first byte picks the kind (its low 7 bits modulo 3: UuidV7,
/// UuidV4, Int64Ordered) and the encoding of the rest (bit 7 clear: the rest is the UTF-8 text; set: the rest is UTF-16LE chars, a last odd byte one char). The rest also feeds the raw
/// value for the format side (its first 16 bytes zero-padded for a UUID, its first 8 for an Int64 with the sign bit cleared).
/// Properties: no exception; the same verdict twice; the char parser and the UTF-8 parser agree on the same text; an accepted input is canonical (<c>ToString</c> and both <c>TryFormat</c>
/// overloads give it back exactly); the verdict and the value are those of an independent reading (the exact prefix and <c>_</c>, the exact length, every character in the alphabet,
/// the value decoded with <see cref="BigInteger"/>, at most 2^128-1 for a UUID and 2^63-1 for an Int64); parse and span formatting allocate 0 B; a value wrapped with <c>FromValue</c>
/// formats to <c>FormattedLength</c> and parses back to itself, and a buffer one short refuses without writing past it.
/// </summary>
internal static class TypedIdTarget
{
    // The format's alphabet and widths, copied, so the reading doesn't take the codec's word for them.
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private const int Width128 = 22; // 62^21 < 2^128 <= 62^22
    private const int Width64 = 11; // 62^10 < 2^63 <= 62^11

    public static FuzzTarget Target { get; } = new(
        "typed-id",
        "a typed ID (prefix_ and base62) of each kind, as chars and as UTF-8: the parser against an independent reading, canonical text, 0 B, and the format side",
        Run,
        Seeds);

    private static void Run(ReadOnlySpan<byte> input)
    {
        var kind = input.IsEmpty ? 0 : (input[0] & 0x7F) % 3;
        var chars = !input.IsEmpty && input[0] >= 0x80;
        var rest = input.IsEmpty ? [] : input[1..].ToArray();

        string text;
        byte[] utf8;
        if (chars)
        {
            var units = new char[(rest.Length + 1) / 2];
            for (var i = 0; i < units.Length; i++)
            {
                units[i] = (char)(rest[2 * i] | (2 * i + 1 < rest.Length ? rest[2 * i + 1] << 8 : 0));
            }

            text = new string(units);
            // A non-ASCII char has no single-byte form: its UTF-8 bytes are refused too, which is the agreement checked.
            utf8 = Encoding.UTF8.GetBytes(text);
        }
        else
        {
            text = Encoding.Latin1.GetString(rest);
            utf8 = rest;
        }

        switch (kind)
        {
            case 0:
                Check<FuzzV7Id, Guid>(text, utf8, rest, Width128, UInt128Limit, ToGuid, GuidFromRaw);
                break;
            case 1:
                Check<FuzzV4Id, Guid>(text, utf8, rest, Width128, UInt128Limit, ToGuid, GuidFromRaw);
                break;
            default:
                Check<FuzzLongId, long>(text, utf8, rest, Width64, long.MaxValue, static v => (long)v, LongFromRaw);
                break;
        }
    }

    private static readonly BigInteger UInt128Limit = (BigInteger.One << 128) - 1;

    private static Guid ToGuid(BigInteger value)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt128BigEndian(bytes, (UInt128)value);
        return new Guid(bytes, bigEndian: true);
    }

    private static Guid GuidFromRaw(byte[] rest)
    {
        var raw = new byte[16];
        rest.AsSpan(0, Math.Min(16, rest.Length)).CopyTo(raw);
        return new Guid(raw, bigEndian: true);
    }

    private static long LongFromRaw(byte[] rest)
    {
        var raw = new byte[8];
        rest.AsSpan(0, Math.Min(8, rest.Length)).CopyTo(raw);
        return BinaryPrimitives.ReadInt64LittleEndian(raw) & long.MaxValue;
    }

    private static void Check<TId, TValue>(string text, byte[] utf8, byte[] rest, int width, BigInteger limit, Func<BigInteger, TValue> valueOf, Func<byte[], TValue> rawValue)
        where TId : struct, ITypedId<TId, TValue>
        where TValue : struct, IEquatable<TValue>
    {
        var prefix = TId.Prefix + "_";
        Checks.That(TId.FormattedLength == prefix.Length + width, $"{typeof(TId).Name}.FormattedLength is {TId.FormattedLength}");

        var verdict = Checks.Deterministic(() => Describe<TId, TValue>(text, utf8), "the typed ID verdict");
        Checks.AllocatesNothing(() => _ = TId.TryParse(text.AsSpan(), out _), "TryParse of chars");
        Checks.AllocatesNothing(() => _ = TId.TryParse(utf8.AsSpan(), out _), "TryParse of UTF-8");

        var parsedChars = TId.TryParse(text.AsSpan(), out var fromChars);
        var parsedUtf8 = TId.TryParse(utf8.AsSpan(), out var fromUtf8);
        Checks.That(parsedChars == parsedUtf8 && fromChars.Equals(fromUtf8), $"'{Shown(text)}': the char parser says {parsedChars} and the UTF-8 parser says {parsedUtf8}");
        Checks.That(TId.TryParse(text, out var fromString) == parsedChars && fromString.Equals(fromChars), "the string overload disagrees with the span overload");

        var expected = Reading(text, prefix, width, limit);
        Checks.That(parsedChars == expected.HasValue, $"'{Shown(text)}' is {(parsedChars ? "accepted" : "refused")} by the parser and {(expected.HasValue ? "accepted" : "refused")} by the reading by hand");
        if (!parsedChars)
        {
            Checks.That(fromChars.Equals(default) && fromUtf8.Equals(default), "a refused input left a value behind");
            Checks.That(Throws<TId>(text), $"Parse accepted '{Shown(text)}', which TryParse refuses");
        }
        else
        {
            Checks.That(fromChars.Value.Equals(valueOf(expected!.Value)), $"'{Shown(text)}' is {fromChars.Value} to the parser and {valueOf(expected.Value)} to the reading by hand");
            Checks.That(TId.Parse(text.AsSpan()).Equals(fromChars) && TId.Parse(utf8.AsSpan()).Equals(fromChars), "Parse and TryParse disagree");
            Checks.That(fromChars.IsEmpty == fromChars.Value.Equals(default), "IsEmpty disagrees with the value");
            Checks.That(Describe<TId, TValue>(text, utf8) == verdict, "the verdict changed");
            CheckCanonical<TId, TValue>(fromChars, text, utf8);
        }

        CheckFormatSide<TId, TValue>(rawValue(rest));
    }

    /// <summary>The value the rules give for <paramref name="text"/>, read independently of the codec; null when one fails.</summary>
    private static BigInteger? Reading(string text, string prefix, int width, BigInteger limit)
    {
        if (text.Length != prefix.Length + width || !text.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        BigInteger value = 0;
        foreach (var c in text.AsSpan(prefix.Length))
        {
            var digit = Alphabet.IndexOf(c, StringComparison.Ordinal);
            if (digit < 0)
            {
                return null;
            }

            value = (value * 62) + digit;
        }

        return value <= limit ? value : null;
    }

    private static void CheckCanonical<TId, TValue>(TId id, string text, byte[] utf8)
        where TId : struct, ITypedId<TId, TValue>
        where TValue : struct, IEquatable<TValue>
    {
        Checks.That(id.ToString() == text, $"'{Shown(text)}' is accepted and its ToString is '{id}'");

        var chars = new char[text.Length + 1];
        Checks.That(id.TryFormat(chars, out var written, default, null) && written == text.Length && chars.AsSpan(0, written).SequenceEqual(text), $"TryFormat of chars doesn't give '{Shown(text)}' back");
        var bytes = new byte[utf8.Length + 1];
        Checks.That(id.TryFormat(bytes, out written, default, null) && written == utf8.Length && bytes.AsSpan(0, written).SequenceEqual(utf8), $"TryFormat of UTF-8 doesn't give '{Shown(text)}' back");
    }

    private static void CheckFormatSide<TId, TValue>(TValue value)
        where TId : struct, ITypedId<TId, TValue>
        where TValue : struct, IEquatable<TValue>
    {
        var id = TId.FromValue(value);
        var length = TId.FormattedLength;
        var formatted = id.ToString()!;
        Checks.That(formatted.Length == length, $"{id} is {formatted.Length} chars and FormattedLength says {length}");
        Checks.That(id.Value.Equals(value) && id.IsEmpty == value.Equals(default), "FromValue changed the value");
        Checks.That(TId.TryParse(formatted, out var back) && back.Value.Equals(value), $"{formatted} doesn't parse back to its value");

        var chars = new char[length];
        var bytes = new byte[length];
        Checks.AllocatesNothing(() => _ = id.TryFormat(chars, out _, default, null), "TryFormat of chars");
        Checks.AllocatesNothing(() => _ = id.TryFormat(bytes, out _, default, null), "TryFormat of UTF-8");
        Checks.That(TId.TryParse(chars.AsSpan(), out back) && back.Value.Equals(value) && TId.TryParse(bytes.AsSpan(), out back) && back.Value.Equals(value), "the formatted spans don't parse back");
        Checks.That(chars.AsSpan().SequenceEqual(formatted) && Encoding.ASCII.GetString(bytes) == formatted, "the formatted spans differ from ToString");

        // One short: it must refuse, and what lies past the short span stays as it was.
        var guarded = new char[length + 8];
        guarded.AsSpan().Fill('#');
        Checks.That(!id.TryFormat(guarded.AsSpan(0, length - 1), out var written, default, null) && written == 0, "TryFormat into a char span one short didn't refuse");
        Checks.That(guarded.AsSpan(length - 1).IndexOfAnyExcept('#') < 0, "TryFormat wrote past a char span one short");
        var guardedBytes = new byte[length + 8];
        guardedBytes.AsSpan().Fill((byte)'#');
        Checks.That(!id.TryFormat(guardedBytes.AsSpan(0, length - 1), out written, default, null) && written == 0, "TryFormat into a byte span one short didn't refuse");
        Checks.That(guardedBytes.AsSpan(length - 1).IndexOfAnyExcept((byte)'#') < 0, "TryFormat wrote past a byte span one short");
    }

    private static bool Throws<TId>(string text)
        where TId : struct, ITypedId<TId>
    {
        try
        {
            _ = TId.Parse(text.AsSpan());
            return false;
        }
        catch (FormatException)
        {
            return true;
        }
    }

    private static string Describe<TId, TValue>(string text, byte[] utf8)
        where TId : struct, ITypedId<TId, TValue>
        where TValue : struct, IEquatable<TValue>
    {
        var chars = TId.TryParse(text.AsSpan(), out var fromChars);
        var bytes = TId.TryParse(utf8.AsSpan(), out var fromUtf8);
        return $"{chars}:{fromChars.Value}/{bytes}:{fromUtf8.Value}";
    }

    private static string Shown(string text) => text.Length <= 64 ? text : string.Concat(text.AsSpan(0, 64), "...");

    private static IEnumerable<(string Name, byte[] Bytes)> Seeds()
    {
        const string Zeros22 = "0000000000000000000000";
        const string Zeros11 = "00000000000";
        // 2^128-1, 2^63-1 and 2^64-1 in base62, fixed width, written out so a drifting encoder can't move the edges; the "above" cases are one more.
        const string Max128 = "7n42DGM5Tflk9n8mt7Fhc7";
        const string Max63 = "AzL8n0Y58m7";
        const string MaxUlong = "LygHa16AHYF";
        var above63 = Increment(Max63);
        var above128 = Increment(Max128);

        static (string, byte[]) Bytes(string name, int kind, string text) => (name, [(byte)kind, .. Encoding.Latin1.GetBytes(text)]);
        static (string, byte[]) Chars(string name, int kind, string text) => (name, [(byte)(0x80 | kind), .. Encoding.Unicode.GetBytes(text)]);

        yield return Bytes("v7-new", 0, FuzzV7Id.New().ToString());
        yield return Bytes("v7-zeros", 0, "zz_" + Zeros22);
        yield return Bytes("v7-max", 0, "zz_" + Max128);
        yield return Bytes("v7-above-max", 0, "zz_" + above128);
        yield return Bytes("v7-wrong-prefix", 0, "zy_" + Zeros22);
        yield return Bytes("v7-no-separator", 0, "zz" + Zeros22 + "0");
        yield return Bytes("v7-prefix-only", 0, "zz");
        yield return Bytes("v7-short", 0, "zz_" + Zeros22[1..]);
        yield return Bytes("v7-long", 0, "zz_" + Zeros22 + "0");
        yield return Bytes("v7-non-ascii", 0, "zz_" + Zeros22[..21] + "é");
        yield return Bytes("v7-just-outside-alphabet", 0, "zz_" + Zeros22[..21] + "/");
        yield return Bytes("v7-after-z", 0, "zz_" + Zeros22[..21] + "{");
        yield return Bytes("v7-after-9", 0, "zz_" + Zeros22[..21] + ":");
        yield return Bytes("v7-before-A", 0, "zz_" + Zeros22[..21] + "@");
        yield return Bytes("v7-before-a", 0, "zz_" + Zeros22[..21] + "`");
        yield return Bytes("v4-new", 1, FuzzV4Id.New().ToString());
        yield return Bytes("v4-zeros", 1, "fuzzy_" + Zeros22);
        yield return Bytes("v4-max", 1, "fuzzy_" + Max128);
        yield return Bytes("v4-above-max", 1, "fuzzy_" + above128);
        yield return Bytes("v4-wrong-prefix", 1, "fuzzz_" + Zeros22);
        yield return Bytes("v4-no-separator", 1, "fuzzy" + Zeros22 + "0");
        yield return Bytes("v4-short", 1, "fuzzy_" + Zeros22[1..]);
        yield return Bytes("v4-long", 1, "fuzzy_" + Zeros22 + "0");
        yield return Bytes("v4-all-z", 1, "fuzzy_" + new string('z', 22));
        yield return Bytes("v4-prefix-only", 1, "fuzzy");
        yield return Bytes("v4-non-ascii", 1, "fuzzy_" + Zeros22[..21] + "\u00e9");
        yield return Bytes("v4-just-outside-alphabet", 1, "fuzzy_" + Zeros22[..21] + "/");
        yield return Bytes("v7-all-z", 0, "zz_" + new string('z', 22));
        yield return Bytes("long-new", 2, FuzzLongId.New().ToString());
        yield return Bytes("long-zeros", 2, "fuzzlong_" + Zeros11);
        yield return Bytes("long-max", 2, "fuzzlong_" + Max63);
        yield return Bytes("long-above-max", 2, "fuzzlong_" + above63);
        yield return Bytes("long-above-ulong", 2, "fuzzlong_" + MaxUlong);
        yield return Bytes("long-all-z", 2, "fuzzlong_" + new string('z', 11));
        yield return Bytes("long-wrong-prefix", 2, "fuzzlonk_" + Zeros11);
        yield return Bytes("long-no-separator", 2, "fuzzlong" + Zeros11 + "0");
        yield return Bytes("long-prefix-only", 2, "fuzzlong_");
        yield return Bytes("long-short", 2, "fuzzlong_" + Zeros11[1..]);
        yield return Bytes("long-long", 2, "fuzzlong_" + Zeros11 + "0");
        yield return Bytes("long-non-ascii", 2, "fuzzlong_" + Zeros11[..10] + "ÿ");
        yield return Bytes("long-just-outside-alphabet", 2, "fuzzlong_" + Zeros11[..10] + "-");
        yield return Chars("v7-chars", 0, FuzzV7Id.New().ToString());
        yield return Chars("v4-chars", 1, FuzzV4Id.New().ToString());
        yield return Chars("long-chars", 2, FuzzLongId.New().ToString());
        yield return Chars("long-chars-max", 2, "fuzzlong_" + Max63);
        yield return Chars("long-chars-wide", 2, "fuzzlong_" + Zeros11[..10] + "İ");
        yield return Chars("v7-chars-wide", 0, "zz_" + Zeros22[..21] + "Ł");
        yield return ("empty", []);
        yield return ("kind-only", [0]);
    }

    /// <summary>The base62 text one above <paramref name="text"/>, at the same width (the text is not all <c>z</c>).</summary>
    private static string Increment(string text)
    {
        var value = BigInteger.Zero;
        foreach (var c in text)
        {
            value = (value * 62) + Alphabet.IndexOf(c, StringComparison.Ordinal);
        }

        value += 1;
        var digits = new char[text.Length];
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            value = BigInteger.DivRem(value, 62, out var digit);
            digits[i] = Alphabet[(int)digit];
        }

        return new string(digits);
    }
}
