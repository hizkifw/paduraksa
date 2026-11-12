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

/// <summary>The traffic analytics over the admin port: who may read them, what the query takes, and that a request the proxy answered gets there through the real drain.</summary>
public sealed class AnalyticsApiTests(SharedAdminApi shared) : IClassFixture<SharedAdminApi>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("owner", HttpStatusCode.OK)]
    [InlineData("admin", HttpStatusCode.OK)]
    [InlineData("operator", HttpStatusCode.OK)]
    [InlineData("viewer", HttpStatusCode.OK)]
    [InlineData("auditor", HttpStatusCode.OK)]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    public async Task EveryRoleMayReadAndAnAnonymousCallerMayNot(string? caller, HttpStatusCode expected)
    {
        using var response = await shared.Api.GetAsync("/api/v1/analytics", caller);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task ATokenNeedsTheMetricsScope()
    {
        var api = shared.Api;
        var allowed = await IssueAsync(api, Permission.MetricsRead);
        var refused = await IssueAsync(api, Permission.ConfigRead);

        using var ok = await Bearer(api, "/api/v1/analytics?range=day", allowed);
        using var forbidden = await Bearer(api, "/api/v1/analytics", refused);

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(403, (await ProblemAsync(forbidden, Ct)).Status);
    }

    [Fact]
    public async Task TheOverviewIsJsonForEveryRangeAndEveryScope()
    {
        var api = shared.Api;
        var host = HostId.New().ToString();

        foreach (var (range, step) in new[] { ("hour", 300), ("day", 3_600), ("week", 10_800), ("month", 86_400) })
        {
            using var response = await api.GetAsync($"/api/v1/analytics?range={range}");
            var overview = await ReadAsync(response, AdminJsonContext.Default.AnalyticsOverview, Ct);
            Assert.Equal(step, overview.StepSeconds);
            Assert.Null(overview.Host);
            Assert.False(overview.Unmatched);
        }

        using var one = await api.GetAsync($"/api/v1/analytics?range=week&host={host}");
        var forOne = await ReadAsync(one, AdminJsonContext.Default.AnalyticsOverview, Ct);
        using var none = await api.GetAsync("/api/v1/analytics?host=unmatched");
        var forNobody = await ReadAsync(none, AdminJsonContext.Default.AnalyticsOverview, Ct);
        using var defaults = await api.GetAsync("/api/v1/analytics");
        var byDefault = await ReadAsync(defaults, AdminJsonContext.Default.AnalyticsOverview, Ct);

        Assert.Equal(host, forOne.Host.ToString());
        Assert.Equal(0, forOne.Totals.Requests);
        Assert.True(forNobody.Unmatched);
        Assert.Equal(MetricsRange.Hour, byDefault.Range);
        Assert.Equal(6, byDefault.StatusClasses.Count);
    }

    [Theory]
    [InlineData("range=fortnight")]
    [InlineData("range=Hour")]
    [InlineData("range=1")]
    [InlineData("host=nope")]
    [InlineData("host=ups_0000000000000000000000")]
    [InlineData("host=UNMATCHED")]
    public async Task ABadQueryIsAProblem(string query)
    {
        using var response = await shared.Api.GetAsync("/api/v1/analytics?" + query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(400, (await ProblemAsync(response, Ct)).Status);
    }

    [Fact]
    public async Task OnlyGetIsServed()
    {
        using var response = await shared.Api.SendAsync(HttpMethod.Post, "/api/v1/analytics");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task ARequestTheProxyAnsweredIsInTheAnalyticsOfThoseNoHostOwnsThroughTheRealDrain()
    {
        var api = shared.Api;
        using HttpClient client = new();
        var publicHttp = TestHost.Endpoint(api.Host, HostRole.Public, "http");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(publicHttp, "/analytics-probe/one"));
        request.Headers.Host = "nobody-serves-this.test";
        request.Headers.UserAgent.ParseAdd("GPTBot/1.2");

        using var answered = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.NotFound, answered.StatusCode);

        // The drain runs every 200 ms and the request is in the open bucket as soon as it has read it.
        Contracts.Analytics.AnalyticsOverview? found = null;
        for (var attempt = 0; attempt < 300 && found is null; attempt++)
        {
            using var response = await api.GetAsync("/api/v1/analytics?host=unmatched");
            var overview = await ReadAsync(response, AdminJsonContext.Default.AnalyticsOverview, Ct);
            if (overview.TopPaths.Any(static path => path.Path == "/analytics-probe/one"))
            {
                found = overview;
            }
            else
            {
                await Task.Delay(50, Ct);
            }
        }

        Assert.NotNull(found);
        Assert.Contains(found.TopBots, static bot => bot is { Name: "GPTBot", Kind: "aiCrawler" });
        Assert.True(found.Totals.BotRequests >= 1);
        Assert.True(found.Totals.Visitors >= 1);
        Assert.Contains(found.StatusClasses, static count => count is { Label: "4xx", Count: > 0 });
    }

    private static async Task<string> IssueAsync(AdminApiHarness api, Permission scope)
    {
        var issued = await api.Host.Shared.ApiTokens.IssueAsync(
            new CommandOrigin(Actor.ForUser(api.UserOf("viewer")), Channel.Api, System.Net.IPAddress.Loopback),
            new IssueApiTokenRequest("analytics-reader", [scope]),
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
