using System.Text;

using Paduraksa.Application.Identity;
using Paduraksa.Core.Model;

namespace Paduraksa.Tests.Unit.Identity;

public sealed class ApiTokenFormatTests
{
    private static byte[] Secret(byte seed = 7) => [.. Enumerable.Range(0, 32).Select(i => (byte)(seed + (i * 5)))];

    [Fact]
    public void TheCrcMatchesTheStandardCheckValue() =>
        Assert.Equal(0xCBF43926u, Crc32.Compute(Encoding.ASCII.GetBytes("123456789")));

    [Fact]
    public void ATokenHasTheDocumentedShape()
    {
        var token = ApiTokenFormat.Format(ApiTokenId.New(), Secret());

        var parts = token.Split('_');
        Assert.Equal("pdk", parts[0]);
        Assert.Equal(4, parts.Length);
        Assert.Equal(22, parts[1].Length); // the token ID, base62
        Assert.Equal(44, parts[2].Length); // 32 secret bytes, base62 in two halves
        Assert.Equal(6, parts[3].Length); // CRC-32, base62
        Assert.Equal(78, token.Length);
        Assert.All(token[4..].Where(c => c != '_'), c => Assert.True(char.IsAsciiLetterOrDigit(c)));
    }

    [Fact]
    public void ParsingGivesBackTheIdAndTheSecret()
    {
        var id = ApiTokenId.New();
        var secret = Secret();

        Assert.True(ApiTokenFormat.TryParse(ApiTokenFormat.Format(id, secret), out var parsedId, out var parsedSecret));

        Assert.Equal(id, parsedId);
        Assert.Equal(secret, parsedSecret);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(255)]
    public void ExtremeSecretsRoundTrip(byte fill)
    {
        var secret = Enumerable.Repeat(fill, 32).ToArray();

        Assert.True(ApiTokenFormat.TryParse(ApiTokenFormat.Format(ApiTokenId.New(), secret), out _, out var parsed));
        Assert.Equal(secret, parsed);
    }

    [Fact]
    public void ADifferentSecretOrIdMakesADifferentToken()
    {
        var id = ApiTokenId.New();

        Assert.NotEqual(ApiTokenFormat.Format(id, Secret(1)), ApiTokenFormat.Format(id, Secret(2)));
        Assert.NotEqual(ApiTokenFormat.Format(id, Secret()), ApiTokenFormat.Format(ApiTokenId.New(), Secret()));
    }

    [Fact]
    public void AnyChangeToATokenBreaksTheChecksumOrTheShape()
    {
        var token = ApiTokenFormat.Format(ApiTokenId.New(), Secret());

        for (var i = 0; i < token.Length; i++)
        {
            var changed = token[i] == 'A' ? 'B' : 'A';
            if (token[i] == '_')
            {
                changed = '-';
            }

            Assert.False(ApiTokenFormat.TryParse(token[..i] + changed + token[(i + 1)..], out _, out _), $"position {i}");
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("pdk_")]
    [InlineData("Bearer pdk_x")]
    public void NonTokensAreRefused(string? presented) => Assert.False(ApiTokenFormat.TryParse(presented, out _, out _));

    [Fact]
    public void WrongLengthsAndPrefixesAreRefused()
    {
        var token = ApiTokenFormat.Format(ApiTokenId.New(), Secret());

        Assert.False(ApiTokenFormat.TryParse(token + "0", out _, out _));
        Assert.False(ApiTokenFormat.TryParse(token[..^1], out _, out _));
        Assert.False(ApiTokenFormat.TryParse("pdx" + token[3..], out _, out _));
        Assert.False(ApiTokenFormat.TryParse(token.ToUpperInvariant(), out _, out _));
    }

    [Fact]
    public void ASecretOfTheWrongSizeCannotBeFormatted() =>
        Assert.Throws<ArgumentException>(() => ApiTokenFormat.Format(ApiTokenId.New(), new byte[31]));
}
