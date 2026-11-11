using System.Numerics;
using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Net;

namespace Paduraksa.Fuzz;

/// <summary>
/// The proof-of-work cookie, the challenge and the posted form: what a client sends the gate to get through the browser check. Both targets fix the key, the
/// host scope, the client and the clock, so a seed can be a token or a challenge that is really valid and the fuzzer can mutate its way along the format.
/// </summary>
internal static class ProofOfWorkTargets
{
    private const long Now = 1_800_000_000;

    private const int SeedDifficulty = 8;

    private static readonly FixedMacKey Key = new();

    private static readonly byte[] Scope = [.. Enumerable.Repeat((byte)0x11, 16)];

    private static readonly IpKey Client = IpKey.Parse("192.0.2.1");

    public static FuzzTarget Cookie { get; } = new(
        "pow-cookie",
        "the paduraksa_pow cookie: header scan and token check",
        RunCookie,
        CookieSeeds);

    public static FuzzTarget Form { get; } = new(
        "pow-form",
        "the posted challenge form: field parser, challenge, solution and return target",
        RunForm,
        FormSeeds);

    // ---- the cookie --------------------------------------------------------------------------------------------------------

    // The input is the first byte (the difficulty the host asks for), then the Cookie header, one line per line feed.
    private static void RunCookie(ReadOnlySpan<byte> input)
    {
        if (input.IsEmpty)
        {
            return;
        }

        var required = input[0] % 40;
        var lines = Lines(input[1..]);
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = new StringValues(lines);
        var request = context.Request;

        string Verdict()
        {
            Span<char> cookie = stackalloc char[ProofOfWorkToken.MaxCookieLength + 1];
            if (!ProofOfWorkToken.TryReadCookie(request, cookie, out var length, out var readProblem))
            {
                return "read " + readProblem;
            }

            var valid = ProofOfWorkToken.TryValidate(cookie[..length], Key, Scope, Client, Now, required, out var expiresAt, out var difficulty, out var problem);
            if (valid)
            {
                // A token that verifies is the one encoding of what it says, so nothing can be added to it or changed in it that the tag doesn't cover.
                Span<char> minted = stackalloc char[ProofOfWorkToken.EncodedLength];
                Checks.That(ProofOfWorkToken.TryEncode(Key, Scope, Client, expiresAt, difficulty, minted, out var written), "a valid token can't be minted again");
                Checks.That(minted[..written].SequenceEqual(cookie[..length]), $"a token that verifies, '{cookie[..length]}', is not the encoding of what it says, '{minted[..written]}'");
                Checks.That(difficulty >= required && expiresAt > Now, "a token that verifies is too easy or expired");
            }

            return $"{valid} {problem} {expiresAt} {difficulty}";
        }

        Checks.Deterministic(Verdict, "the cookie verdict");
        Checks.AllocatesNothing(() => _ = Verdict2(request, required), "reading and checking the cookie");
    }

    // The verdict again, without the string it builds: what the gate does.
    private static bool Verdict2(HttpRequest request, int required)
    {
        Span<char> cookie = stackalloc char[ProofOfWorkToken.MaxCookieLength + 1];
        return ProofOfWorkToken.TryReadCookie(request, cookie, out var length, out _)
            && ProofOfWorkToken.TryValidate(cookie[..length], Key, Scope, Client, Now, required, out _, out _, out _);
    }

    private static IEnumerable<(string Name, byte[] Bytes)> CookieSeeds()
    {
        var valid = Token(Now + 3600, 12);
        yield return ("valid", Cookie("paduraksa_pow=" + valid, 8));
        yield return ("valid-among-others", Cookie("a=b; paduraksa_pow=" + valid + "; c=d", 8));
        yield return ("too-easy", Cookie("paduraksa_pow=" + Token(Now + 3600, 8), 20));
        yield return ("expired", Cookie("paduraksa_pow=" + Token(Now - 1, 12), 8));
        yield return ("tampered", Cookie("paduraksa_pow=" + valid[..^2] + "AA", 8));
        yield return ("duplicate", Cookie("paduraksa_pow=" + valid + "; paduraksa_pow=" + valid, 8));
        yield return ("two-lines", Cookie("a=b\npaduraksa_pow=" + valid, 8));
        yield return ("other-cookie", Cookie("session=abc", 8));
        yield return ("empty-value", Cookie("paduraksa_pow=", 8));
        yield return ("padded", Cookie("paduraksa_pow=" + valid + "==", 8));

        static byte[] Cookie(string header, byte required) => [required, .. Encoding.Latin1.GetBytes(header)];
    }

    private static string Token(long expiresAt, int difficulty)
    {
        var buffer = new char[ProofOfWorkToken.EncodedLength];
        _ = ProofOfWorkToken.TryEncode(Key, Scope, Client, (uint)expiresAt, difficulty, buffer, out var written);
        return new string(buffer, 0, written);
    }

    // ---- the form ----------------------------------------------------------------------------------------------------------

