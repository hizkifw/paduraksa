using Microsoft.Extensions.DependencyInjection;

using Paduraksa.Admin.Mcp;
using Paduraksa.Server.Hosting;

namespace Paduraksa.Tests.Integration.Admin;

/// <summary>
/// A sandbox child never outlives the server: stopping the host kills it (docs/design/admin-surfaces.md "MCP in code mode (M10)"). Test classes run in parallel in one
/// process, and other MCP tests have pools of their own, so each test asks its own server which children it started rather than
/// scanning every sandbox child of the process.
/// </summary>
public sealed class McpShutdownTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static McpToolkit? Toolkit(AdminApiHarness api) => api.Host.GetServices(HostRole.Admin).GetService<McpToolkit>();

    [Fact]
    public async Task StoppingTheServerKillsTheChildrenItStarted()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "reads /proc");
        var api = await AdminApiHarness.StartAsync(Ct);
        IReadOnlyList<int> children;
        try
        {
            await using (var client = await McpTestClient.ConnectAsync(api, cancellationToken: Ct))
            {
                Assert.Equal("1", (await client.CallAsync("search", "async () => 1", Ct)).Text);
            }

            children = Toolkit(api)!.ChildProcessIds;
            Assert.NotEmpty(children);
            Assert.All(children, pid => Assert.True(Directory.Exists($"/proc/{pid}"), $"child {pid} is not running"));
        }
        finally
        {
            await api.DisposeAsync();
        }

        foreach (var pid in children)
        {
            Assert.False(Directory.Exists($"/proc/{pid}"), $"child {pid} outlived the server");
        }
    }

    [Fact]
    public async Task NoChildExistsUntilTheFirstCallAndAnOffServerNeverStartsOne()
    {
        await using var api = await AdminApiHarness.StartAsync(Ct);
        Assert.Empty(Toolkit(api)!.ChildProcessIds);

        await using var off = await AdminApiHarness.StartAsync(Ct, settings: new Dictionary<string, string?> { ["Paduraksa:Mcp:Enabled"] = "false" });
        Assert.Empty(Toolkit(off)!.ChildProcessIds);
    }

    [Fact]
    public async Task SwitchingMcpOnAndOffLiveOpensTheEndpointAndThenStopsTheChildren()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "reads /proc");

        // Nothing in the environment: the server setting decides, and its default is off.
        await using var api = await AdminApiHarness.StartAsync(Ct, settings: new Dictionary<string, string?> { ["Paduraksa:Mcp:Enabled"] = null });
        using (var before = await api.Client.GetAsync(new Uri(api.BaseAddress, "/mcp"), Ct))
        {
            Assert.Equal(System.Net.HttpStatusCode.NotFound, before.StatusCode);
        }

        await SetAsync(api, enabled: true);
        await using (var client = await McpTestClient.ConnectAsync(api, cancellationToken: Ct))
        {
            Assert.Equal("1", (await client.CallAsync("search", "async () => 1", Ct)).Text);
        }

        var children = Toolkit(api)!.ChildProcessIds;
        Assert.NotEmpty(children);

        await SetAsync(api, enabled: false);
        using (var after = await api.Client.GetAsync(new Uri(api.BaseAddress, "/mcp"), Ct))
        {
            Assert.Equal(System.Net.HttpStatusCode.NotFound, after.StatusCode);
        }

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (children.Any(pid => Directory.Exists($"/proc/{pid}")) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, Ct);
        }

        Assert.All(children, pid => Assert.False(Directory.Exists($"/proc/{pid}"), $"child {pid} outlived switching MCP off"));
        Assert.Empty(Toolkit(api)!.ChildProcessIds);
    }

    [Fact]
    public async Task AQuickOffAndOnNeverKillsThePoolBuiltAfterIt()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "reads /proc");
        await using var api = await AdminApiHarness.StartAsync(Ct, settings: new Dictionary<string, string?> { ["Paduraksa:Mcp:Enabled"] = null });
        await SetAsync(api, enabled: true);
        await using (var client = await McpTestClient.ConnectAsync(api, cancellationToken: Ct))
        {
            Assert.Equal("1", (await client.CallAsync("search", "async () => 1", Ct)).Text);
        }

        var first = Toolkit(api)!.ChildProcessIds;
        await SetAsync(api, enabled: false);
        await SetAsync(api, enabled: true);
        await using (var client = await McpTestClient.ConnectAsync(api, cancellationToken: Ct))
        {
            Assert.Equal("2", (await client.CallAsync("search", "async () => 2", Ct)).Text);
        }

        var second = Toolkit(api)!.ChildProcessIds;
        Assert.NotEmpty(second);
        Assert.Empty(second.Intersect(first));

        // The off's kill runs in the background; whenever it lands, it is the first pool's, never this one's.
        await Task.Delay(1000, Ct);
        Assert.All(second, pid => Assert.True(Directory.Exists($"/proc/{pid}"), $"child {pid} of the new pool was killed"));
        Assert.Equal(second, Toolkit(api)!.ChildProcessIds);
    }

    private static async Task SetAsync(AdminApiHarness api, bool enabled)
    {
        using var read = await api.GetAsync("/api/v1/server-settings");
        var view = await AdminApiHarness.ReadAsync(read, Paduraksa.Admin.Json.AdminJsonContext.Default.ServerSettingsView, Ct);
        using var put = await api.SendJsonAsync(
            HttpMethod.Put, "/api/v1/server-settings", view.Stored with { McpEnabled = enabled }, Paduraksa.Admin.Json.AdminJsonContext.Default.ServerSettings,
            ifMatch: AdminApiHarness.ETag(read));
        Assert.Equal(System.Net.HttpStatusCode.OK, put.StatusCode);
    }
}
