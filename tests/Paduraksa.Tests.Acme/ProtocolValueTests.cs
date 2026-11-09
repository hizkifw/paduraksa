using System.Buffers.Text;
using System.Net;
using System.Text;
using System.Text.Json;
using Paduraksa.Acme;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Tests.Acme.Fakes;
using static Paduraksa.Tests.Acme.Fakes.AcmeHarness;

namespace Paduraksa.Tests.Acme;

public sealed class ProtocolValueTests
{
    // Computed independently of this code (Python hashlib over the same string), not by calling the implementation.
    private const string Token = "evaGxfADs6pSRb2LAv9IZf17Dt3juxGJ-PCt92wr-oA";
    private const string Thumbprint = "nP1qzpXGymHBrUEepNY9HCsQk7K8KhOypzEt62jcerQ";
    private const string ExpectedDnsValue = "NGwKoXBgCT8JhEa0bK7AwfSqHyu_ZWeugV07fLGIVq0";

    [Fact]
    public void KeyAuthorizationIsTokenDotThumbprint() =>
        Assert.Equal($"{Token}.{Thumbprint}", AcmeChallenges.KeyAuthorization(Token, Thumbprint));

    [Fact]
    public void Dns01ValueIsTheUnpaddedBase64UrlSha256OfTheKeyAuthorization() =>
        Assert.Equal(ExpectedDnsValue, AcmeChallenges.Dns01TxtValue(AcmeChallenges.KeyAuthorization(Token, Thumbprint)));

    [Theory]
    [InlineData("example.com", "_acme-challenge.example.com")]
    [InlineData("*.example.com", "_acme-challenge.example.com")]
    [InlineData("a.b.example.com", "_acme-challenge.a.b.example.com")]
    public void Dns01RecordNameDropsTheWildcardLabel(string name, string expected) =>
        Assert.Equal(expected, AcmeChallenges.Dns01RecordName(AcmeIdentifier.Dns(name)));

    [Fact]
    public void Dns01CannotValidateAnIpAddress() =>
        Assert.Throws<ArgumentException>(() => AcmeChallenges.Dns01RecordName(AcmeIdentifier.Ip(IPAddress.Parse("192.0.2.1"))));

    [Theory]
    [InlineData("")]
    [InlineData("../../etc/passwd")]
    [InlineData("a/b")]
    [InlineData("tok en")]
    [InlineData("tok.en")]
    public void TokensOutsideBase64UrlAreRefused(string token)
    {
        Assert.Throws<ArgumentException>(() => AcmeChallenges.KeyAuthorization(token, Thumbprint));
        Assert.Throws<ArgumentException>(() => AcmeChallenges.Http01Path(token));
    }

    [Fact]
    public void Http01PathIsUnderWellKnown() =>
        Assert.Equal($"/.well-known/acme-challenge/{Token}", AcmeChallenges.Http01Path(Token));

    [Fact]
    public void IdentifiersHaveTheirProtocolForms()
    {
        Assert.Equal(new AcmeIdentifier("dns", "example.com"), AcmeIdentifier.Dns(" Example.COM "));
        Assert.True(AcmeIdentifier.Dns("*.example.com").IsWildcard);
        Assert.False(AcmeIdentifier.Dns("example.com").IsWildcard);
        Assert.Equal(new AcmeIdentifier("ip", "2001:db8::1"), AcmeIdentifier.Ip(IPAddress.Parse("2001:DB8:0:0:0:0:0:1")));
    }

    [Theory]
    [InlineData("Example.COM.", "example.com")]
    [InlineData("  *.Sub-Domain.example.com ", "*.sub-domain.example.com")]
    [InlineData("xn--mnchen-3ya.de", "xn--mnchen-3ya.de")]
    public void DnsIdentifiersAreLowercasedAndLoseOneTrailingDot(string name, string expected) =>
        Assert.Equal(expected, AcmeIdentifier.Dns(name).Value);

