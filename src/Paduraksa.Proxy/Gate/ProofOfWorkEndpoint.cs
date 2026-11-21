using System.Globalization;
using Microsoft.AspNetCore.Http;
using Paduraksa.Core.Text;
using Paduraksa.Core.Vault;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.ProxyProtocol;

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

        var settings = new ProofOfWorkVerification(
            ProofOfWorkDomain.Gate, host.ProofOfWorkHostScope, host.ProofOfWork.Difficulty, host.ProofOfWork.TokenLifetimeHours, "/", null, runtime.Stats);
        _ = await VerifyAsync(context, key, runtime.Time, settings).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the posted form, checks the challenge and the work, and answers: <c>303</c> to the return target with the token cookie on
    /// success, the same <c>303</c> without a cookie for an expired challenge, else <c>403</c> (or <c>413</c>). The browser check and the
    /// trap run the same code over their own <paramref name="settings"/>.
    /// </summary>
    internal static async Task<ProofOfWorkVerifyResult> VerifyAsync(HttpContext context, IMacKey key, TimeProvider time, ProofOfWorkVerification settings)
    {
        var stats = settings.Stats;
        if (context.Request.ContentLength > MaxBodyBytes)
        {
            await ResponseWriter.WriteAsync(context, ProofOfWorkResponses.BodyTooLarge).ConfigureAwait(false);
            return ProofOfWorkVerifyResult.Rejected;
        }

        if (!(context.Request.ContentType?.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            stats?.CountVerifyRejected();
            await ResponseWriter.WriteAsync(context, ProofOfWorkResponses.VerificationFailed).ConfigureAwait(false);
            return ProofOfWorkVerifyResult.Rejected;
        }

        var buffer = new byte[MaxBodyBytes + 1];
        var total = await ReadBoundedAsync(context.Request.Body, buffer, context.RequestAborted).ConfigureAwait(false);

        if (total > MaxBodyBytes)
        {
            await ResponseWriter.WriteAsync(context, ProofOfWorkResponses.BodyTooLarge).ConfigureAwait(false);
            return ProofOfWorkVerifyResult.Rejected;
        }

        Span<char> challenge = stackalloc char[ProofOfWorkChallenge.EncodedLength + 1];
        Span<char> solution = stackalloc char[ProofOfWorkChallenge.MaxSolutionLength + 1];
        Span<char> returnTarget = stackalloc char[MaxBodyBytes + 1];
        if (!TryParseForm(buffer.AsSpan(0, total), challenge, out var challengeLength, solution, out var solutionLength, returnTarget, out var returnLength))
        {
            stats?.CountVerifyRejected();
            await ResponseWriter.WriteAsync(context, ProofOfWorkResponses.VerificationFailed).ConfigureAwait(false);
            return ProofOfWorkVerifyResult.Rejected;
        }

        var state = context.Features.Get<IGateFeature>()?.State;
        var client = state?.ClientAddress ?? IpKey.Unspecified;
        var now = time.GetUtcNow().ToUnixTimeSeconds();
        if (now is < 0 or > uint.MaxValue)
        {
            await ResponseWriter.WriteAsync(context, ProofOfWorkResponses.Unavailable).ConfigureAwait(false);
            return ProofOfWorkVerifyResult.Rejected;
        }

        var valid = ProofOfWorkChallenge.TryValidate(
            settings.Domain, challenge[..challengeLength], key, settings.HostScope, client, now, out var issuedAt, out var difficulty, out var problem);
        if (!valid)
        {
            if (problem == ProofOfWorkChallengeProblem.Expired)
            {
                stats?.CountExpired();
                Redirect(context, SanitizeReturn(returnTarget[..returnLength], settings.ReturnPrefix));
                return ProofOfWorkVerifyResult.Rejected;
            }

            stats?.CountVerifyRejected();
            await ResponseWriter.WriteAsync(context, ProofOfWorkResponses.VerificationFailed).ConfigureAwait(false);
            return ProofOfWorkVerifyResult.Rejected;
        }

        if (difficulty < settings.Difficulty
            || !ProofOfWorkChallenge.TryVerifySolution(challenge[..challengeLength], solution[..solutionLength], difficulty))
        {
            stats?.CountVerifyRejected();
            await ResponseWriter.WriteAsync(context, ProofOfWorkResponses.VerificationFailed).ConfigureAwait(false);
            return ProofOfWorkVerifyResult.Rejected;
        }

        var lifetime = (long)settings.LifetimeHours * 3600;
        var expiresAt = now + lifetime;
        if (expiresAt > uint.MaxValue)
        {
            await ResponseWriter.WriteAsync(context, ProofOfWorkResponses.Unavailable).ConfigureAwait(false);
            return ProofOfWorkVerifyResult.Rejected;
        }

        Span<char> token = stackalloc char[ProofOfWorkToken.EncodedLength];
        if (!ProofOfWorkToken.TryEncode(settings.Domain, key, settings.HostScope, client, (uint)expiresAt, difficulty, token, out var tokenLength))
        {
            await ResponseWriter.WriteAsync(context, ProofOfWorkResponses.Unavailable).ConfigureAwait(false);
            return ProofOfWorkVerifyResult.Rejected;
        }

        stats?.CountSolved();
        context.Response.Headers.SetCookie = Cookie(settings.Domain.CookieName, token[..tokenLength], lifetime, secure: state?.IsHttps == true, settings.CookiePath);
        var location = SanitizeReturn(returnTarget[..returnLength], settings.ReturnPrefix);
        Redirect(context, location);
        return new ProofOfWorkVerifyResult(ProofOfWorkVerifyOutcome.Solved, issuedAt, location);
    }

    // Secure follows the trusted scheme (a TLS-terminating proxy counts), not the connection's own.
    internal static string Cookie(ReadOnlySpan<char> token, long lifetimeSeconds, bool secure) =>
        Cookie(ProofOfWorkToken.CookieName, token, lifetimeSeconds, secure, "/");

    internal static string Cookie(string name, ReadOnlySpan<char> token, long lifetimeSeconds, bool secure, string path) => string.Concat(
        name,
        "=",
        token.ToString(),
        "; Path=",
        path,
        "; HttpOnly; SameSite=Lax; Max-Age=",
        lifetimeSeconds.ToString(CultureInfo.InvariantCulture),
        secure ? "; Secure" : string.Empty);

    /// <summary>
    /// A local path only: starts with one <c>/</c>, not <c>//</c> or <c>/\</c> (both are read as another authority),
    /// has no control character or space, and stays visible ASCII so the <c>Location</c> header is unambiguous.
    /// </summary>
    internal static string SanitizeReturn(ReadOnlySpan<char> value) => SanitizeReturn(value, null);

    /// <summary>
    /// <see cref="SanitizeReturn(ReadOnlySpan{char})"/>, and when <paramref name="prefix"/> is set the target also has to lie under it:
    /// the trap sends a solver back into the trap, never out of it.
    /// </summary>
    internal static string SanitizeReturn(ReadOnlySpan<char> value, string? prefix)
    {
        var target = SanitizeLocal(value);
        return prefix is null || target.StartsWith(prefix, StringComparison.Ordinal) ? target : prefix;
    }

    private static string SanitizeLocal(ReadOnlySpan<char> value)
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

    /// <summary>The three fields of the posted form (<c>challenge</c>, <c>solution</c> and <c>return</c>) into the caller's buffers; false when it isn't a form this endpoint reads.</summary>
    internal static bool TryParseForm(
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
                if (i + 2 >= encoded.Length || !TryHexByte((char)encoded[i + 1], (char)encoded[i + 2], out b))
                {
                    written = 0;
                    return false;
                }

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

    /// <summary>The byte of two hex digits. The digits are checked first because number parsing also accepts trailing NULs.</summary>
    internal static bool TryHexByte(char high, char low, out byte value)
    {
        Span<char> pair = [high, low];
        value = 0;
        return char.IsAsciiHexDigit(high) && char.IsAsciiHexDigit(low)
            && byte.TryParse(pair, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
    }
}

/// <summary>What one verify endpoint checks against: the flow's MAC domain, the host's scope, the least difficulty, how long a token lasts, and the cookie's path.</summary>
/// <param name="Domain">Separates this flow's challenges and tokens from the other's.</param>
/// <param name="HostScope">The host's 16 scope bytes.</param>
/// <param name="Difficulty">The least difficulty a challenge may carry.</param>
/// <param name="LifetimeHours">How long the cookie lasts.</param>
/// <param name="CookiePath">The cookie's <c>Path</c> attribute.</param>
/// <param name="ReturnPrefix">When set, a return target outside it is replaced by it.</param>
/// <param name="Stats">The counters to move; null: none (the trap keeps its own account).</param>
internal readonly record struct ProofOfWorkVerification(
    ProofOfWorkDomain Domain, byte[] HostScope, int Difficulty, int LifetimeHours, string CookiePath, string? ReturnPrefix, ProofOfWorkStats? Stats);

/// <summary>What a verify request came to.</summary>
internal enum ProofOfWorkVerifyOutcome : byte
{
    /// <summary>Not a valid solution (or a challenge that expired, which is redirected without a cookie).</summary>
    Rejected,

    /// <summary>A valid solution: the cookie was set and the visitor redirected.</summary>
    Solved,
}

/// <summary>The outcome of <see cref="ProofOfWorkEndpoint.VerifyAsync"/>; the rest is for a solved one.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="IssuedAt">When the solved challenge was issued, in unix seconds.</param>
/// <param name="Return">Where the visitor was sent.</param>
internal readonly record struct ProofOfWorkVerifyResult(ProofOfWorkVerifyOutcome Outcome, uint IssuedAt = 0, string? Return = null)
{
    public static ProofOfWorkVerifyResult Rejected => new(ProofOfWorkVerifyOutcome.Rejected);
}
