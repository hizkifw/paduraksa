using Paduraksa.Mcp;

namespace Paduraksa.Tests.Unit.Mcp;

public sealed class ToolDescriptionTests
{
    [Fact]
    public void TheDescriptionsStateTheLimitsInForceAndStayShort()
    {
        var limits = new SandboxLimits
        {
            CpuTime = TimeSpan.FromSeconds(2),
            ScriptTime = TimeSpan.FromSeconds(3),
            TotalTime = TimeSpan.FromSeconds(12),
            ChildHeapBytes = 8L * 1024 * 1024,
            MaxRequests = 7,
            MaxOutputBytes = 16 * 1024,
        };

        foreach (var text in new[] { ToolDescriptions.Search(limits), ToolDescriptions.Execute(limits) })
        {
            Assert.Contains("2 s of CPU", text, StringComparison.Ordinal);
            Assert.Contains("3 s on the clock", text, StringComparison.Ordinal);
            Assert.Contains("8 MiB of memory", text, StringComparison.Ordinal);
            Assert.Contains("16 KiB", text, StringComparison.Ordinal);
            Assert.Contains("Example: async () =>", text, StringComparison.Ordinal);
            Assert.InRange(text.Length, 700, 2600);
        }

        var execute = ToolDescriptions.Execute(limits);
        Assert.Contains("7 requests", execute, StringComparison.Ordinal);
        Assert.Contains("12 s in all", execute, StringComparison.Ordinal);
    }

    [Fact]
    public void ExecuteNamesTheRequestShapeTheTwoStepRuleAndWhatIsRefused()
    {
        var execute = ToolDescriptions.Execute(SandboxLimits.Default);
        foreach (var part in new[] { "paduraksa.request({ method, path, query?, body?, confirm?, ifMatch? })", "{ status, body, etag? }", "confirm: true", "dryRun", "status 428", "backups", "API tokens", "/api/v1/logs/recent", "/api/v1/metrics/overview" })
        {
            Assert.Contains(part, execute, StringComparison.Ordinal);
        }

        var search = ToolDescriptions.Search(SandboxLimits.Default);
        Assert.Contains("`spec`", search, StringComparison.Ordinal);
        Assert.Contains("x-required-permission", search, StringComparison.Ordinal);
        Assert.Contains("x-recursive-ref", search, StringComparison.Ordinal);
        Assert.Contains("is stopped", search, StringComparison.Ordinal);
    }
}