    [Theory]
    [InlineData("münchen.de")]
    [InlineData("exa mple.com")]
    [InlineData("-a.example.com")]
    [InlineData("a-.example.com")]
    [InlineData("a..example.com")]
    [InlineData("*.*.example.com")]
    [InlineData("a*b.example.com")]
    [InlineData("a_b.example.com")]
    [InlineData("*.")]
    [InlineData("\u212A.example.com")]
    public void DnsIdentifiersMustBeAsciiLdh(string name) =>
        Assert.Throws<ArgumentException>(() => AcmeIdentifier.Dns(name));

    [Theory]
    [InlineData("2001:db8:0:1:1:1:1:1", "2001:db8:0:1:1:1:1:1")]
    [InlineData("2001:0db8:0000:0000:0000:0000:0000:0001", "2001:db8::1")]
    [InlineData("2001:db8:0:0:1:0:0:1", "2001:db8::1:0:0:1")]
    [InlineData("2001:DB8:0:1:0:0:0:1", "2001:db8:0:1::1")]
    [InlineData("0:0:0:0:0:0:0:0", "::")]
    [InlineData("::1", "::1")]
    [InlineData("1::", "1::")]
    [InlineData("192.0.2.7", "192.0.2.7")]
    public void IpIdentifiersUseTheRfc5952Form(string address, string expected) =>
        Assert.Equal(expected, AcmeIdentifier.Ip(IPAddress.Parse(address)).Value);

    [Fact]
    public void IpIdentifiersRefuseAScopeId() =>
        Assert.Throws<ArgumentException>(() => AcmeIdentifier.Ip(IPAddress.Parse("fe80::1%2")));

    [Fact]
    public void AriIdMatchesTheExampleOfRfc9773()
    {
        // RFC 9773 Appendix A: AKI 69:88:5B:6B:87:46:40:41:E1:B3:7B:84:7B:A0:AE:2C:DE:01:C8:D4, serial 00:87:65:43:21.
        Assert.Equal(
            "aYhba4dGQEHhs3uEe6CuLN4ByNQ.AIdlQyE",
            AcmeRenewalInfoId.Create("69885b6b87464041e1b37b847ba0ae2cde01c8d4", "0087654321"));
    }

    [Fact]
    public void AriIdFromCertificateInfoKeepsTheLeadingZeroByteOfTheSerial()
    {
        var info = Certificate("69885b6b87464041e1b37b847ba0ae2cde01c8d4", "0087654321");

        Assert.Equal("aYhba4dGQEHhs3uEe6CuLN4ByNQ.AIdlQyE", AcmeRenewalInfoId.Create(info));
        Assert.True(AcmeRenewalInfoId.TryCreate(info, out var id));
        Assert.Equal("aYhba4dGQEHhs3uEe6CuLN4ByNQ.AIdlQyE", id);
    }

    [Fact]
    public void AriIdNeedsAnAuthorityKeyIdentifier()
    {
        var info = Certificate(null, "0087654321");

        Assert.False(AcmeRenewalInfoId.TryCreate(info, out var id));
        Assert.Null(id);
        Assert.Throws<ArgumentException>(() => AcmeRenewalInfoId.Create(info));
    }

    [Fact]
    public void RenewalWindowPicksATimeInsideIt()
    {
        var start = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);
        var info = new AcmeRenewalInfo(start, start.AddHours(10), null, null);

        Assert.Equal(start, info.PickTime(0));
        Assert.Equal(start.AddHours(5), info.PickTime(0.5));
        Assert.Equal(start.AddHours(10), info.PickTime(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => info.PickTime(1.1));
    }

    [Fact]
    public void ProtectedHeaderWithJwkHasNoKid()
    {
        var jwk = new JsonWebKeyPublic("EC", "P-256", "xx", "yy", null, null);
        var json = JsonDocument.Parse(AcmeJws.ProtectedHeader(JwsAlgorithm.ES256, jwk, null, "n0nce", new Uri("https://ca.test/new-account"))).RootElement;

        Assert.Equal("ES256", json.GetProperty("alg").GetString());
        Assert.Equal("n0nce", json.GetProperty("nonce").GetString());
        Assert.Equal("https://ca.test/new-account", json.GetProperty("url").GetString());
        Assert.False(json.TryGetProperty("kid", out _));
        Assert.Equal("EC", json.GetProperty("jwk").GetProperty("kty").GetString());
        Assert.Equal("P-256", json.GetProperty("jwk").GetProperty("crv").GetString());
        Assert.False(json.GetProperty("jwk").TryGetProperty("n", out _));
    }

