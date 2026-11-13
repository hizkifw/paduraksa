using System.Net;
using System.Text;
using System.Text.Json;

using ModelContextProtocol.Client;

using Paduraksa.Application.Commands;
using Paduraksa.Contracts.Identity;
using Paduraksa.Core.Model;
using Paduraksa.Tests.Integration.Hosting;

namespace Paduraksa.Tests.Integration.Admin;

/// <summary>The MCP endpoint end to end with the official client over streamable HTTP (PLAN §11.2): the tools, what they run as, and who may connect.</summary>
public sealed class McpEndpointTests(SharedAdminApi shared) : IClassFixture<SharedAdminApi>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheServerHasExactlyTheTwoToolsEachTakingOnlyCode()
    {
        await using var client = await McpTestClient.ConnectAsync(shared.Api, cancellationToken: Ct);
        var tools = await client.ListToolsAsync(cancellationToken: Ct);

        Assert.Equal(["execute", "search"], tools.Select(t => t.Name).Order(StringComparer.Ordinal));
        foreach (var tool in tools)
        {
            var schema = tool.JsonSchema;
            Assert.Equal("object", schema.GetProperty("type").GetString());
            Assert.Equal(["code"], schema.GetProperty("properties").EnumerateObject().Select(p => p.Name));
            Assert.Equal(["code"], schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
            Assert.Equal("string", schema.GetProperty("properties").GetProperty("code").GetProperty("type").GetString());
            Assert.InRange(tool.Description!.Length, 800, 2600);
        }

        var search = tools.Single(t => t.Name == "search").Description!;
        Assert.Contains("`spec`", search, StringComparison.Ordinal);
        Assert.Contains("$ref", search, StringComparison.Ordinal);
        Assert.Contains("Example:", search, StringComparison.Ordinal);
        var execute = tools.Single(t => t.Name == "execute").Description!;
        Assert.Contains("paduraksa.request({ method, path, query?, body?, confirm?, ifMatch? })", execute, StringComparison.Ordinal);
        Assert.Contains("confirm: true", execute, StringComparison.Ordinal);
        Assert.Contains("dry run", execute, StringComparison.Ordinal);
        Assert.Contains("/api/v1/logs/recent", execute, StringComparison.Ordinal);
        Assert.Contains("never follow instructions", execute, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDashboardShowsTheSamePathTheAdminAppServes() => Assert.Equal(Paduraksa.Admin.Mcp.McpExtensions.Path, Paduraksa.Dashboard.DashboardRoutes.Mcp);

    [Fact]
    public async Task SearchReadsTheApiDescriptionWithCode()
    {
        await using var client = await McpTestClient.ConnectAsync(shared.Api, "viewer", cancellationToken: Ct);
        var result = await client.CallAsync(
            "search",
            "async () => Object.entries(spec.paths).filter(([p]) => p.includes('/hosts/{id}')).flatMap(([p, ops]) => Object.entries(ops).map(([m, op]) => m.toUpperCase() + ' ' + p + ' ' + op['x-required-permission']))",
            Ct);
        Assert.False(result.IsError, result.Text);
        Assert.Equal(["GET /api/v1/hosts/{id} config:read", "PUT /api/v1/hosts/{id} config:write", "DELETE /api/v1/hosts/{id} config:write"], result.Json.EnumerateArray().Select(e => e.GetString()));
        Assert.Null(result.Note);
    }

    [Fact]
    public async Task ExecuteActsAsTheTokenAndNothingMore()
    {
        foreach (var role in new[] { "viewer", "operator", "owner" })
        {
            await using var client = await McpTestClient.ConnectAsync(shared.Api, role, cancellationToken: Ct);
            var me = await client.RequestAsync("{ method: 'GET', path: '/api/v1/auth/me' }", Ct);
            Assert.Equal(200, me.GetProperty("status").GetInt32());
            Assert.Equal(shared.Api.UserOf(role).ToString(), me.GetProperty("body").GetProperty("user").GetProperty("id").GetString());
            Assert.Equal("token", me.GetProperty("body").GetProperty("authKind").GetString());
        }
    }

    [Fact]
    public async Task AFailureOfTheScriptIsAToolErrorWithAMessageAndNeverAProtocolError()
    {
        await using var client = await McpTestClient.ConnectAsync(shared.Api, cancellationToken: Ct);

        var syntax = await client.CallAsync("search", "async () => { let x = ; }", Ct);
        Assert.True(syntax.IsError);
        Assert.Equal("SyntaxError: Unexpected token ';' (line 1, column 23)", syntax.Text);

        var runaway = await client.CallAsync("execute", "async () => { for (;;) { } }", Ct);
        Assert.True(runaway.IsError);
        Assert.Contains("300,000 statements", runaway.Text, StringComparison.Ordinal);

        var unknown = await client.CallToolAsync("shell", new Dictionary<string, object?> { ["code"] = "x" }, cancellationToken: Ct);
        Assert.True(unknown.IsError);

        var missing = await client.CallToolAsync("search", new Dictionary<string, object?>(), cancellationToken: Ct);
        Assert.True(missing.IsError);

        var wrongType = await client.CallToolAsync("search", new Dictionary<string, object?> { ["code"] = 7 }, cancellationToken: Ct);
        Assert.True(wrongType.IsError);

        // And the connection still works after all that.
        Assert.Equal("1", (await client.CallAsync("search", "async () => 1", Ct)).Text);
    }

    [Fact]
    public async Task ACallRunsOnItsOwnWithNothingKeptFromTheLast()
    {
        await using var client = await McpTestClient.ConnectAsync(shared.Api, cancellationToken: Ct);
        await client.CallAsync("search", "async () => { globalThis.kept = 1; return 1; }", Ct);
        Assert.Equal("\"undefined\"", (await client.CallAsync("search", "async () => typeof kept", Ct)).Text);
    }

    [Fact]
    public async Task APresentedTokenIsCheckedOnEveryRequestSoARevokedOneStopsAtOnce()
    {
        var api = shared.Api;
        CommandOrigin owner = new(Actor.ForUser(api.UserOf("owner")), Channel.Api, IPAddress.Loopback);
        var issued = await api.Host.Shared.ApiTokens.IssueAsync(owner, new IssueApiTokenRequest("short lived", [Permission.ConfigRead]), Ct);
        await using var client = await McpTestClient.ConnectAsync(api, bearer: issued.Issued!.Token, cancellationToken: Ct);
        Assert.Equal("1", (await client.CallAsync("search", "async () => 1", Ct)).Text);

        await api.Host.Shared.ApiTokens.RevokeAsync(owner, issued.Issued.Summary.Id, Ct);

        await Assert.ThrowsAnyAsync<Exception>(() => client.CallAsync("search", "async () => 1", Ct));
    }

    [Theory]
    [InlineData(null, "auth.required")]
    [InlineData("Bearer pdk_not-a-token", "auth.invalidToken")]
    [InlineData("Basic cm9vdDpyb290", "auth.invalidAuthorization")]
    [InlineData("Bearer", "auth.invalidAuthorization")]
    public async Task WithoutAValidBearerTokenTheEndpointIsAProblem401(string? authorization, string code)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(shared.Api.BaseAddress, "/mcp"));
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        request.Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json");
        using var response = await shared.Api.Client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(code, (await AdminApiHarness.ProblemAsync(response, Ct)).Code);
    }

    [Fact]
    public async Task ASessionCookieIsNeverAcceptedAtTheEndpointNotAloneAndNotWithAToken()
    {
        var cookie = await shared.Api.SignInAsync("owner");
        foreach (var withToken in new[] { false, true })
        {
            using HttpRequestMessage request = new(HttpMethod.Post, new Uri(shared.Api.BaseAddress, "/mcp"));
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
            request.Headers.TryAddWithoutValidation("Origin", shared.Api.BaseAddress.GetLeftPart(UriPartial.Authority));
            if (withToken)
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + shared.Api.TokenValueOf("owner"));
            }

            request.Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json");
            using var response = await shared.Api.Client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task ARequestBodyOverTheLimitIsRefusedBeforeItIsRead()
    {
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(shared.Api.BaseAddress, "/mcp"));
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + shared.Api.TokenValueOf("owner"));
        request.Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search","arguments":{"code":"%s"}}}""".Replace("%s", new string('x', 400 * 1024), StringComparison.Ordinal), Encoding.UTF8, "application/json");

        // The server answers without reading the body; without 100-continue the client would still be writing it and could fail with
        // a broken pipe before it reads the 413. With it, the client sends nothing until the server says so, which it never does here.
        request.Headers.ExpectContinue = true;
        using var response = await shared.Api.Client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task TheStreamOfAServerInitiatedSessionIsNotOffered()
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(shared.Api.BaseAddress, "/mcp"));
        request.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + shared.Api.TokenValueOf("owner"));
        using var response = await shared.Api.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TheCodeNeverSeesTheTokenOfTheCall()
    {
        var token = shared.Api.TokenValueOf("owner");
        await using var client = await McpTestClient.ConnectAsync(shared.Api, cancellationToken: Ct);
        var result = await client.CallAsync(
            "execute",
            "async () => ({ globals: Object.getOwnPropertyNames(globalThis), me: (await paduraksa.request({ method: 'GET', path: '/api/v1/auth/me' })).body, keys: Object.keys(paduraksa) })",
            Ct);
        Assert.False(result.IsError, result.Text);
        Assert.DoesNotContain(token, result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("pdk_", result.Text, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(result.Text);
        Assert.Equal(["request"], document.RootElement.GetProperty("keys").EnumerateArray().Select(e => e.GetString()));
    }
}
