using System.Text.Json;

using Paduraksa.Application.Certificates;
using Paduraksa.Contracts.Certificates;
using Paduraksa.Core.Vault;

namespace Paduraksa.Tests.Unit.Certificates;

public sealed class CertificateOperationContractTests
{
    [Fact]
    public void KeySpecTravelsAsItsStableNameInEveryRequest()
    {
        Assert.Equal(
            """{"Names":["a.example.com"],"KeySpec":"rsa-3072","Validity":null,"Label":null}""",
            JsonSerializer.Serialize(new SelfSignedCertificateRequest(["a.example.com"], KeySpec.Rsa3072)));
        Assert.Equal(KeySpec.EcdsaP384, JsonSerializer.Deserialize<LocalCaRequest>("""{"KeySpec":"ecdsa-p384"}""")!.KeySpec);
        Assert.Equal(KeySpec.Rsa4096, JsonSerializer.Deserialize<LocalCaLeafRequest>("""{"Names":["a.lan"],"KeySpec":"rsa-4096"}""")!.KeySpec);
        Assert.Equal(KeySpec.Rsa2048, JsonSerializer.Deserialize<ReissueRequest>("""{"KeySpec":"rsa-2048"}""")!.KeySpec);
        Assert.Null(JsonSerializer.Deserialize<ReissueRequest>("""{"KeySpec":null}""")!.KeySpec);
        Assert.Null(JsonSerializer.Deserialize<ReissueRequest>("{}")!.KeySpec);
        Assert.Equal("""{"KeySpec":"ecdsa-p256","Validity":null}""", JsonSerializer.Serialize(new ReissueRequest(KeySpec.EcdsaP256)));
    }

    [Theory]
    [InlineData("""{"KeySpec":2}""")]
    [InlineData("""{"KeySpec":"rsa-1024"}""")]
    [InlineData("""{"KeySpec":"Rsa2048"}""")]
    public void ANumberOrAnUnknownNameIsRejected(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<LocalCaRequest>(json));

    [Theory]
    [InlineData("WWW.Example.COM", "www.example.com")]
    [InlineData("*.example.com", "*.example.com")]
    [InlineData("192.0.2.1", "192.0.2.1")]
    [InlineData("not a host!", null)]
    [InlineData("", null)]
    public void TheVaultsNameRuleIsTheInventorys(string name, string? expected) =>
        Assert.Equal(expected, new CertificateNameRules().Normalize(name));
}
