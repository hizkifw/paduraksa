using System.Globalization;

using Microsoft.AspNetCore.Http;

using Paduraksa.Core.Text;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Snapshot;

namespace Paduraksa.Proxy.Gate;

/// <summary>
/// <c>POST /.well-known/paduraksa/pow</c>: reads the page's <c>challenge</c>, <c>solution</c> and <c>return</c>, checks the
/// challenge and the work, and answers <c>303</c> to the return target with the token cookie. Cold, bounded and allowed to
/// allocate: at most 2 KiB in, a 51-character challenge, a 20-digit solution and a 2 KiB return target.
/// </summary>
/// <remarks>
/// The challenge is stateless, so a solved one can be redeemed again inside its ten minutes. That only yields a cookie
/// for the same host and client address, which is what makes it acceptable: there is no table to grow, and a replay gains
/// nothing a second solve would not. An expired challenge answers the same 303 as a success (without a cookie), so the
/// visitor simply gets a fresh challenge instead of an error page.
/// </remarks>
internal static class ProofOfWorkEndpoint
{
    private const int MaxBodyBytes = 2048;
    private const int MaxFieldNameLength = 16;

    public static async Task HandleAsync(HttpContext context, CompiledHost host, ProofOfWorkRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(runtime);

        if (runtime.Key is not { } key)
        {
            runtime.ReportUnavailable();
            await ResponseWriter.WriteAsync(context, ProofOfWorkResponses.Unavailable).ConfigureAwait(false);
            return;
        }

        if (context.Request.ContentLength > MaxBodyBytes)
        {
            await ResponseWriter.WriteAsync(context, ProofOfWorkResponses.BodyTooLarge).ConfigureAwait(false);
            return;
        }

        if (!(context.Request.ContentType?.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            runtime.Stats.CountVerifyRejected();
            await ResponseWriter.WriteAsync(context, ProofOfWorkResponses.VerificationFailed).ConfigureAwait(false);
            return;
        }

        var buffer = new byte[MaxBodyBytes + 1];
        var total = await ReadBoundedAsync(context.Request.Body, buffer, context.RequestAborted).ConfigureAwait(false);

        if (total > MaxBodyBytes)
        {
            await ResponseWriter.WriteAsync(context, ProofOfWorkResponses.BodyTooLarge).ConfigureAwait(false);
            return;
        }

        Span<char> challenge = stackalloc char[ProofOfWorkChallenge.EncodedLength + 1];
        Span<char> solution = stackalloc char[ProofOfWorkChallenge.MaxSolutionLength + 1];
        Span<char> returnTarget = stackalloc char[MaxBodyBytes + 1];
        if (!TryParseForm(buffer.AsSpan(0, total), challenge, out var challengeLength, solution, out var solutionLength, returnTarget, out var returnLength))
        {
            runtime.Stats.CountVerifyRejected();
            await ResponseWriter.WriteAsync(context, ProofOfWorkResponses.VerificationFailed).ConfigureAwait(false);
            return;
        }

        var state = context.Features.Get<IGateFeature>()?.State;
        var client = state?.ClientAddress ?? IpKey.Unspecified;
        var now = runtime.Time.GetUtcNow().ToUnixTimeSeconds();
        if (now is < 0 or > uint.MaxValue)
        {
            await ResponseWriter.WriteAsync(context, ProofOfWorkResponses.Unavailable).ConfigureAwait(false);
            return;
        }

        var valid = ProofOfWorkChallenge.TryValidate(
            challenge[..challengeLength], key, host.ProofOfWorkHostScope, client, now, out _, out var difficulty, out var problem);
        if (!valid)
        {
            if (problem == ProofOfWorkChallengeProblem.Expired)
            {
                runtime.Stats.CountExpired();
                Redirect(context, SanitizeReturn(returnTarget[..returnLength]));
                return;
            }

            runtime.Stats.CountVerifyRejected();
            await ResponseWriter.WriteAsync(context, ProofOfWorkResponses.VerificationFailed).ConfigureAwait(false);
            return;
        }

        if (difficulty < host.ProofOfWork.Difficulty
            || !ProofOfWorkChallenge.TryVerifySolution(challenge[..challengeLength], solution[..solutionLength], difficulty))
        {
            runtime.Stats.CountVerifyRejected();
            await ResponseWriter.WriteAsync(context, ProofOfWorkResponses.VerificationFailed).ConfigureAwait(false);
            return;
        }

        var lifetime = (long)host.ProofOfWork.TokenLifetimeHours * 3600;
        var expiresAt = now + lifetime;
        if (expiresAt > uint.MaxValue)
        {
            await ResponseWriter.WriteAsync(context, ProofOfWorkResponses.Unavailable).ConfigureAwait(false);
            return;
        }

        Span<char> token = stackalloc char[ProofOfWorkToken.EncodedLength];
        if (!ProofOfWorkToken.TryEncode(key, host.ProofOfWorkHostScope, client, (uint)expiresAt, difficulty, token, out var tokenLength))
        {
            await ResponseWriter.WriteAsync(context, ProofOfWorkResponses.Unavailable).ConfigureAwait(false);
            return;
        }

        runtime.Stats.CountSolved();
        context.Response.Headers.SetCookie = Cookie(token[..tokenLength], lifetime, secure: state?.IsHttps == true);
        Redirect(context, SanitizeReturn(returnTarget[..returnLength]));
    }

    // Secure follows the trusted scheme (a TLS-terminating proxy counts), not the connection's own.
    internal static string Cookie(ReadOnlySpan<char> token, long lifetimeSeconds, bool secure) => string.Concat(
        ProofOfWorkToken.CookieName,
        "=",
        token.ToString(),
        "; Path=/; HttpOnly; SameSite=Lax; Max-Age=",
        lifetimeSeconds.ToString(CultureInfo.InvariantCulture),
        secure ? "; Secure" : string.Empty);

    /// <summary>
    /// A local path only: starts with one <c>/</c>, not <c>//</c> or <c>/\</c> (both are read as another authority),
    /// has no control character or space, and stays visible ASCII so the <c>Location</c> header is unambiguous.
    /// </summary>
    internal static string SanitizeReturn(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty || value[0] != '/' || (value.Length > 1 && value[1] is '/' or '\\')
            || value.IndexOfAny(AsciiChars.ControlOrSpace) >= 0)
        {
            return "/";
        }

        foreach (var c in value)
        {
            if (c > '~')
            {
                return "/";
            }
        }

        return value.ToString();
    }