    private static void RunForm(ReadOnlySpan<byte> bytes)
    {
        var input = bytes.ToArray();
        string Verdict()
        {
            Span<char> challenge = stackalloc char[ProofOfWorkChallenge.EncodedLength + 1];
            Span<char> solution = stackalloc char[ProofOfWorkChallenge.MaxSolutionLength + 1];
            Span<char> returnTarget = stackalloc char[2049];
            if (!ProofOfWorkEndpoint.TryParseForm(input, challenge, out var challengeLength, solution, out var solutionLength, returnTarget, out var returnLength))
            {
                return "form refused";
            }

            Checks.That(challengeLength <= challenge.Length && solutionLength <= solution.Length && returnLength <= returnTarget.Length, "a field longer than its buffer");
            var sanitized = ProofOfWorkEndpoint.SanitizeReturn(returnTarget[..returnLength]);
            Checks.That(
                sanitized.Length > 0 && sanitized[0] == '/' && !(sanitized.Length > 1 && sanitized[1] is '/' or '\\') && sanitized.All(static c => c is > ' ' and <= '~'),
                $"the return target '{sanitized}' can leave the site or break the Location header");

            var valid = ProofOfWorkChallenge.TryValidate(challenge[..challengeLength], Key, Scope, Client, Now, out var issuedAt, out var difficulty, out var problem);
            var solved = false;
            if (valid)
            {
                solved = ProofOfWorkChallenge.TryVerifySolution(challenge[..challengeLength], solution[..solutionLength], difficulty);

                // An independent count of the leading zero bits of SHA-256(challenge:solution).
                var hash = SHA256.HashData(Encoding.ASCII.GetBytes(string.Concat(challenge[..challengeLength], ":", solution[..solutionLength])));
                var zeros = LeadingZeroBits(hash);
                var wellFormed = solutionLength is > 0 and <= ProofOfWorkChallenge.MaxSolutionLength && solution[..solutionLength].ToString().All(char.IsAsciiDigit);
                Checks.That(solved == (wellFormed && zeros >= difficulty), $"the solution '{solution[..solutionLength]}' has {zeros} zero bits, the difficulty is {difficulty}, the verdict {solved}");
            }

            return $"{valid} {problem} {issuedAt} {difficulty} {solved} {sanitized}";
        }

        Checks.Deterministic(Verdict, "the form verdict");
        Checks.AllocatesNothing(
            () =>
            {
                Span<char> challenge = stackalloc char[ProofOfWorkChallenge.EncodedLength + 1];
                Span<char> solution = stackalloc char[ProofOfWorkChallenge.MaxSolutionLength + 1];
                Span<char> returnTarget = stackalloc char[2049];
                if (ProofOfWorkEndpoint.TryParseForm(input, challenge, out var challengeLength, solution, out var solutionLength, returnTarget, out _)
                    && ProofOfWorkChallenge.TryValidate(challenge[..challengeLength], Key, Scope, Client, Now, out _, out var difficulty, out _))
                {
                    _ = ProofOfWorkChallenge.TryVerifySolution(challenge[..challengeLength], solution[..solutionLength], difficulty);
                }
            },
            "parsing the form and checking the challenge");
    }

    private static int LeadingZeroBits(ReadOnlySpan<byte> hash)
    {
        var bits = 0;
        foreach (var value in hash)
        {
            if (value == 0)
            {
                bits += 8;
                continue;
            }

            return bits + BitOperations.LeadingZeroCount((uint)value) - 24;
        }

        return bits;
    }

    private static IEnumerable<(string Name, byte[] Bytes)> FormSeeds()
    {
        var challenge = Challenge(Now - 30, SeedDifficulty);
        var counter = 0;
        while (!ProofOfWorkChallenge.TryVerifySolution(challenge, counter.ToString(System.Globalization.CultureInfo.InvariantCulture), SeedDifficulty))
        {
            counter++;
        }

        var solution = counter.ToString(System.Globalization.CultureInfo.InvariantCulture);
        yield return ("solved", Form(challenge, solution, "%2Fdashboard%3Fa%3D1"));
        yield return ("solved-no-return", Form(challenge, solution, null));
        yield return ("wrong-solution", Form(challenge, solution + "1", "%2F"));
        yield return ("expired-challenge", Form(Challenge(Now - 700, SeedDifficulty), solution, "%2Fx"));
        yield return ("return-other-site", Form(challenge, solution, "%2F%2Fevil.example"));
        yield return ("return-backslash", Form(challenge, solution, "%2F%5Cevil.example"));
        yield return ("return-absolute", Form(challenge, solution, "https%3A%2F%2Fevil.example"));
        yield return ("plus-and-escapes", Form(challenge, solution, "%2Fa+b%00c"));
        yield return ("bad-escape", Encoding.ASCII.GetBytes($"challenge={challenge}&solution={solution}&return=%2"));
        yield return ("duplicate-field", Encoding.ASCII.GetBytes($"challenge={challenge}&challenge={challenge}&solution={solution}"));
        yield return ("unknown-field", Encoding.ASCII.GetBytes($"x=1&challenge={challenge}&solution={solution}&y"));
        yield return ("empty", []);

        static byte[] Form(string challenge, string solution, string? returnTarget) =>
            Encoding.ASCII.GetBytes($"challenge={challenge}&solution={solution}" + (returnTarget is null ? string.Empty : "&return=" + returnTarget));
    }

    private static string Challenge(long issuedAt, int difficulty)
    {
        var buffer = new char[ProofOfWorkChallenge.EncodedLength];
        var nonce = new byte[ProofOfWorkChallenge.NonceLength];
        for (var i = 0; i < nonce.Length; i++)
        {
            nonce[i] = (byte)(i * 7);
        }

        _ = ProofOfWorkChallenge.TryCreate(Key, Scope, Client, (uint)issuedAt, difficulty, nonce, buffer, out var written);
        return new string(buffer, 0, written);
    }

    private static string[] Lines(ReadOnlySpan<byte> bytes)
    {
        List<string> lines = [];
        while (lines.Count < 4)
        {
            var feed = bytes.IndexOf((byte)'\n');
            lines.Add(Encoding.Latin1.GetString(feed < 0 ? bytes : bytes[..feed]));
            if (feed < 0)
            {
                break;
            }

            bytes = bytes[(feed + 1)..];
        }

        return [.. lines];
    }
}
