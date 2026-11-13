using System.Text.RegularExpressions;

namespace Paduraksa.Tests.Integration.Admin;

/// <summary>
/// The GETs the request policy allows although their paths look sensitive (EndpointRules.Reviewed) read through MCP, on a host that holds secrets: a DNS
/// credential, a notification channel's secrets, a certificate with a private key in the vault. What a script reads must hold none of them.
/// </summary>
public sealed class McpReviewedReadsTests
{
    private const string DnsToken = "cf-token-9d41c0a7e2b84f31a5";
    private const string HookSecret = "https://hooks.example.test/services/T0001/B0002/hook-secret-77aa";
    private const string SigningKey = "signing-key-5c3e9f0b12d84a";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WhatTheReviewedReadsReturnHoldsNoSecretMaterial()
    {
        await using var api = await AdminApiHarness.StartAsync(Ct);
        using (var dns = await api.SendAsync(HttpMethod.Post, "/api/v1/dns-providers", content: AdminApiHarness.JsonBody($$"""{"kind":"cloudflare","label":"cf","credential":"{{DnsToken}}"}""")))
        {
            Assert.Equal(System.Net.HttpStatusCode.Created, dns.StatusCode);
        }

        using (var channel = await api.SendAsync(HttpMethod.Post, "/api/v1/notification-channels", content: AdminApiHarness.JsonBody(
            $$"""{"kind":"webhook","label":"hook","events":["upstream.down"],"secret":"{{HookSecret}}","signingSecret":"{{SigningKey}}"}""")))
        {
            Assert.Equal(System.Net.HttpStatusCode.Created, channel.StatusCode);
        }

        using (var certificate = await api.SendAsync(HttpMethod.Post, "/api/v1/certificates/self-signed", content: AdminApiHarness.JsonBody("""{"names":["keys.test"]}""")))
        {
            Assert.True(certificate.IsSuccessStatusCode, await certificate.Content.ReadAsStringAsync(Ct));
        }

        await using var client = await McpTestClient.ConnectAsync(api, "owner", cancellationToken: Ct);
        var bodies = new List<string>();
        foreach (var path in new[]
        {
            "/api/v1/config", "/api/v1/dns-providers", "/api/v1/notification-channels", "/api/v1/acme-accounts", "/api/v1/acme-orders", "/api/v1/certificates", "/api/v1/ca",
            "/api/v1/auth/me", "/api/v1/auth-providers", "/api/v1/metrics/overview",
        })
        {
            var reply = await client.CallAsync("execute", $"async () => JSON.stringify(await paduraksa.request({{ method: 'GET', path: '{path}' }}))", Ct);
            Assert.False(reply.IsError, $"{path}: {reply.Text}");
            Assert.Contains("\\\"status\\\":200", reply.Text, StringComparison.Ordinal);
            bodies.Add(reply.Text);
        }

        var certificateId = Regex.Match(bodies[5], "([a-z]+_[0-9A-Za-z]{22})").Value;
        Assert.NotEmpty(certificateId);
        foreach (var path in new[] { $"/api/v1/certificates/{certificateId}", $"/api/v1/certificates/{certificateId}/order" })
        {
            var reply = await client.CallAsync("execute", $"async () => JSON.stringify(await paduraksa.request({{ method: 'GET', path: '{path}' }}))", Ct);
            bodies.Add(reply.Text);
        }

        foreach (var body in bodies)
        {
            foreach (var secret in new[] { DnsToken, "hook-secret-77aa", SigningKey, "PRIVATE KEY", "pdk_", "sec_" })
            {
                Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
            }

            // A field that names a secret holds no value (escaped, since each body is a JSON string inside the result).
            Assert.DoesNotMatch(new Regex(@"\\""(secret|signingSecret|password|credential|privateKey|token)\\"":\\""[^\\""]+", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)), body);
        }
    }
}

/// <summary>MCP is the operator's choice (<c>Paduraksa:Mcp:Enabled</c>, off by default): while it is off the endpoint does not exist.</summary>
public sealed class McpDisabledTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public async Task WhileItIsOffMcpIsNotMappedWhateverIsPresented(string? setting)
    {
        Dictionary<string, string?> settings = new() { ["Paduraksa:Mcp:Enabled"] = setting };
        await using var api = await AdminApiHarness.StartAsync(Ct, settings: settings);
        foreach (var token in new[] { null, api.TokenValueOf("owner") })
        {
            using HttpRequestMessage request = new(HttpMethod.Post, new Uri(api.BaseAddress, "/mcp"));
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
            if (token is not null)
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            }

            request.Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", System.Text.Encoding.UTF8, "application/json");
            using var response = await api.Client.SendAsync(request, Ct);
            // Not mapped: the dashboard's page routes answer a POST with 405; no MCP answer and no authentication challenge, whatever was presented.
            Assert.Contains(response.StatusCode, new[] { System.Net.HttpStatusCode.NotFound, System.Net.HttpStatusCode.MethodNotAllowed });
            Assert.NotEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        }

        using var get = await api.Client.GetAsync(new Uri(api.BaseAddress, "/mcp"), Ct);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, get.StatusCode);

        // The REST API is unaffected.
        using var me = await api.GetAsync("/api/v1/auth/me");
        Assert.True(me.IsSuccessStatusCode);
    }

    [Fact]
    public async Task ItIsOnWhenTheOperatorSaysSo()
    {
        await using var api = await AdminApiHarness.StartAsync(Ct, settings: new Dictionary<string, string?> { ["Paduraksa:Mcp:Enabled"] = "true" });
        await using var client = await McpTestClient.ConnectAsync(api, cancellationToken: Ct);
        Assert.Equal("1", (await client.CallAsync("search", "async () => 1", Ct)).Text);
    }
}
