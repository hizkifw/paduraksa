using Paduraksa.Contracts.Logging;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard;
using Paduraksa.Dashboard.Config;
using Paduraksa.Dashboard.Layout;
using Paduraksa.Dashboard.LogFiles;

namespace Paduraksa.Tests.Dashboard.Components;

public sealed class LiveTailTextTests
{
    [Theory]
    [InlineData(null, Tone.Default)]
    [InlineData(200, Tone.Success)]
    [InlineData(204, Tone.Success)]
    [InlineData(301, Tone.Info)]
    [InlineData(404, Tone.Warning)]
    [InlineData(499, Tone.Warning)]
    [InlineData(500, Tone.Error)]
    [InlineData(504, Tone.Error)]
    public void AStatusIsColouredByItsClass(int? status, Tone expected) => Assert.Equal(expected, LiveTailText.ToneOf(status));

    [Theory]
    [InlineData(null, "-")]
    [InlineData(0.42, "0.42 ms")]
    [InlineData(38.04, "38 ms")]
    [InlineData(380.6, "381 ms")]
    [InlineData(1234.0, "1.23 s")]
    public void DurationsAreWordedForReading(double? milliseconds, string expected) => Assert.Equal(expected, LiveTailText.Duration(milliseconds));

    [Theory]
    [InlineData("http://10.0.0.2:8080/", "10.0.0.2:8080")]
    [InlineData("https://app.internal/base/", "app.internal/base")]
    [InlineData(null, "-")]
    public void TheUpstreamDropsItsSchemeAndTrailingSlash(string? upstream, string expected) => Assert.Equal(expected, LiveTailText.UpstreamText(upstream));

    [Fact]
    public void TheSimulatorLinkCarriesHostMethodPathAndClient()
    {
        LogTailEntry entry = new(
            1, DateTimeOffset.UtcNow, 1, ClientAddress: "2001:db8::1", Host: "app.test", MatchedHost: "app.test", Method: "PUT", Path: "/a b/c", Query: "?x=1&y=REDACTED");

        var link = LiveTailText.SimulatorLink(entry);

        Assert.StartsWith("/simulator?url=" + Uri.EscapeDataString("https://app.test/a b/c?x=1&y=REDACTED"), link, StringComparison.Ordinal);
        Assert.Contains("&clientIp=" + Uri.EscapeDataString("2001:db8::1"), link, StringComparison.Ordinal);
        Assert.EndsWith("&method=PUT", link, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveTailIsListedUnderInsightForWhoeverMayReadTheLogs()
    {
        var entry = Assert.Single(DashboardNavigation.Entries, e => e.Href == DashboardRoutes.LiveTail);

        Assert.Equal(DashboardNavigation.InsightGroup, entry.Group);
        Assert.Equal(Permission.LogsRead, entry.Requires);
        // /logs/live is a page of its own: it marks its entry, and /logs still marks Logs.
        Assert.Equal(entry, DashboardNavigation.ActiveFor("/logs/live", DashboardNavigation.Entries));
        Assert.Equal("Logs", DashboardNavigation.ActiveFor("/logs", DashboardNavigation.Entries)!.Title);
    }
}
