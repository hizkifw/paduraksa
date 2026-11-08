using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Paduraksa.Vault.Totp;

/// <summary>RFC 4226 (HOTP) and RFC 6238 (TOTP) arithmetic, with the parameters every authenticator app supports.</summary>
internal static class TotpMath
{
    /// <summary>The length of a generated secret: 160 bits, the size RFC 4226 recommends for HMAC-SHA-1.</summary>
    public const int SecretBytes = 20;

    public const int StepSeconds = 30;

    public const int Digits = 6;

    /// <summary>One step of clock drift either way.</summary>
    public const int Window = 1;

    /// <summary>The HOTP value (<paramref name="digits"/> decimal digits) of <paramref name="key"/> at <paramref name="step"/>.</summary>
    public static int Code(ReadOnlySpan<byte> key, long step, int digits = Digits)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> mac = stackalloc byte[HMACSHA1.HashSizeInBytes];
#pragma warning disable CA5350 // RFC 6238 specifies HMAC-SHA-1 and every authenticator app uses it; the key is a random 160-bit secret.
        HMACSHA1.HashData(key, counter, mac);
#pragma warning restore CA5350

        var offset = mac[^1] & 0x0F;
        var binary = ((mac[offset] & 0x7F) << 24) | (mac[offset + 1] << 16) | (mac[offset + 2] << 8) | mac[offset + 3];
        var modulus = 1;
        for (var i = 0; i < digits; i++)
        {
            modulus *= 10;
        }

        CryptographicOperations.ZeroMemory(mac);
        return binary % modulus;
    }

    /// <summary>The time step containing <paramref name="time"/>.</summary>
    public static long StepOf(DateTimeOffset time) => Math.Max(0, time.ToUnixTimeSeconds()) / StepSeconds;

    /// <summary>
    /// The latest step in the window whose code equals <paramref name="presented"/>, or -1. Every step is computed and compared in
    /// constant time, so the time taken does not say whether (or which) one matched.
    /// </summary>
    public static long MatchingStep(ReadOnlySpan<byte> key, int presented, long currentStep)
    {
        var matched = -1L;
        Span<byte> want = stackalloc byte[4];
        Span<byte> got = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(want, presented);
        for (var offset = -Window; offset <= Window; offset++)
        {
            var step = currentStep + offset;
            if (step < 0)
            {
                continue;
            }

            BinaryPrimitives.WriteInt32BigEndian(got, Code(key, step));
            var equal = CryptographicOperations.FixedTimeEquals(want, got);
            matched = equal ? step : matched;
        }

        return matched;
    }

    /// <summary>Exactly six ASCII digits.</summary>
    public static bool TryParseCode(string? text, out int code)
    {
        code = 0;
        if (text is not { Length: Digits })
        {
            return false;
        }

        foreach (var c in text)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }

            code = (code * 10) + (c - '0');
        }

        return true;
    }
}
