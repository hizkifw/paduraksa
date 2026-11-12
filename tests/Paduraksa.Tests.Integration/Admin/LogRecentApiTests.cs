using System.Net;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using Paduraksa.Proxy.Logging;
using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Hosting;

namespace Paduraksa.Tests.Integration.Admin;

/// <summary>The live tail's buffer as one JSON read (<c>GET /api/v1/logs/recent</c>): what a client that cannot hold a stream open, MCP's scripts among them, reads instead.</summary>
public sealed class LogRecentApiTests(SharedAdminApi shared) : IClassFixture<SharedAdminApi>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AccessLogTail Hub => shared.Api.Host.GetServices(HostRole.Admin).GetRequiredService<AccessLogTail>();

    private static AccessLogEntry Entry(string path, int status = 200, string? query = null) => new()
    {
        TimestampUtcTicks = DateTime.UtcNow.Ticks,
        ClientAddress = "203.0.113.77",
        Host = "app.test",
        Method = "GET",
        Path = path,
        Query = query,
        Protocol = "HTTP/1.1",
        StatusCode = status,
        RequestId = "rid-" + path,
        UserAgent = "agent/1.0",
    };

    /// <summary>Only what these tests offered: the host's public listener is live, so a stray request to it (a port prober on a developer machine) is in the buffer too.</summary>
    private async Task<JsonElement> ReadAsync(string query = "", string? caller = "owner")
    {
        using var response = await shared.Api.GetAsync("/api/v1/logs/recent?ip=203.0.113." + (query.Length == 0 ? string.Empty : "&" + query.TrimStart('?')), caller);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    [Theory]
    [InlineData("owner", HttpStatusCode.OK)]
    [InlineData("admin", HttpStatusCode.OK)]
    [InlineData("auditor", HttpStatusCode.OK)]
    [InlineData("operator", HttpStatusCode.Forbidden)]
    [InlineData("viewer", HttpStatusCode.Forbidden)]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    public async Task ItNeedsTheLogsReadPermission(string? caller, HttpStatusCode expected)
    {
        using var response = await shared.Api.GetAsync("/api/v1/logs/recent", caller);
        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task TheNewestEntriesComeBackOldestFirstWithTheFileSRedactionAndFiltersAndALimit()
    {
        Hub.Offer(Entry("/recent-a", 200, "?page=1&token=hunter2"));
        Hub.Offer(Entry("/recent-b", 502));
        Hub.Offer(Entry("/recent-c", 200));
        Hub.Offer(Entry("/recent-d", 503));

        var all = await ReadAsync();
        Assert.Equal(["/recent-a", "/recent-b", "/recent-c", "/recent-d"], all.EnumerateArray().Select(e => e.GetProperty("path").GetString()).Where(p => p!.StartsWith("/recent-", StringComparison.Ordinal)));
        Assert.Equal("?page=1&token=REDACTED", all[0].GetProperty("query").GetString());
        Assert.DoesNotContain("hunter2", all.GetRawText(), StringComparison.Ordinal);

        var failures = await ReadAsync("statusClass=5xx&path=recent-");
        Assert.Equal(["/recent-b", "/recent-d"], failures.EnumerateArray().Select(e => e.GetProperty("path").GetString()));

        var newest = await ReadAsync("limit=2");
        Assert.Equal(["/recent-c", "/recent-d"], newest.EnumerateArray().Select(e => e.GetProperty("path").GetString()));
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=1001")]
    [InlineData("limit=many")]
    [InlineData("limit=-1")]
    [InlineData("statusClass=9")]
    [InlineData("unmatched=maybe")]
    public async Task ABadParameterIsA400(string query)
    {
        using var response = await shared.Api.GetAsync("/api/v1/logs/recent?" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await AdminApiHarness.ProblemAsync(response, Ct);
        Assert.NotNull(problem.Detail);
    }

    [Fact]
    public async Task TheApiDescriptionHasIt()
    {
        using var response = await shared.Api.GetAsync("/api/v1/openapi.json", null);
        var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
        var operation = document.GetProperty("paths").GetProperty("/api/v1/logs/recent").GetProperty("get");
        Assert.Equal("ListRecentAccessLog", operation.GetProperty("operationId").GetString());
        Assert.Equal("logs:read", operation.GetProperty("x-required-permission").GetString());
    }
}
