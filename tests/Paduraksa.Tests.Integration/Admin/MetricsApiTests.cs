using System.Net;
using System.Net.Http.Headers;

using Paduraksa.Admin.Json;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Identity;
using Paduraksa.Contracts.Identity;
using Paduraksa.Contracts.Metrics;
using Paduraksa.Core.Model;
using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Hosting;
using static Paduraksa.Tests.Integration.Admin.AdminApiHarness;

namespace Paduraksa.Tests.Integration.Admin;

/// <summary>
/// The metrics over the admin port: the Prometheus scrape at <c>/metrics</c> (and its API path), the permission and the bearer-token
/// scope a scraper needs, and the overview the dashboard draws.
/// </summary>
public sealed class MetricsApiTests(SharedAdminApi shared) : IClassFixture<SharedAdminApi>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("owner", HttpStatusCode.OK)]
    [InlineData("admin", HttpStatusCode.OK)]
    [InlineData("operator", HttpStatusCode.OK)]
    [InlineData("viewer", HttpStatusCode.OK)]
    [InlineData("auditor", HttpStatusCode.OK)]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    public async Task EveryRoleMayScrapeAndAnAnonymousCallerMayNot(string? caller, HttpStatusCode expected)
    {
        var api = shared.Api;

        using var scrape = await api.GetAsync("/metrics", caller);
        using var path = await api.GetAsync("/api/v1/metrics", caller);
        using var overview = await api.GetAsync("/api/v1/metrics/overview", caller);

        Assert.Equal(expected, scrape.StatusCode);
        Assert.Equal(expected, path.StatusCode);
        Assert.Equal(expected, overview.StatusCode);
    }

    [Fact]
    public async Task TheScrapeIsPrometheusTextThatIsNeverCached()
    {
        var api = shared.Api;

        using var response = await api.GetAsync("/metrics");
        var text = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain; version=0.0.4; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains("# TYPE paduraksa_requests_total counter\n", text, StringComparison.Ordinal);
        Assert.Contains("# TYPE paduraksa_request_duration_seconds histogram\n", text, StringComparison.Ordinal);
        Assert.Contains("paduraksa_hosts ", text, StringComparison.Ordinal);
        Assert.Contains("paduraksa_rate_limit_decisions_total{result=\"allowed\"}", text, StringComparison.Ordinal);
        // The notification outbox: its queue depth is a gauge, and its outcomes are counters by the kind of channel (never a channel's name).
        Assert.Contains("# TYPE paduraksa_notification_queue_depth gauge\n", text, StringComparison.Ordinal);
        Assert.Contains("paduraksa_notification_queue_depth 0\n", text, StringComparison.Ordinal);
        foreach (var outcome in new[] { "sent", "failed", "expired", "dropped" })
        {
            Assert.Contains($"# TYPE paduraksa_notification_{outcome}_total counter\n", text, StringComparison.Ordinal);
            Assert.Contains($"paduraksa_notification_{outcome}_total{{kind=\"webhook\"}} 0\n", text, StringComparison.Ordinal);
        }

        // The API path is the same endpoint.
        using var viaApi = await api.GetAsync("/api/v1/metrics");
        Assert.Equal(HttpStatusCode.OK, viaApi.StatusCode);
        Assert.StartsWith("# HELP paduraksa_requests_total", await viaApi.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AScraperPresentsABearerTokenWithTheMetricsScope()
    {
        var api = shared.Api;
        var token = await IssueAsync(api, "viewer", Permission.MetricsRead);

        using var scrape = await Bearer(api, "/metrics", token);

        Assert.Equal(HttpStatusCode.OK, scrape.StatusCode);
    }

    [Fact]
    public async Task ATokenWithoutTheScopeIsForbiddenEvenThoughItsUserHasThePermission()
    {
        var api = shared.Api;
        var token = await IssueAsync(api, "viewer", Permission.ConfigRead);

        using var scrape = await Bearer(api, "/metrics", token);

        Assert.Equal(HttpStatusCode.Forbidden, scrape.StatusCode);
        Assert.Equal(403, (await ProblemAsync(scrape, Ct)).Status);
    }

    [Fact]
    public async Task AWrongTokenIsRefusedBeforeAnythingIsRead()
    {
        var api = shared.Api;

        using var scrape = await Bearer(api, "/metrics", "pdk_0000000000000000000000_not-a-token_0000000");

        Assert.Equal(HttpStatusCode.Unauthorized, scrape.StatusCode);
    }

    [Fact]
    public async Task OnlyGetIsServed()
    {
        var api = shared.Api;

        using var response = await api.SendAsync(HttpMethod.Post, "/metrics");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task TheOverviewIsJsonAndTakesARange()
    {
        var api = shared.Api;

        using var hour = await api.GetAsync("/api/v1/metrics/overview");
        var overview = await ReadAsync(hour, AdminJsonContext.Default.MetricsOverview, Ct);
        using var week = await api.GetAsync("/api/v1/metrics/overview?range=week");
        var weekly = await ReadAsync(week, AdminJsonContext.Default.MetricsOverview, Ct);

        Assert.Equal(MetricsRange.Hour, overview.Range);
        Assert.Equal(TimeSpan.FromSeconds(10), overview.BucketWidth);
        Assert.Equal(MetricsRange.Week, weekly.Range);
        Assert.Equal(TimeSpan.FromMinutes(42), weekly.BucketWidth);
    }

    [Theory]
    [InlineData("fortnight")]
    [InlineData("1")]
    [InlineData("Hour")]
    public async Task AnUnknownRangeIsAProblem(string range)
    {
        var api = shared.Api;

        using var response = await api.GetAsync("/api/v1/metrics/overview?range=" + range);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(400, (await ProblemAsync(response, Ct)).Status);
    }

    [Fact]
    public async Task ARequestTheProxyAnsweredIsInTheNextScrapeAndEverySlotIsLabelledFromTheLiveConfig()
    {
        var api = shared.Api;
        using HttpClient client = new();
        var publicHttp = TestHost.Endpoint(api.Host, HostRole.Public, "http");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(publicHttp, "/anything"));
        request.Headers.Host = "nobody-serves-this.test";

        using var answered = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.NotFound, answered.StatusCode);

        // The completion callback counts after the response, so the scrape that follows may need a moment.
        var line = string.Empty;
        for (var attempt = 0; attempt < 200 && line.Length == 0; attempt++)
        {
            using var scrape = await api.GetAsync("/metrics");
            var text = await scrape.Content.ReadAsStringAsync(Ct);
            line = text.Split('\n').FirstOrDefault(l => l.StartsWith("paduraksa_requests_total{host=\"(unmatched)\",host_id=\"\",status_class=\"4xx\"} ", StringComparison.Ordinal) && !l.EndsWith(" 0", StringComparison.Ordinal)) ?? string.Empty;
            if (line.Length == 0)
            {
                // Poll step of a bounded wait for the scrape to show the request.
                await Task.Delay(10, Ct);
            }
        }

        Assert.NotEmpty(line);
    }

    private static async Task<string> IssueAsync(AdminApiHarness api, string role, Permission scope)
    {
        var issued = await api.Host.Shared.ApiTokens.IssueAsync(
            new CommandOrigin(Actor.ForUser(api.UserOf(role)), Channel.Api, System.Net.IPAddress.Loopback),
            new IssueApiTokenRequest("scraper", [scope]),
            Ct);
        return issued.Issued?.Token ?? throw new InvalidOperationException($"No token: {issued.Status} {issued.Problem}");
    }

    private static async Task<HttpResponseMessage> Bearer(AdminApiHarness api, string path, string token)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(api.BaseAddress, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await api.Client.SendAsync(request, Ct);
    }
}
