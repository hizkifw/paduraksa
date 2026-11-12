using System.Text.Json;

using Paduraksa.Admin.Json;
using Paduraksa.Contracts.Config;
using Paduraksa.Tests.Integration.Config;

namespace Paduraksa.Tests.Integration.Admin;

/// <summary>
/// A change through MCP is two steps and is the caller's (PLAN §11.2): without <c>confirm</c> it is a dry run (or a 428 where the operation has none),
/// with it the change is made, as the token and on the <c>mcp</c> channel, with exactly the permissions the token has over HTTP. Each test has its own
/// host because it counts what a refused or previewed change leaves behind.
/// </summary>
public sealed class McpWriteTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Body(HostConfig host) => JsonSerializer.Serialize(host, AdminJsonContext.Default.HostConfig);

    private static async Task<int> RevisionCountAsync(AdminApiHarness api)
    {
        using var response = await api.GetAsync("/api/v1/revisions");
        using var page = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return page.RootElement.GetProperty("items").GetArrayLength();
    }

    private static async Task<int> HostCountAsync(AdminApiHarness api)
    {
        using var response = await api.GetAsync("/api/v1/hosts");
        using var hosts = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return hosts.RootElement.GetArrayLength();
    }

    [Fact]
    public async Task AWriteWithoutConfirmIsADryRunAndWithConfirmItIsMade()
    {
        await using var api = await AdminApiHarness.StartAsync(Ct);
        await using var client = await McpTestClient.ConnectAsync(api, "admin", cancellationToken: Ct);
        var host = ConfigTestSupport.RespondHost("mcp", "mcp.test", "ok");

        var dry = await client.RequestAsync($"{{ method: 'POST', path: '/api/v1/hosts', body: {Body(host)} }}", Ct);
        Assert.Equal(200, dry.GetProperty("status").GetInt32());
        Assert.True(dry.GetProperty("dryRun").GetBoolean());
        Assert.Equal("dryRun", dry.GetProperty("body").GetProperty("status").GetString());
        Assert.Equal(0, await HostCountAsync(api));
        Assert.Equal(0, await RevisionCountAsync(api));

        var made = await client.RequestAsync($"{{ method: 'POST', path: '/api/v1/hosts', body: {Body(host)}, confirm: true }}", Ct);
        Assert.Equal(201, made.GetProperty("status").GetInt32());
        Assert.False(made.TryGetProperty("dryRun", out _));
        Assert.Equal(1, await HostCountAsync(api));
        Assert.Equal(1, await RevisionCountAsync(api));
    }

    [Fact]
    public async Task ChangingAnExistingObjectTakesTheEtagOfItsLastReadAndAStaleOneIsRefusedByTheApi()
    {
        await using var api = await AdminApiHarness.StartAsync(Ct);
        await using var client = await McpTestClient.ConnectAsync(api, "admin", cancellationToken: Ct);
        var host = ConfigTestSupport.RespondHost("etag", "etag.test", "ok");
        await client.RequestAsync($"{{ method: 'POST', path: '/api/v1/hosts', body: {Body(host)}, confirm: true }}", Ct);

        var read = await client.RequestAsync($"{{ method: 'GET', path: '/api/v1/hosts/{host.Id}' }}", Ct);
        var etag = read.GetProperty("etag").GetString()!;
        var changed = read.GetProperty("body").Clone();
        var edited = JsonSerializer.Deserialize(changed.GetRawText(), AdminJsonContext.Default.HostConfig)! with { Respond = new RespondConfig(200, "changed") };

        var withoutTag = await client.RequestAsync($"{{ method: 'PUT', path: '/api/v1/hosts/{host.Id}', body: {Body(edited)}, confirm: true }}", Ct);
        Assert.Equal(428, withoutTag.GetProperty("status").GetInt32());

        var stale = await client.RequestAsync($"{{ method: 'PUT', path: '/api/v1/hosts/{host.Id}', body: {Body(edited)}, confirm: true, ifMatch: '\"99\"' }}", Ct);
        Assert.Equal(412, stale.GetProperty("status").GetInt32());

        var preview = await client.RequestAsync($"{{ method: 'PUT', path: '/api/v1/hosts/{host.Id}', body: {Body(edited)}, ifMatch: {JsonSerializer.Serialize(etag)} }}", Ct);
        Assert.True(preview.GetProperty("dryRun").GetBoolean());
        Assert.Equal("dryRun", preview.GetProperty("body").GetProperty("status").GetString());

        var applied = await client.RequestAsync($"{{ method: 'PUT', path: '/api/v1/hosts/{host.Id}', body: {Body(edited)}, confirm: true, ifMatch: {JsonSerializer.Serialize(etag)} }}", Ct);
        Assert.Equal(200, applied.GetProperty("status").GetInt32());

        using var after = await api.GetAsync($"/api/v1/hosts/{host.Id}");
        Assert.Contains("changed", await after.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOperationWithNoDryRunIsNotSentWithoutConfirmAndAnswers428()
    {
        await using var api = await AdminApiHarness.StartAsync(Ct);
        await using var client = await McpTestClient.ConnectAsync(api, "owner", cancellationToken: Ct);

        var reply = await client.RequestAsync("{ method: 'POST', path: '/api/v1/certificates/self-signed', body: { names: ['x.test'] } }", Ct);
        Assert.Equal(428, reply.GetProperty("status").GetInt32());
        Assert.True(reply.GetProperty("body").GetProperty("requiresConfirm").GetBoolean());
        Assert.Equal("POST", reply.GetProperty("body").GetProperty("method").GetString());
        Assert.Equal("/api/v1/certificates/self-signed", reply.GetProperty("body").GetProperty("path").GetString());

        using var certificates = await api.GetAsync("/api/v1/certificates");
        using var inventory = JsonDocument.Parse(await certificates.Content.ReadAsStringAsync(Ct));
        Assert.DoesNotContain(inventory.RootElement.EnumerateArray(), c => c.GetRawText().Contains("x.test", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ATokenWithoutWriteScopeStaysReadOnlyWhateverTheScriptSaysAndNothingIsLeftBehind()
    {
        await using var api = await AdminApiHarness.StartAsync(Ct);
        var host = ConfigTestSupport.RespondHost("viewer", "viewer.test", "ok");
        foreach (var role in new[] { "viewer", "auditor" })
        {
            await using var client = await McpTestClient.ConnectAsync(api, role, cancellationToken: Ct);
            var denied = await client.RequestAsync($"{{ method: 'POST', path: '/api/v1/hosts', body: {Body(host)}, confirm: true }}", Ct);
            Assert.Equal(403, denied.GetProperty("status").GetInt32());
            Assert.Equal("auth.permissionRequired", denied.GetProperty("body").GetProperty("code").GetString());
        }

        Assert.Equal(0, await HostCountAsync(api));
        Assert.Equal(0, await RevisionCountAsync(api));
    }

    [Fact]
    public async Task AConfirmedWriteIsRecordedAsTheTokenOnTheMcpChannelInTheAuditLogAndTheHistory()
    {
        await using var api = await AdminApiHarness.StartAsync(Ct);
        await using var client = await McpTestClient.ConnectAsync(api, "admin", cancellationToken: Ct);
        var host = ConfigTestSupport.RespondHost("audited", "audited.test", "ok");
        await client.RequestAsync($"{{ method: 'POST', path: '/api/v1/hosts', body: {Body(host)}, confirm: true }}", Ct);

        using var revisions = await api.GetAsync("/api/v1/revisions");
        using var history = JsonDocument.Parse(await revisions.Content.ReadAsStringAsync(Ct));
        var revision = Assert.Single(history.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("mcp", revision.GetProperty("channel").GetString());
        Assert.Equal("token", revision.GetProperty("actor").GetProperty("kind").GetString());
        Assert.Equal(api.TokenOf("admin").ToString(), revision.GetProperty("actor").GetProperty("token").GetString());

        using var audit = await api.GetAsync($"/api/v1/audit?actor={api.TokenOf("admin")}&action=config.*");
        using var log = JsonDocument.Parse(await audit.Content.ReadAsStringAsync(Ct));
        var events = log.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.NotEmpty(events);
        Assert.All(events, e => Assert.Equal("mcp", e.GetProperty("channel").GetString()));
        Assert.Contains(events, e => e.GetProperty("outcome").GetString() == "success" && e.GetProperty("revisionId").GetString() == revision.GetProperty("id").GetString());
    }
}
