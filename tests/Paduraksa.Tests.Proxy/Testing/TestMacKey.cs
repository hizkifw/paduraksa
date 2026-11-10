using System.Security.Cryptography;

using Paduraksa.Core.Vault;

namespace Paduraksa.Tests.Proxy.Testing;

/// <summary>
/// A deterministic HMAC-SHA-256 stand-in that needs no key material (Tests.Proxy may not use <c>HMACSHA256</c>): the tag
/// is SHA-256(key ‖ data), which is stable and collision-free enough for gate tests. Like the vault's handle it accepts a
/// shorter tag as a prefix, which is what the proof-of-work token keeps.
/// </summary>
internal sealed class TestMacKey(byte fill = 0x42) : IMacKey
{
    private readonly byte[] _key = Enumerable.Repeat(fill, 32).ToArray();

    public MacAlgorithm Algorithm => MacAlgorithm.HmacSha256;

    public int TagLength => 32;

    public int Sign(ReadOnlySpan<byte> data, Span<byte> tag)
    {
        if (tag.Length < TagLength)
        {
            throw new ArgumentException("The tag buffer is too short.", nameof(tag));
        }

        Span<byte> input = stackalloc byte[256];
        if (_key.Length + data.Length > input.Length)
        {
            throw new ArgumentException("The test MAC's input is too long.", nameof(data));
        }

        _key.CopyTo(input);
        data.CopyTo(input[_key.Length..]);
        Span<byte> hash = stackalloc byte[32];
        _ = SHA256.HashData(input[..(_key.Length + data.Length)], hash);
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
