using Paduraksa.Core.Vault;

namespace Paduraksa.Tests.Unit.Vault;

public sealed class VaultContractsTests
{
    [Fact]
    public void NamesArePinnedAndRoundTrip()
    {
        Assert.Equal(["ecdsa-p256", "ecdsa-p384", "rsa-2048", "rsa-3072", "rsa-4096"], Enum.GetValues<KeySpec>().Select(k => k.ToName()));
        Assert.Equal(["hmac-sha256", "hmac-sha384", "hmac-sha512"], Enum.GetValues<MacAlgorithm>().Select(a => a.ToName()));
        Assert.Equal(
            ["dns-api-token", "eab-hmac-key", "totp-secret", "tsig-key", "hmac-signing-key"],
            Enum.GetValues<SecretKind>().Select(k => k.ToName()));
        Assert.Equal(["ES256", "ES384", "RS256"], Enum.GetValues<JwsAlgorithm>().Select(a => a.ToName()));

        Assert.All(Enum.GetValues<KeySpec>(), k => Assert.Equal(k, VaultNames.ParseKeySpec(k.ToName())));
        Assert.All(Enum.GetValues<MacAlgorithm>(), a => Assert.Equal(a, VaultNames.ParseMacAlgorithm(a.ToName())));
        Assert.All(Enum.GetValues<SecretKind>(), k => Assert.Equal(k, VaultNames.ParseSecretKind(k.ToName())));
        Assert.All(Enum.GetValues<JwsAlgorithm>(), a => Assert.Equal(a, JwsAlgorithmNames.Parse(a.ToName())));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("ECDSA-P256")]
    public void ParseRejectsAnythingElse(string name)
    {
        Assert.Throws<FormatException>(() => VaultNames.ParseKeySpec(name));
        Assert.Throws<FormatException>(() => VaultNames.ParseMacAlgorithm(name));
        Assert.Throws<FormatException>(() => VaultNames.ParseSecretKind(name));
        Assert.Throws<FormatException>(() => JwsAlgorithmNames.Parse(name));
    }

    [Theory]
    [InlineData("api.cloudflare.com", "api.cloudflare.com")]
    [InlineData("  API.Cloudflare.COM. ", "api.cloudflare.com")]
    [InlineData("bücher.example", "xn--bcher-kva.example")]
    public void HostNamesNormalise(string input, string expected) =>
        Assert.Equal(expected, VaultHostNames.Normalize(input));

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("https://api.cloudflare.com")]
    [InlineData("api.cloudflare.com:443")]
    [InlineData("api.cloudflare.com/client/v4")]
    [InlineData("*.cloudflare.com")]
    [InlineData("user@api.cloudflare.com")]
    [InlineData("[::1]")]
    [InlineData("a b.example")]
    public void HostNamesRefuseAnythingButAPlainHost(string input) =>
        Assert.Throws<ArgumentException>(() => VaultHostNames.Normalize(input));

    [Theory]
    [InlineData("api.example.com", "api.example.com")]
    [InlineData("API.example.com:443", "api.example.com")]
    [InlineData("API.example.com.:8443", "api.example.com:8443")]
    [InlineData("bücher.example:8443", "xn--bcher-kva.example:8443")]
    [InlineData("localhost:1", "localhost:1")]
    [InlineData("localhost:65535", "localhost:65535")]
    public void AuthoritiesNormaliseAndWriteTheDefaultPortAsNone(string input, string expected) =>
        Assert.Equal(expected, VaultHostNames.NormalizeAuthority(input));

    [Theory]
    [InlineData("api.example.com:")]
    [InlineData("api.example.com:0")]
    [InlineData("api.example.com:65536")]
    [InlineData("api.example.com:80a")]
    [InlineData("api.example.com:-1")]
    [InlineData("api.example.com:8443:1")]
    [InlineData("api.example.com:123456")]
    [InlineData("[::1]:8443")]
    [InlineData("::1")]
    [InlineData("https://api.example.com:8443")]
    [InlineData(":8443")]
    [InlineData("a.example/x:8443")]
    public void AuthoritiesRefuseABadPortOrAnythingButAHost(string input) =>
        Assert.Throws<ArgumentException>(() => VaultHostNames.NormalizeAuthority(input));

    [Fact]
    public void InjectionNormalisesHostsAndKeepsOneOfEach()
    {
        var injection = SecretInjection.Bearer(["API.desec.io", "api.desec.io.", "dns.example"]);

        Assert.Equal(SecretInjectionKind.Authorization, injection.Kind);
        Assert.Equal("Bearer", injection.Scheme);
        Assert.Equal(["api.desec.io", "dns.example"], injection.AllowedHosts);
        Assert.Equal("Token", SecretInjection.Authorization("Token", ["desec.io"]).Scheme);
    }

    [Fact]
    public void InjectionRequiresHosts()
    {
        Assert.Throws<ArgumentException>(() => SecretInjection.Bearer([]));
        Assert.Throws<ArgumentException>(() => SecretInjection.Bearer(["https://api.example"]));
        Assert.Equal(["api.example:8443", "api.example"], SecretInjection.Bearer(["api.example:8443", "API.example:443"]).AllowedHosts);
    }

    [Theory]
    [InlineData("Host")]
    [InlineData("authorization")]
    [InlineData("Proxy-Authorization")]
    [InlineData("Transfer-Encoding")]
    [InlineData("Cookie")]
    [InlineData("X Auth")]
    [InlineData("X-Auth:")]
    [InlineData("")]
    public void HeaderInjectionRefusesReservedAndNonTokenNames(string name) =>
        Assert.Throws<ArgumentException>(() => SecretInjection.Header(name, ["api.example"]));

    [Fact]
    public void HeaderInjectionAcceptsAProviderHeader() =>
        Assert.Equal("X-Auth-Token", SecretInjection.Header(" X-Auth-Token ", ["api.example"]).HeaderName);

    [Theory]
    [InlineData("bad scheme")]
    [InlineData("")]
    [InlineData("Bearer,")]
    public void AuthorizationSchemeMustBeAToken(string scheme) =>
        Assert.Throws<ArgumentException>(() => SecretInjection.Authorization(scheme, ["api.example"]));

    [Theory]
    [InlineData("a:b")]
    [InlineData("a\nb")]
    public void BasicAuthRefusesColonAndControlCharactersInTheUserName(string username) =>
        Assert.Throws<ArgumentException>(() => SecretInjection.BasicAuth(username, ["api.example"]));

    [Fact]
    public void BasicAuthKeepsTheUserName() =>
        Assert.Equal("acme", SecretInjection.BasicAuth("acme", ["api.example"]).Username);
}
