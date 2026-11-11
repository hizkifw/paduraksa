using System.Security.Cryptography;

using Paduraksa.Core.Vault;

namespace Paduraksa.Fuzz;

/// <summary>
/// The MAC the proof-of-work targets sign and verify with: SHA-256 over a fixed key and the data. It is not an HMAC and holds no key material, which
/// a fuzz harness has no business with; it has the same shape (a tag per input, a shorter tag accepted as a prefix), which is all the parsers see.
/// </summary>
internal sealed class FixedMacKey : IMacKey
{
    private static readonly byte[] Key = [.. Enumerable.Repeat((byte)0x5A, 32)];

    public MacAlgorithm Algorithm => MacAlgorithm.HmacSha256;

    public int TagLength => 32;

    public int Sign(ReadOnlySpan<byte> data, Span<byte> tag)
    {
        Span<byte> input = stackalloc byte[256];
        Key.CopyTo(input);
        data.CopyTo(input[Key.Length..]);
        Span<byte> hash = stackalloc byte[32];
        _ = SHA256.HashData(input[..(Key.Length + data.Length)], hash);
        hash.CopyTo(tag);
        return TagLength;
    }

    public bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> tag) => tag.Length == TagLength && Matches(data, tag);

    public bool VerifyTruncated(ReadOnlySpan<byte> data, ReadOnlySpan<byte> tag) =>
        tag.Length >= IMacKey.MinTruncatedTagLength && tag.Length <= TagLength && Matches(data, tag);

    private bool Matches(ReadOnlySpan<byte> data, ReadOnlySpan<byte> tag)
    {
        Span<byte> expected = stackalloc byte[TagLength];
        _ = Sign(data, expected);
        return CryptographicOperations.FixedTimeEquals(expected[..tag.Length], tag);
    }
}
