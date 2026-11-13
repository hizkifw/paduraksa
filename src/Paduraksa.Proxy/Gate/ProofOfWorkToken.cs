using System.Buffers.Binary;
using System.Buffers.Text;
using Microsoft.AspNetCore.Http;
using Paduraksa.Core.Vault;
using Paduraksa.Proxy.Net;
using Paduraksa.ProxyProtocol;

namespace Paduraksa.Proxy.Gate;

/// <summary>Why a <c>paduraksa_pow</c> cookie didn't let a request through.</summary>
internal enum ProofOfWorkTokenProblem : byte
{
    None,

    /// <summary>There is no such cookie.</summary>
    Missing,

    /// <summary>More than one <c>paduraksa_pow</c> cookie: ambiguous, so none is trusted.</summary>
    Duplicate,

    /// <summary>Not a token this build issues (bad length, base64url or version).</summary>
    Malformed,

    /// <summary>The tag doesn't verify for this host and client address: altered, or issued for another scope.</summary>
    BadTag,

    /// <summary>The tag verifies, but its expiry has passed.</summary>
    Expired,

    /// <summary>The tag verifies, but the token is easier than the host's current difficulty.</summary>
    TooEasy,
}

/// <summary>
/// The <c>paduraksa_pow</c> token: <c>version | expiresAt (uint32) | difficulty | tag (16 bytes)</c>, base64url without
/// padding. The tag is the first 16 bytes of HMAC-SHA-256 over a domain-separated input that includes the host's stable
/// scope and the client address masked to /32 (IPv4) or /64 (IPv6), so a token is useless on another host or from
/// another network. Parsing, decoding and verification run on the request path and allocate nothing.
/// </summary>
internal static class ProofOfWorkToken
{
    public const string CookieName = "paduraksa_pow";
    public const byte Version = 1;
    public const int TagLength = 16;
    public const int ValueLength = 22;
    public const int EncodedLength = 30;

    private static ReadOnlySpan<byte> Prefix => "paduraksa-pow-token-v1"u8;

    /// <summary>The longest cookie value the gate parses; a longer one is malformed.</summary>
    public const int MaxCookieLength = EncodedLength;

