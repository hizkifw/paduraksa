using System.Net;
using System.Text.Json.Nodes;

using Paduraksa.Tests.Integration.Hosting;

namespace Paduraksa.Tests.Integration.Admin;

public sealed class VersionApiTests(SharedAdminApi shared) : IClassFixture<SharedAdminApi>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheVersionNamesTheBuildAndTheApiAndNeedsASignedInCaller()
    {
        var api = shared.Api;

        using var response = await api.GetAsync("/api/v1/version", caller: "viewer");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!.AsObject();
        Assert.Matches(@"^\d+\.\d+\.\d+", body["version"]!.GetValue<string>());
        Assert.DoesNotContain('+', body["version"]!.GetValue<string>());
        Assert.Equal("v1", body["api"]!.GetValue<string>());
        Assert.True(body.ContainsKey("commit"));
        Assert.Equal(["api", "commit", "version"], body.Select(p => p.Key).Order(StringComparer.Ordinal));

        using var anonymous = await api.GetAsync("/api/v1/version", caller: null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }
}
