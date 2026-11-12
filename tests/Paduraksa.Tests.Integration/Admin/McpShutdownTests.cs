namespace Paduraksa.Tests.Integration.Admin;

/// <summary>A sandbox child never outlives the server: stopping the host kills it (PLAN section 11.2).</summary>
public sealed class McpShutdownTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static List<int> Children() =>
    [
        .. Directory.GetDirectories("/proc")
            .Select(d => int.TryParse(Path.GetFileName(d), out var pid) ? pid : 0)
            .Where(pid => pid > 0 && IsSandboxChildOfThisProcess(pid)),
    ];

    private static bool IsSandboxChildOfThisProcess(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            var parent = int.Parse(stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
            return parent == Environment.ProcessId && File.ReadAllText($"/proc/{pid}/cmdline").Contains("mcp-sandbox", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [Fact]
    public async Task StoppingTheServerKillsTheChildrenItStarted()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "reads /proc");
        var api = await AdminApiHarness.StartAsync(Ct);
        List<int> children;
        try
        {
            await using (var client = await McpTestClient.ConnectAsync(api, cancellationToken: Ct))
            {
                Assert.Equal("1", (await client.CallAsync("search", "async () => 1", Ct)).Text);
            }

            children = Children();
            Assert.NotEmpty(children);
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
        Assert.SkipUnless(OperatingSystem.IsLinux(), "reads /proc");
        var before = Children();
        await using var api = await AdminApiHarness.StartAsync(Ct);
        Assert.Equal(before, Children());
        await using var off = await AdminApiHarness.StartAsync(Ct, settings: new Dictionary<string, string?> { ["Paduraksa:Mcp:Enabled"] = "false" });
        Assert.Equal(before, Children());
    }
}
