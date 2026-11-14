using System.Text.Json;

using ModelContextProtocol.Client;

using Paduraksa.Admin.Json;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Drafts;
using Paduraksa.Tests.Integration.Config;

namespace Paduraksa.Tests.Integration.Admin;

/// <summary>
/// Drafts through MCP (docs/design/admin-surfaces.md "MCP in code mode (M10)", docs/design/dashboard.md "Drafts (M10)"): reading the draft and reviewing it are allowed; adding entries, opening, applying and discarding are writes that need
/// <c>confirm</c>, and an apply without it is the review. An agent proposes a set of changes into its token owner's draft, and the person applies it.
/// </summary>
public sealed class McpDraftTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnAgentProposesChangesIntoTheOwnersDraftAndTheWritesNeedConfirm()
    {
        await using var api = await AdminApiHarness.StartAsync(Ct);
        await using var client = await McpTestClient.ConnectAsync(api, "admin", cancellationToken: Ct);
        var upstream = ConfigTestSupport.Upstream("agent", "http://10.0.0.30:80");
        var host = ConfigTestSupport.ProxyHost("agent", "agent.test", "agent");
        var entries = JsonSerializer.Serialize(new AddDraftEntriesRequest([new UpsertUpstreamCommand(upstream), new UpsertHostCommand(host)]), AdminJsonContext.Default.AddDraftEntriesRequest);

        var read = await client.RequestAsync("{ method: 'GET', path: '/api/v1/drafts' }", Ct);
        Assert.Equal(200, read.GetProperty("status").GetInt32());
        Assert.Equal(0, read.GetProperty("body").GetProperty("items").GetArrayLength());

        var withoutConfirm = await client.RequestAsync("{ method: 'POST', path: '/api/v1/drafts' }", Ct);
        Assert.Equal(428, withoutConfirm.GetProperty("status").GetInt32());
        Assert.True(withoutConfirm.GetProperty("body").GetProperty("requiresConfirm").GetBoolean());
        Assert.Equal(0, (await DraftsAsync(client)).GetArrayLength());

        var opened = await client.RequestAsync("{ method: 'POST', path: '/api/v1/drafts', confirm: true }", Ct);
        Assert.Equal(201, opened.GetProperty("status").GetInt32());
        var id = opened.GetProperty("body").GetProperty("id").GetString()!;

        var unconfirmedAdd = await client.RequestAsync($"{{ method: 'POST', path: '/api/v1/drafts/{id}/entries', body: {entries} }}", Ct);
        Assert.Equal(428, unconfirmedAdd.GetProperty("status").GetInt32());
        Assert.Equal(0, (await DraftsAsync(client))[0].GetProperty("entries").GetArrayLength());

        var added = await client.RequestAsync($"{{ method: 'POST', path: '/api/v1/drafts/{id}/entries', body: {entries}, confirm: true }}", Ct);
        Assert.Equal(200, added.GetProperty("status").GetInt32());
        Assert.Equal(2, added.GetProperty("body").GetProperty("entries").GetArrayLength());

        // A review changes nothing, so it needs no confirm; an apply without it is that same review.
        var review = await client.RequestAsync($"{{ method: 'POST', path: '/api/v1/drafts/{id}/review' }}", Ct);
        Assert.Equal(200, review.GetProperty("status").GetInt32());
        Assert.False(review.TryGetProperty("dryRun", out _));
        Assert.Equal("dryRun", review.GetProperty("body").GetProperty("result").GetProperty("status").GetString());
        var token = review.GetProperty("body").GetProperty("reviewToken").GetString()!;
        Assert.Equal($"\"{token}\"", review.GetProperty("etag").GetString());
        var preview = await client.RequestAsync($"{{ method: 'POST', path: '/api/v1/drafts/{id}/apply' }}", Ct);
        Assert.True(preview.GetProperty("dryRun").GetBoolean());

        // A confirmed apply must name the review it confirmed: without the token it does nothing and answers with the review as it is.
        var untokened = await client.RequestAsync($"{{ method: 'POST', path: '/api/v1/drafts/{id}/apply', confirm: true }}", Ct);
        Assert.Equal(428, untokened.GetProperty("status").GetInt32());
        Assert.Equal(token, untokened.GetProperty("body").GetProperty("review").GetProperty("reviewToken").GetString());
        var invented = await client.RequestAsync($"{{ method: 'POST', path: '/api/v1/drafts/{id}/apply', confirm: true, ifMatch: '\"1.AAAAAAAAAAAAAAAAAAAAAA\"' }}", Ct);
        Assert.Equal(412, invented.GetProperty("status").GetInt32());
        Assert.Equal("draft.changedSinceReview", invented.GetProperty("body").GetProperty("code").GetString());
        using (var before = await api.GetAsync("/api/v1/hosts"))
        {
            Assert.Equal("""{"items":[],"nextCursor":null}""", (await before.Content.ReadAsStringAsync(Ct)).Trim());
        }

        // The person who owns the token applies what the agent proposed.
        using var applied = await api.SendAsync(HttpMethod.Post, $"/api/v1/drafts/{id}/apply", "admin", ifMatch: $"\"{token}\"");
        var outcome = await AdminApiHarness.ReadAsync(applied, AdminJsonContext.Default.DraftOutcome, Ct);
        Assert.Equal(ConfigCommandStatus.Committed, outcome.Result.Status);
        using var after = await api.GetAsync($"/api/v1/hosts/{host.Id}");
        Assert.True(after.IsSuccessStatusCode);
    }

    [Fact]
    public async Task DiscardingAndApplyingAreWritesThatNeedConfirm()
    {
        await using var api = await AdminApiHarness.StartAsync(Ct);
        await using var client = await McpTestClient.ConnectAsync(api, "admin", cancellationToken: Ct);
        var opened = await client.RequestAsync("{ method: 'POST', path: '/api/v1/drafts', confirm: true }", Ct);
        var id = opened.GetProperty("body").GetProperty("id").GetString()!;

        var unconfirmed = await client.RequestAsync($"{{ method: 'DELETE', path: '/api/v1/drafts/{id}' }}", Ct);
        Assert.Equal(428, unconfirmed.GetProperty("status").GetInt32());
        Assert.Equal(1, (await DraftsAsync(client)).GetArrayLength());

        var discarded = await client.RequestAsync($"{{ method: 'DELETE', path: '/api/v1/drafts/{id}', confirm: true }}", Ct);
        Assert.Equal(204, discarded.GetProperty("status").GetInt32());
        Assert.Equal(0, (await DraftsAsync(client)).GetArrayLength());
    }

    [Fact]
    public async Task AScriptOpensADraftAddsAnEntryAndSimulatesAgainstItWithoutAConfirm()
    {
        await using var api = await AdminApiHarness.StartAsync(Ct);
        await using var client = await McpTestClient.ConnectAsync(api, "admin", cancellationToken: Ct);
        var host = ConfigTestSupport.RespondHost("agent-sim", "agent-sim.test", "from the draft");
        var entries = JsonSerializer.Serialize(new AddDraftEntriesRequest([new UpsertHostCommand(host)]), AdminJsonContext.Default.AddDraftEntriesRequest);
        var probe = "{ request: { url: 'http://agent-sim.test/', clientIp: '203.0.113.9' }, target: { kind: 'draft', draft: id } }";

        var result = await client.CallAsync("execute", $$"""
            async () => {
              const opened = await paduraksa.request({ method: 'POST', path: '/api/v1/drafts', confirm: true });
              const id = opened.body.id;
              await paduraksa.request({ method: 'POST', path: `/api/v1/drafts/${id}/entries`, body: {{entries}}, confirm: true });
              const drafted = await paduraksa.request({ method: 'POST', path: '/api/v1/simulate', body: {{probe}} });
              const live = await paduraksa.request({ method: 'POST', path: '/api/v1/simulate', body: { request: { url: 'http://agent-sim.test/', clientIp: '203.0.113.9' } } });
              return { drafted, live };
            }
            """, Ct);

        Assert.False(result.IsError, result.Text);
        var drafted = result.Json.GetProperty("drafted");
        Assert.Equal(200, drafted.GetProperty("status").GetInt32());
        Assert.False(drafted.TryGetProperty("dryRun", out _));
        Assert.Equal("draft", drafted.GetProperty("body").GetProperty("source").GetProperty("kind").GetString());
        Assert.Equal("fixedAnswer", drafted.GetProperty("body").GetProperty("outcome").GetProperty("kind").GetString());
        Assert.NotEqual("fixedAnswer", result.Json.GetProperty("live").GetProperty("body").GetProperty("outcome").GetProperty("kind").GetString());
        using var live = await api.GetAsync($"/api/v1/hosts/{host.Id}");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, live.StatusCode);
    }

    private static async Task<JsonElement> DraftsAsync(McpClient client) =>
        (await client.RequestAsync("{ method: 'GET', path: '/api/v1/drafts' }", Ct)).GetProperty("body").GetProperty("items").Clone();
}