    [Fact]
    public void ProtectedHeaderWithKidHasNoJwk()
    {
        var kid = new Uri("https://ca.test/acct/7");
        var json = JsonDocument.Parse(AcmeJws.ProtectedHeader(JwsAlgorithm.RS256, null, kid, "n0nce", new Uri("https://ca.test/new-order"))).RootElement;

        Assert.Equal("RS256", json.GetProperty("alg").GetString());
        Assert.Equal("https://ca.test/acct/7", json.GetProperty("kid").GetString());
        Assert.False(json.TryGetProperty("jwk", out _));
    }

    [Fact]
    public void ProtectedHeaderRefusesBothOrNeitherOfJwkAndKid()
    {
        var jwk = new JsonWebKeyPublic("EC", "P-256", "x", "y", null, null);
        var url = new Uri("https://ca.test/x");

        Assert.Throws<ArgumentException>(() => AcmeJws.ProtectedHeader(JwsAlgorithm.ES256, jwk, new Uri("https://ca.test/acct/1"), "n", url));
        Assert.Throws<ArgumentException>(() => AcmeJws.ProtectedHeader(JwsAlgorithm.ES256, null, null, "n", url));
    }

    [Theory]
    [InlineData(MacAlgorithm.HmacSha256, "HS256")]
    [InlineData(MacAlgorithm.HmacSha384, "HS384")]
    [InlineData(MacAlgorithm.HmacSha512, "HS512")]
    public void ExternalAccountBindingIsAJwsMacedOverTheAccountJwk(MacAlgorithm algorithm, string alg)
    {
        var secret = Encoding.ASCII.GetBytes("a-secret-from-the-ca-a-secret-from-the-ca");
        var mac = new FakeMacKey(secret, algorithm);
        var jwk = new JsonWebKeyPublic("EC", "P-256", "xx", "yy", null, null);
        var url = new Uri("https://ca.test/new-account");

        var jws = AcmeJws.ExternalAccountBinding(mac, "eab-kid", url, jwk);

        var header = JsonDocument.Parse(Base64Url.DecodeFromChars(jws.Protected)).RootElement;
        Assert.Equal(alg, header.GetProperty("alg").GetString());
        Assert.Equal("eab-kid", header.GetProperty("kid").GetString());
        Assert.Equal(url.AbsoluteUri, header.GetProperty("url").GetString());
        Assert.False(header.TryGetProperty("nonce", out _));
        var payload = JsonDocument.Parse(Base64Url.DecodeFromChars(jws.Payload)).RootElement;
        Assert.Equal("xx", payload.GetProperty("x").GetString());
        Assert.Equal("EC", payload.GetProperty("kty").GetString());
        Assert.True(mac.Verify(Encoding.ASCII.GetBytes($"{jws.Protected}.{jws.Payload}"), Base64Url.DecodeFromChars(jws.Signature)));
    }

    [Fact]
    public void StatusConverterReadsKnownAndUnknownValues()
    {
        var wire = JsonSerializer.Deserialize(
            """{"status":"processing","challenges":[{"type":"x","url":"https://ca.test/c","status":"somethingnew"}]}""",
            AcmeJsonContext.Default.AuthorizationWire)!;

        Assert.Equal(AcmeStatus.Processing, wire.Status);
        Assert.Equal(AcmeStatus.Unknown, wire.Challenges![0].Status);
    }

    private static CertificateInfo Certificate(string? aki, string serial) =>
        new(string.Empty, [], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "CN=Issuer", serial, aki, "ff", "ECDSA P-256", [], string.Empty);
}
