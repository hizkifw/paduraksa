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
        Assert.Null(Toolkit(off));
    }
}
