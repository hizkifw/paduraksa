using System.Text.Json;

using Paduraksa.Admin.Json;
using Paduraksa.Contracts.Certificates;

namespace Paduraksa.Tests.Unit.Certificates;

public sealed class CaJsonContractTests
{
    private static JsonSerializerOptions Http()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web);
        AdminJsonContext.AddTo(options);
        return options;
    }

    [Fact]
    public void TheRootIsCamelCaseAndRoundTrips()
    {
        CaRootCertificate root = new("ab12", "CN=Paduraksa Local CA (1a2b3c4d)", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(3650));

        var json = JsonSerializer.Serialize(root, Http());

        Assert.Contains("\"sha256Fingerprint\":\"ab12\"", json, StringComparison.Ordinal);
        Assert.Contains("\"subject\":\"CN=Paduraksa Local CA (1a2b3c4d)\"", json, StringComparison.Ordinal);
        Assert.Equal(root, JsonSerializer.Deserialize<CaRootCertificate>(json, Http()));
    }

    [Theory]
    [InlineData("""{"sha256Fingerprint":"a","subject":"s","notBefore":"2026-01-01T00:00:00+00:00","notAfter":"2027-01-01T00:00:00+00:00","extra":1}""")]
    [InlineData("""{"sha256Fingerprint":"a","subject":"s","notBefore":"2026-01-01T00:00:00+00:00"}""")]
    [InlineData("""{"sha256Fingerprint":null,"subject":"s","notBefore":"2026-01-01T00:00:00+00:00","notAfter":"2027-01-01T00:00:00+00:00"}""")]
    public void ReadingIsStrict(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CaRootCertificate>(json, Http()));

    [Fact]
    public void TheContractIsTheOneTheApiWrites()
    {
        // The endpoints answer through the host's HTTP options, so the DTO must be part of the API's source-generated contract.
        CaRootCertificate root = new("ab12", "CN=x", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

        var throughContract = JsonSerializer.Serialize(root, AdminJsonContext.Default.CaRootCertificate);

        Assert.Equal(throughContract, JsonSerializer.Serialize(root, Http()));
    }

    [Fact]
    public void AddToIsIdempotent()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web);
        AdminJsonContext.AddTo(options);
        var converters = options.Converters.Count;
        var resolvers = options.TypeInfoResolverChain.Count;

        AdminJsonContext.AddTo(options);

        Assert.Equal(converters, options.Converters.Count);
        Assert.Equal(resolvers, options.TypeInfoResolverChain.Count);
    }
}
