using System.Buffers.Binary;
using System.Buffers.Text;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

using Paduraksa.Core.Vault;
using Paduraksa.Proxy.Net;

namespace Paduraksa.Proxy.Gate;

/// <summary>Why a challenge didn't check out.</summary>
internal enum ProofOfWorkChallengeProblem : byte
{
    None,

    /// <summary>Not a challenge this build issues.</summary>
    Malformed,

    /// <summary>The tag doesn't verify for this host and client address.</summary>
    BadTag,

    /// <summary>Issued more than ten minutes ago.</summary>
    Expired,
}

/// <summary>
/// The proof-of-work challenge: <c>version | issuedAt (uint32) | nonce (16 random bytes) | difficulty | tag (16)</c>,
/// base64url without padding, signed exactly like <see cref="ProofOfWorkToken"/> but with its own domain prefix. It is
/// stateless: the server holds no table, because the tag is the state. A solved challenge may be redeemed again within its
/// ten minutes, but it only yields a cookie for the same host and client address, so that is harmless. A token can't pass
/// as a challenge (or the other way round): each is decoded at its exact length, and their MACs have different prefixes.
/// </summary>
internal static class ProofOfWorkChallenge
{
    public const byte Version = 1;
    public const int NonceLength = 16;
    public const int TagLength = 16;
    public const int ValueLength = 38;
    public const int EncodedLength = 51;

    /// <summary>How long a challenge is valid, in seconds.</summary>
    public const int LifetimeSeconds = 600;

    /// <summary>The largest solution the page may post: a 20-digit counter, enough for any difficulty this build allows.</summary>
    public const int MaxSolutionLength = 20;

    private static ReadOnlySpan<byte> Prefix => "paduraksa-pow-challenge-v1"u8;

    /// <summary>
    /// Signs a fresh challenge for this host and client. The caller passes the nonce buffer so a simulation can avoid
    /// touching the random source; production fills it with <see cref="RandomNumberGenerator.Fill(Span{byte})"/>.
    /// </summary>
    internal static bool TryCreate(
        IMacKey key,
        ReadOnlySpan<byte> hostScope,
        IpKey client,
        uint issuedAt,
        int difficulty,
        Span<byte> nonce,
        Span<char> destination,
        out int written)
    {
        written = 0;
        if (hostScope.Length != 16 || nonce.Length < NonceLength)
        {
            return false;
        }

        Span<byte> value = stackalloc byte[ValueLength];
        value[0] = Version;
        BinaryPrimitives.WriteUInt32BigEndian(value[1..], issuedAt);
        nonce[..NonceLength].CopyTo(value[5..]);
        value[21] = (byte)difficulty;

        Span<byte> input = stackalloc byte[128];
        var inputLength = WriteMacInput(input, issuedAt, value.Slice(5, 16), difficulty, hostScope, client);
        Span<byte> tag = stackalloc byte[64];
        var tagLength = key.Sign(input[..inputLength], tag);
        if (tagLength < TagLength)
        {
            return false;
        }

        tag[..TagLength].CopyTo(value[22..]);
        written = Base64Url.EncodeToChars(value, destination);
        return true;
    }

    /// <summary>Checks a posted challenge: its shape, its tag against this host and client, and its age.</summary>
    internal static bool TryValidate(
        ReadOnlySpan<char> value,
        IMacKey key,
        ReadOnlySpan<byte> hostScope,
        IpKey client,
        long nowSeconds,
        out uint issuedAt,
        out int difficulty,
        out ProofOfWorkChallengeProblem problem)
    {
        issuedAt = 0;
        difficulty = 0;
        if (value.Length != EncodedLength || hostScope.Length != 16)
        {
            problem = ProofOfWorkChallengeProblem.Malformed;
            return false;
        }

        Span<byte> decoded = stackalloc byte[ValueLength];
        if (!Base64Url.TryDecodeFromChars(value, decoded, out var decodedLength)
            || decodedLength != ValueLength
            || decoded[0] != Version)
        {
            problem = ProofOfWorkChallengeProblem.Malformed;
            return false;
        }

        issuedAt = BinaryPrimitives.ReadUInt32BigEndian(decoded[1..]);
        difficulty = decoded[21];
        Span<byte> tag = stackalloc byte[TagLength];
        decoded[22..].CopyTo(tag);

        Span<byte> input = stackalloc byte[128];
        var written = WriteMacInput(input, issuedAt, decoded.Slice(5, NonceLength), difficulty, hostScope, client);
        if (!key.VerifyTruncated(input[..written], tag))
        {
            problem = ProofOfWorkChallengeProblem.BadTag;
            return false;
        }

        if (nowSeconds < 0 || issuedAt > nowSeconds || nowSeconds - issuedAt > LifetimeSeconds)
        {
            problem = ProofOfWorkChallengeProblem.Expired;
            return false;
        }

        problem = ProofOfWorkChallengeProblem.None;
        return true;
    }

    /// <summary>
    /// Verifies the posted counter: SHA-256 over <c>challenge + ":" + solution</c> (UTF-8) has at least
    /// <paramref name="difficulty"/> leading zero bits. The solution is hashed exactly as it was posted, so a leading zero
    /// is part of the work, not normalised away.
    /// </summary>
    internal static bool TryVerifySolution(ReadOnlySpan<char> challenge, ReadOnlySpan<char> solution, int difficulty)
    {
        if (solution.IsEmpty || solution.Length > MaxSolutionLength)
        {
            return false;
        }

        foreach (var c in solution)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        if (challenge.Length > 128)
        {
            return false;
        }

        Span<byte> buffer = stackalloc byte[challenge.Length + 1 + MaxSolutionLength];
        var length = Encoding.ASCII.GetBytes(challenge, buffer);
        buffer[length++] = (byte)':';
        for (var i = 0; i < solution.Length; i++)
        {
            buffer[length++] = (byte)solution[i];
        }

        Span<byte> hash = stackalloc byte[32];
        _ = SHA256.HashData(buffer[..length], hash);
        return CountLeadingZeroBits(hash) >= difficulty;
    }

    /// <summary>The number of leading zero bits in a SHA-256 digest (0-256), used by the verifier and its tests.</summary>
    internal static int CountLeadingZeroBits(ReadOnlySpan<byte> hash)
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

    private static int WriteMacInput(
        Span<byte> destination, uint issuedAt, ReadOnlySpan<byte> nonce, int difficulty, ReadOnlySpan<byte> hostScope, IpKey client)
    {
        var position = 0;
        Prefix.CopyTo(destination);
        position += Prefix.Length;
        destination[position++] = Version;
        BinaryPrimitives.WriteUInt32BigEndian(destination[position..], issuedAt);
        position += 4;
        nonce.CopyTo(destination[position..]);
        position += NonceLength;
        destination[position++] = (byte)difficulty;
        hostScope.CopyTo(destination[position..]);
        position += 16;
        client.MaskTo(32, 64).WriteBytes(destination[position..]);
        position += 16;
        return position;
    }
}