    /// <summary>Reads the cookie from the raw <c>Cookie</c> header, never through <c>Request.Cookies</c> (which allocates a dictionary).</summary>
    internal static bool TryReadCookie(HttpRequest request, Span<char> value, out int length, out ProofOfWorkTokenProblem problem)
    {
        value = value[..Math.Min(value.Length, MaxCookieLength + 1)];
        length = 0;
        problem = ProofOfWorkTokenProblem.Missing;
        var found = false;
        var headers = request.Headers.Cookie;
        for (var h = 0; h < headers.Count; h++)
        {
            if (headers[h] is not { } line)
            {
                continue;
            }

            var rest = line.AsSpan();
            while (!rest.IsEmpty)
            {
                var semicolon = rest.IndexOf(';');
                var pair = semicolon < 0 ? rest : rest[..semicolon];
                rest = semicolon < 0 ? default : rest[(semicolon + 1)..];
                var equals = pair.IndexOf('=');
                if (equals <= 0 || !pair[..equals].Trim().Equals(CookieName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (found)
                {
                    length = 0;
                    problem = ProofOfWorkTokenProblem.Duplicate;
                    return false;
                }

                found = true;
                var cookie = pair[(equals + 1)..].Trim();
                if (cookie.Length > value.Length)
                {
                    length = 0;
                    problem = ProofOfWorkTokenProblem.Malformed;
                    return false;
                }

                cookie.CopyTo(value);
                length = cookie.Length;
            }
        }

        if (!found)
        {
            problem = ProofOfWorkTokenProblem.Missing;
            return false;
        }

        problem = ProofOfWorkTokenProblem.None;
        return true;
    }

    /// <summary>
    /// Checks a decoded cookie value. <paramref name="nowSeconds"/> is the process clock in unix seconds; the tag is
    /// checked before the expiry and the difficulty, so a tampered token always reports <see cref="ProofOfWorkTokenProblem.BadTag"/>.
    /// </summary>
    internal static bool TryValidate(
        ReadOnlySpan<char> value,
        IMacKey key,
        ReadOnlySpan<byte> hostScope,
        IpKey client,
        long nowSeconds,
        int requiredDifficulty,
        out uint expiresAt,
        out int difficulty,
        out ProofOfWorkTokenProblem problem)
    {
        expiresAt = 0;
        difficulty = 0;
        if (value.Length != EncodedLength || hostScope.Length != 16)
        {
            problem = ProofOfWorkTokenProblem.Malformed;
            return false;
        }

        // TryDecodeFromChars throws, not returns false, on a character outside the alphabet or nonzero spare bits in the
        // last one, and the cookie is the client's: checked first, so a forged one is malformed, not a 500 (and no exception).
        Span<byte> decoded = stackalloc byte[ValueLength];
        if (!Base64Url.IsValid(value, out var validLength)
            || validLength != ValueLength
            || !Base64Url.TryDecodeFromChars(value, decoded, out var decodedLength)
            || decodedLength != ValueLength
            || decoded[0] != Version)
        {
            problem = ProofOfWorkTokenProblem.Malformed;
            return false;
        }

        expiresAt = BinaryPrimitives.ReadUInt32BigEndian(decoded[1..]);
        difficulty = decoded[5];
        Span<byte> tag = stackalloc byte[TagLength];
        decoded[6..].CopyTo(tag);

        Span<byte> input = stackalloc byte[128];
        var written = WriteMacInput(input, expiresAt, difficulty, hostScope, client);
        if (!key.VerifyTruncated(input[..written], tag))
        {
            problem = ProofOfWorkTokenProblem.BadTag;
            return false;
        }

        if (nowSeconds < 0 || expiresAt <= nowSeconds)
        {
            problem = ProofOfWorkTokenProblem.Expired;
            return false;
        }

        if (difficulty < requiredDifficulty)
        {
            problem = ProofOfWorkTokenProblem.TooEasy;
            return false;
        }

        problem = ProofOfWorkTokenProblem.None;
        return true;
    }

    /// <summary>Mints a token value (base64url, no padding) for a solved challenge or a test.</summary>
    internal static bool TryEncode(
        IMacKey key,
        ReadOnlySpan<byte> hostScope,
        IpKey client,
        uint expiresAt,
        int difficulty,
        Span<char> destination,
        out int written)
    {
        Span<byte> value = stackalloc byte[ValueLength];
        value[0] = Version;
        BinaryPrimitives.WriteUInt32BigEndian(value[1..], expiresAt);
        value[5] = (byte)difficulty;

        Span<byte> input = stackalloc byte[128];
        var inputLength = WriteMacInput(input, expiresAt, difficulty, hostScope, client);
        Span<byte> tag = stackalloc byte[64];
        var tagLength = key.Sign(input[..inputLength], tag);
        if (tagLength < TagLength)
        {
            written = 0;
            return false;
        }

        tag[..TagLength].CopyTo(value[6..]);
        written = Base64Url.EncodeToChars(value, destination);
        return true;
    }

    private static int WriteMacInput(Span<byte> destination, uint expiresAt, int difficulty, ReadOnlySpan<byte> hostScope, IpKey client)
    {
        var position = 0;
        Prefix.CopyTo(destination);
        position += Prefix.Length;
        destination[position++] = Version;
        BinaryPrimitives.WriteUInt32BigEndian(destination[position..], expiresAt);
        position += 4;
        destination[position++] = (byte)difficulty;
        hostScope.CopyTo(destination[position..]);
        position += 16;
        client.MaskTo(32, 64).WriteBytes(destination[position..]);
        position += 16;
        return position;
    }
}
