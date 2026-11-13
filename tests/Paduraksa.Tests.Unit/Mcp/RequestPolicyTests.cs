using System.Text;
using System.Text.Json;

using Paduraksa.Mcp;

namespace Paduraksa.Tests.Unit.Mcp;

public sealed class RequestPolicyTests
{
    private static readonly RequestPolicy Policy = RequestPolicy.FromSpec(JsonDocument.Parse(Encoding.UTF8.GetBytes(McpTestSpec.OpenApi)).RootElement.Clone());

    [Theory]
    [InlineData("GET", "/api/v1/hosts", RequestKind.Read, false)]
    [InlineData("GET", "/api/v1/hosts/host_abc", RequestKind.Read, false)]
    [InlineData("GET", "/api/v1/HOSTS/host_abc", RequestKind.Read, false)]
    [InlineData("POST", "/api/v1/hosts", RequestKind.Write, true)]
    [InlineData("PUT", "/api/v1/hosts/host_abc", RequestKind.Write, true)]
    [InlineData("DELETE", "/api/v1/hosts/host_abc", RequestKind.Write, false)]
    [InlineData("POST", "/api/v1/certificates/cert_abc/renew", RequestKind.Write, false)]
    [InlineData("POST", "/api/v1/simulate", RequestKind.Read, false)]
    [InlineData("GET", "/api/v1/logs/recent", RequestKind.Read, false)]
    [InlineData("GET", "/api/v1/dns-providers", RequestKind.Read, false)]
    public void ARequestIsAReadAWriteOrAWriteWithADryRun(string method, string path, RequestKind kind, bool dryRun)
    {
        var decision = Policy.Decide(method, path);
        Assert.Equal(kind, decision.Kind);
        Assert.Equal(dryRun, decision.SupportsDryRun);
    }

    [Theory]
    [InlineData("GET", "/api/v1/tokens")]
    [InlineData("POST", "/api/v1/tokens")]
    [InlineData("GET", "/api/v1/TOKENS")]
    [InlineData("DELETE", "/api/v1/tokens/tok_abc")]
    [InlineData("GET", "/api/v1/users/user_abc")]
    [InlineData("GET", "/api/v1/sessions")]
    [InlineData("POST", "/api/v1/me/password")]
    [InlineData("POST", "/api/v1/auth/login")]
    [InlineData("POST", "/api/v1/setup")]
    [InlineData("POST", "/api/v1/dns-providers")]
    [InlineData("POST", "/api/v1/dns-providers/dnsp_abc/credential")]
    [InlineData("GET", "/api/v1/logs/tail")]
    [InlineData("GET", "/api/v1/backup")]
    [InlineData("POST", "/api/v1/backup/restore")]
    [InlineData("GET", "/metrics")]
    [InlineData("GET", "/METRICS")]
    [InlineData("GET", "/api/v1/metrics")]
    public void WhatIsOnTheRefusedListIsRefusedWhateverTheCase(string method, string path)
    {
        var decision = Policy.Decide(method, path);
        Assert.Equal(RequestKind.Refused, decision.Kind);
        Assert.StartsWith("Not available through this tool: ", decision.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("GET", "/api/v1/hosts/../tokens")]
    [InlineData("GET", "/api/v1/hosts/%2e%2e/tokens")]
    [InlineData("GET", "/api/v1/hosts/host_abc/")]
    [InlineData("GET", "/api/v1//hosts")]
    [InlineData("GET", "/api/v1/hosts?x=1")]
    [InlineData("GET", "/api/v1/hosts#x")]
    [InlineData("GET", "/api/v1/hosts;x")]
    [InlineData("GET", "/api/v1/hosts\\..\\tokens")]
    [InlineData("GET", "/api/v1/hosts/\u0000")]
    [InlineData("GET", "/api/v1/hosts/host abc")]
    [InlineData("GET", "api/v1/hosts")]
    [InlineData("GET", "http://elsewhere/api/v1/hosts")]
    [InlineData("GET", "/api/v1/")]
    [InlineData("GET", "")]
    [InlineData("GET", "/api/v1/hosts/İ")]
    public void APathThatIsNotPlainIsRefusedBeforeItIsMatchedAgainstAnything(string method, string path)
    {
        var decision = Policy.Decide(method, path);
        Assert.Equal(RequestKind.Refused, decision.Kind);
        Assert.Contains("plain path", decision.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    [InlineData("TRACE")]
    [InlineData("get")]
    [InlineData("CONNECT")]
    public void OnlyTheFiveMethodsAreSent(string method) => Assert.Equal(RequestKind.Refused, Policy.Decide(method, "/api/v1/hosts").Kind);

    [Theory]
    [InlineData("GET", "/api/v1/nothing")]
    [InlineData("PATCH", "/api/v1/hosts/host_abc")]
    [InlineData("GET", "/api/v1/hosts/host_abc/extra")]
    public void WhatIsNotInTheApiDescriptionIsUnknownAndPointsToSearch(string method, string path)
    {
        var decision = Policy.Decide(method, path);
        Assert.Equal(RequestKind.Unknown, decision.Kind);
        Assert.Contains("search", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ADryRunParameterGivenByReferenceIsFound()
    {
        var policy = RequestPolicy.FromSpec(JsonDocument.Parse("""
            { "paths": { "/api/v1/things": { "put": { "parameters": [ { "$ref": "#/components/parameters/Dry" } ] }, "post": {} } },
              "components": { "parameters": { "Dry": { "name": "dryRun", "in": "query" } } } }
            """).RootElement.Clone());
        Assert.True(policy.Decide("PUT", "/api/v1/things").SupportsDryRun);
        Assert.False(policy.Decide("POST", "/api/v1/things").SupportsDryRun);
    }

    [Fact]
    public void TheListsDoNotOverlapAndEveryRuleSaysWhy()
    {
        foreach (var reviewed in EndpointRules.Reviewed)
        {
            Assert.DoesNotContain(EndpointRules.Refused, refused => refused.Covers(reviewed.Method, reviewed.Path));
        }

        foreach (var rule in EndpointRules.Refused.Concat(EndpointRules.Reviewed).Concat(EndpointRules.ReadOnlyPosts))
        {
            Assert.False(string.IsNullOrWhiteSpace(rule.Reason));
            Assert.StartsWith("/", rule.Path, StringComparison.Ordinal);
        }
    }
}
