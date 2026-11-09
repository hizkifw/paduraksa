using System.Security.Cryptography;

namespace Paduraksa.Proxy.Logging;

/// <summary>
/// The request ID every request carries (PLAN §7.4): <c>X-Request-Id</c>, taken from the client only through a trusted
/// proxy, else generated here, and shown on error pages (M8) so a user can report it.
/// </summary>
/// <remarks>
/// <para><b>Which one is honoured.</b> Only a peer that the snapshot's trusted-proxy policy trusts (the same decision
/// that makes <c>X-Forwarded-For</c> or <c>CF-Connecting-IP</c> count, <see cref="Net.ClientIpResolution.PeerTrusted"/>)
/// may supply the value: it is what its own log correlated. From any other peer the header is ignored and replaced, so a
/// client can't choose the ID its requests appear under.</para>
/// <para><b>What is accepted.</b> One header line of 1 to <see cref="MaxLength"/> visible ASCII characters (0x21-0x7E): no
/// spaces, no control characters, nothing non-ASCII. Anything else (empty, too long, repeated, control characters) is
/// replaced by a generated ID rather than rejected.</para>
/// <para><b>Generated shape.</b> 22 characters: an 8-character per-process prefix from <see cref="RandomNumberGenerator"/>
/// and a 13-character base32 rendering of a per-process 64-bit counter, so no two requests of one process share an ID and
/// two processes started from the same code don't either. Formatting it is a span write; the string itself is the one
/// allocation a request accepts (see <see cref="Gate.GateRequestState.RequestIdText"/>).</para>
/// </remarks>
public static class RequestIds
{
    /// <summary>The request and response header that carries it.</summary>
    public const string HeaderName = "X-Request-Id";

    /// <summary>Longest accepted value, in characters.</summary>
    public const int MaxLength = 128;

    /// <summary>Length of a generated ID: 8 prefix characters, a hyphen and 13 counter characters.</summary>
    public const int GeneratedLength = 22;

    private const int PrefixLength = 8;
    private const int CounterLength = 13;

    // Crockford's base32 without the letters that are easy to misread (I, L, O, U).
    private const string Alphabet = "0123456789abcdefghjkmnpqrstvwxyz";

    private static readonly string Prefix = CreatePrefix();
    private static long s_counter;

    /// <summary>
    /// Whether <paramref name="value"/> may be adopted as the request ID: 1 to <see cref="MaxLength"/> visible ASCII
    /// characters. Allocation-free.
    /// </summary>
    public static bool IsValid(ReadOnlySpan<char> value) =>
        !value.IsEmpty && value.Length <= MaxLength && !value.ContainsAnyExceptInRange('\x21', '\x7E');

    /// <summary>The next per-process counter value, for a generated ID.</summary>
    public static long NextCounter() => Interlocked.Increment(ref s_counter);

    /// <summary>The generated ID for <paramref name="counter"/>: one string, the request's accepted allocation.</summary>
    public static string Format(long counter)
    {
        Span<char> buffer = stackalloc char[GeneratedLength];
        int length = Format(counter, buffer);
        return new string(buffer[..length]);
    }

    /// <summary>Writes <see cref="GeneratedLength"/> characters of the ID for <paramref name="counter"/>.</summary>
    public static int Format(long counter, Span<char> destination)
    {
        if (destination.Length < GeneratedLength)
        {
            throw new ArgumentException($"The buffer must hold {GeneratedLength} characters.", nameof(destination));
        }

        Prefix.AsSpan().CopyTo(destination);
        destination[PrefixLength] = '-';
        Span<char> counterText = destination.Slice(PrefixLength + 1, CounterLength);
        ulong remaining = (ulong)counter;
        for (int i = CounterLength - 1; i >= 0; i--)
        {
            counterText[i] = Alphabet[(int)(remaining & 0x1F)];
            remaining >>= 5;
        }

        return GeneratedLength;
    }

    private static string CreatePrefix()
    {
        Span<byte> bytes = stackalloc byte[5];
        RandomNumberGenerator.Fill(bytes);
        Span<char> text = stackalloc char[PrefixLength];
        ulong value = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            value = (value << 8) | bytes[i];
        }

        for (int i = PrefixLength - 1; i >= 0; i--)
        {
            text[i] = Alphabet[(int)(value & 0x1F)];
            value >>= 5;
        }

        return new string(text);
    }
}
