using Paduraksa.Core.Vault;

namespace Paduraksa.Tests.Unit.Vault;

/// <summary>The small pieces of logic in the vault contracts: stable names, host normalisation, injection validation.</summary>
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
