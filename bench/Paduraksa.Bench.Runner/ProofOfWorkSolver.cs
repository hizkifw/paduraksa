using System.Globalization;
using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Paduraksa.Bench.Runner;

/// <summary>
/// Solves the proof-of-work challenge the way the browser page does, so the runner can hand the harness a valid
/// <c>paduraksa_pow</c> cookie before the browser-check run. The protocol is the gate's (docs/design/hot-path.md "Request pipeline"): GET the page with
/// <c>Accept: text/html</c>, read the challenge out of it, find a decimal counter whose SHA-256 digest has enough leading
/// zero bits, POST it to the verify endpoint, and take the cookie from the 303's <c>Set-Cookie</c>.
/// </summary>
/// <remarks>
/// This is deliberately a re-implementation of the page's JavaScript rather than a call into the proxy's internals: the
/// harness must fail if the wire protocol changes, not pass because it shares the server's helper.
/// </remarks>
internal static partial class ProofOfWorkSolver
{
    public const string CookieName = "paduraksa_pow";

    [GeneratedRegex("name=\"challenge\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex ChallengeInput();

    /// <summary>The cookie value (<c>paduraksa_pow=...</c>, attributes left off) for <paramref name="host"/>.</summary>
    /// <exception cref="InvalidOperationException">The page, the verify endpoint or the cookie isn't what this protocol says.</exception>
    public static async Task<string> SolveAsync(int port, string host, int difficulty, CancellationToken cancellationToken)
    {
        using HttpClientHandler handler = new() { AllowAutoRedirect = false };
        using HttpClient client = new(handler)
        {
            BaseAddress = new Uri(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/")),
            Timeout = TimeSpan.FromSeconds(10),
        };

        using HttpRequestMessage pageRequest = new(HttpMethod.Get, "/");
        pageRequest.Headers.Host = host;
        pageRequest.Headers.Accept.ParseAdd("text/html");
        using HttpResponseMessage page = await client.SendAsync(pageRequest, cancellationToken).ConfigureAwait(false);
        var html = await page.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!page.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"the challenge page answered {(int)page.StatusCode} {page.StatusCode}");
        }

        var match = ChallengeInput().Match(html);
        if (!match.Success)
        {
            throw new InvalidOperationException($"the challenge page ({html.Length} chars) carries no challenge input");
        }

        var challenge = match.Groups[1].Value;
        var solution = Solve(challenge, difficulty);

        using FormUrlEncodedContent form = new(new Dictionary<string, string>
        {
            ["challenge"] = challenge,
            ["solution"] = solution,
            ["return"] = "/",
        });
        using HttpRequestMessage post = new(HttpMethod.Post, "/.well-known/paduraksa/pow") { Content = form };
        post.Headers.Host = host;
        using HttpResponseMessage verified = await client.SendAsync(post, cancellationToken).ConfigureAwait(false);
        if (verified.StatusCode != HttpStatusCode.SeeOther)
        {
            throw new InvalidOperationException($"the verify endpoint answered {(int)verified.StatusCode} {verified.StatusCode}, not 303");
        }

        if (verified.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            foreach (var cookie in cookies)
            {
                if (cookie.StartsWith(CookieName + "=", StringComparison.Ordinal))
                {
                    var end = cookie.IndexOf(';', StringComparison.Ordinal);
                    return end < 0 ? cookie : cookie[..end];
                }
            }
        }

        throw new InvalidOperationException("the verify endpoint's 303 carried no paduraksa_pow cookie");
    }

    /// <summary>The smallest counter whose <c>SHA-256(challenge + ":" + counter)</c> has enough leading zero bits.</summary>
    private static string Solve(string challenge, int difficulty)
    {
        var prefix = Encoding.ASCII.GetBytes(challenge + ":");
        var buffer = new byte[prefix.Length + 20];
        prefix.CopyTo(buffer, 0);
        Span<char> digits = stackalloc char[20];
        Span<byte> hash = stackalloc byte[32];
        for (long counter = 0; ; counter++)
        {
            _ = counter.TryFormat(digits, out var digitCount, provider: CultureInfo.InvariantCulture);
            for (var i = 0; i < digitCount; i++)
            {
                buffer[prefix.Length + i] = (byte)digits[i];
            }

            _ = SHA256.HashData(buffer.AsSpan(0, prefix.Length + digitCount), hash);
            if (LeadingZeroBits(hash) >= difficulty)
            {
                return counter.ToString(CultureInfo.InvariantCulture);
            }
        }
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

            bits += BitOperations.LeadingZeroCount((uint)value) - 24;
            break;
        }

        return bits;
    }
}