    private static void Redirect(HttpContext context, string location)
    {
        context.Response.StatusCode = StatusCodes.Status303SeeOther;
        context.Response.Headers.Location = location;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.ContentLength = 0;
    }

    private static async Task<int> ReadBoundedAsync(Stream body, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total <= MaxBodyBytes)
        {
            var read = await body.ReadAsync(buffer.AsMemory(total, MaxBodyBytes + 1 - total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static bool TryParseForm(
        ReadOnlySpan<byte> body,
        Span<char> challenge,
        out int challengeLength,
        Span<char> solution,
        out int solutionLength,
        Span<char> returnTarget,
        out int returnLength)
    {
        challengeLength = 0;
        solutionLength = 0;
        returnLength = 0;
        var sawChallenge = false;
        var sawSolution = false;
        var sawReturn = false;
        Span<char> name = stackalloc char[MaxFieldNameLength];
        Span<char> value = stackalloc char[MaxBodyBytes];
        var rest = body;
        while (!rest.IsEmpty)
        {
            var ampersand = rest.IndexOf((byte)'&');
            var pair = ampersand < 0 ? rest : rest[..ampersand];
            rest = ampersand < 0 ? default : rest[(ampersand + 1)..];
            if (pair.IsEmpty)
            {
                continue;
            }

            var equals = pair.IndexOf((byte)'=');
            if (equals <= 0
                || !TryDecode(pair[..equals], name, out var nameLength)
                || !TryDecode(pair[(equals + 1)..], value, out var valueLength))
            {
                return false;
            }

            var field = name[..nameLength];
            if (field.SequenceEqual("challenge"))
            {
                if (sawChallenge || valueLength > challenge.Length)
                {
                    return false;
                }

                value[..valueLength].CopyTo(challenge);
                challengeLength = valueLength;
                sawChallenge = true;
            }
            else if (field.SequenceEqual("solution"))
            {
                if (sawSolution || valueLength > solution.Length)
                {
                    return false;
                }

                value[..valueLength].CopyTo(solution);
                solutionLength = valueLength;
                sawSolution = true;
            }
            else if (field.SequenceEqual("return"))
            {
                if (sawReturn || valueLength > returnTarget.Length)
                {
                    return false;
                }

                value[..valueLength].CopyTo(returnTarget);
                returnLength = valueLength;
                sawReturn = true;
            }
        }

        if (!sawChallenge || !sawSolution)
        {
            return false;
        }

        if (!sawReturn)
        {
            returnTarget[0] = '/';
            returnLength = 1;
        }

        return true;
    }

    /// <summary>One <c>application/x-www-form-urlencoded</c> field: <c>+</c> is a space and <c>%XX</c> a byte. All ASCII in
    /// practice; a byte above 0x7F becomes its Latin-1 character and the return check then falls back to <c>/</c>.</summary>
    private static bool TryDecode(ReadOnlySpan<byte> encoded, Span<char> destination, out int written)
    {
        written = 0;
        for (var i = 0; i < encoded.Length; i++)
        {
            var b = encoded[i];
            if (b == '+')
            {
                b = (byte)' ';
            }
            else if (b == '%')
            {
                if (i + 2 >= encoded.Length || !TryHex(encoded[i + 1], out var high) || !TryHex(encoded[i + 2], out var low))
                {
                    written = 0;
                    return false;
                }

                b = (byte)((high << 4) | low);
                i += 2;
            }

            if (written >= destination.Length)
            {
                written = 0;
                return false;
            }

            destination[written++] = (char)b;
        }

        return true;
    }

    private static bool TryHex(byte value, out int digit)
    {
        if (value is >= (byte)'0' and <= (byte)'9')
        {
            digit = value - '0';
            return true;
        }

        if (value is >= (byte)'a' and <= (byte)'f')
        {
            digit = value - 'a' + 10;
            return true;
        }

        if (value is >= (byte)'A' and <= (byte)'F')
        {
            digit = value - 'A' + 10;
            return true;
        }

        digit = 0;
        return false;
    }
}
