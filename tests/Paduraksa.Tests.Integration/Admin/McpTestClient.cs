using System.Text.Json;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Paduraksa.Tests.Integration.Admin;

/// <summary>What a tool call gave back, as a person reading the MCP result would see it.</summary>
internal sealed record McpResult(bool IsError, string Text, string? Note)
{
    /// <summary>The result's text as JSON (a successful call returns the function's value as JSON).</summary>
    public JsonElement Json => JsonDocument.Parse(Text).RootElement.Clone();
}

/// <summary>A real MCP client over streamable HTTP, as Claude Code or Claude Desktop connect: the official SDK's client against the admin listener.</summary>
internal static class McpTestClient
{
    public static async Task<McpClient> ConnectAsync(AdminApiHarness api, string caller = "owner", string? bearer = null, CancellationToken cancellationToken = default)
    {
        HttpClientTransport transport = new(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(api.BaseAddress, "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + (bearer ?? api.TokenValueOf(caller)) },
            },
            api.Client,
            ownsHttpClient: false);
        return await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
    }

    public static async Task<McpResult> CallAsync(this McpClient client, string tool, string code, CancellationToken cancellationToken)
    {
        var result = await client.CallToolAsync(tool, new Dictionary<string, object?> { ["code"] = code }, cancellationToken: cancellationToken);
        var blocks = result.Content.OfType<TextContentBlock>().Select(b => b.Text).ToList();
        return new McpResult(result.IsError ?? false, blocks[0], blocks.Count > 1 ? blocks[1] : null);
    }

    /// <summary>The <c>paduraksa.request</c> of one call, returned whole: <c>{ status, body, ... }</c>.</summary>
    public static async Task<JsonElement> RequestAsync(this McpClient client, string options, CancellationToken cancellationToken)
    {
        var result = await client.CallAsync("execute", $"async () => await paduraksa.request({options})", cancellationToken);
        Assert.False(result.IsError, result.Text);
        return result.Json;
    }
}
