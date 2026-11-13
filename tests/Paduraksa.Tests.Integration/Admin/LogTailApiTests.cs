using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Paduraksa.Application.Logging;
using Paduraksa.Proxy.Logging;
using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Hosting;

namespace Paduraksa.Tests.Integration.Admin;

/// <summary>
/// The live tail over the API: Server-Sent Events behind <c>logs:read</c>, the filters, the heartbeat, the bound on how many
/// streams the process serves, and that a client that goes away frees its place.
/// </summary>
public sealed class LogTailApiTests(SharedAdminApi shared) : IClassFixture<SharedAdminApi>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AccessLogEntry Entry(string path, int status = 200, string host = "app.test", string? query = null) => new()
    {
        TimestampUtcTicks = DateTime.UtcNow.Ticks,
        ClientAddress = "198.51.100.7",
        Host = host,
        Method = "GET",
        Path = path,
        Query = query,
        Protocol = "HTTP/1.1",
        StatusCode = status,
        RequestId = "rid-" + path,
        UserAgent = "agent/1.0",
    };

    private AccessLogTail Hub => shared.Api.Host.GetServices(HostRole.Admin).GetRequiredService<AccessLogTail>();

    private LogTailService Service => shared.Api.Host.GetServices(HostRole.Admin).GetRequiredService<LogTailService>();

    /// <summary>
    /// Opens the stream for the requests these tests offer (their client address): the host's public listener is live, so a stray
    /// request to it (a port prober on a developer machine) would otherwise be in the stream too.
    /// </summary>
    private async Task<(HttpResponseMessage Response, StreamReader Reader)> OpenAsync(string query = "", string? caller = "owner")
    {
        query = (query.Length == 0 ? "?" : query + "&") + "ip=198.51.100.";
        using var request = shared.Api.Request(HttpMethod.Get, "/api/v1/logs/tail" + query, caller);
        var response = await shared.Api.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
        return (response, new StreamReader(await response.Content.ReadAsStreamAsync(Ct)));
    }

    /// <summary>The next line that says something: the blank line that ends each event is only a separator.</summary>
    private static async Task<string> NextLineAsync(StreamReader reader)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        while (true)
        {
            var line = await reader.ReadLineAsync(timeout.Token) ?? throw new EndOfStreamException("The stream ended.");
            if (line.Length > 0)
            {
                return line;
            }
        }
    }

    /// <summary>The next data event's JSON, skipping comments and blank lines.</summary>
    private static async Task<JsonElement> NextEntryAsync(StreamReader reader)
    {
        while (true)
        {
            var line = await NextLineAsync(reader);
            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                return JsonDocument.Parse(line["data: ".Length..]).RootElement;
            }
        }
    }

    private async Task WaitForSubscribersAsync(int expected)
    {
        for (var attempt = 0; attempt < 400 && Hub.SubscriberCount != expected; attempt++)
        {
            // Poll step of a wait bounded to ten seconds.
            await Task.Delay(25, Ct);
        }

        Assert.Equal(expected, Hub.SubscriberCount);
    }

    [Theory]
    [InlineData("owner", HttpStatusCode.OK)]
    [InlineData("admin", HttpStatusCode.OK)]
    [InlineData("auditor", HttpStatusCode.OK)]
    [InlineData("operator", HttpStatusCode.Forbidden)]
    [InlineData("viewer", HttpStatusCode.Forbidden)]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    public async Task TheStreamNeedsTheReadPermission(string? caller, HttpStatusCode expected)
    {
        var (response, reader) = await OpenAsync(caller: caller);
        using (response)
        using (reader)
        {
            Assert.Equal(expected, response.StatusCode);
            if (expected == HttpStatusCode.OK)
            {
                Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
                Assert.Contains("no-store", response.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
            }
        }

        await WaitForSubscribersAsync(0);
    }

    [Fact]
    public async Task EntriesArriveAsEventsWithTheirSequenceAndWhatTheFileWouldShow()
    {
        var (response, reader) = await OpenAsync();
        using (response)
        using (reader)
        {
            Assert.Equal(": open", await NextLineAsync(reader));
            Hub.Offer(Entry("/api/items", 201, query: "?page=2&token=hunter2"));

            string? id = null;
            string? data = null;
            while (data is null)
            {
                var line = await NextLineAsync(reader);
                if (line.StartsWith("id: ", StringComparison.Ordinal))
                {
                    id = line["id: ".Length..];
                }
                else if (line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    data = line["data: ".Length..];
                }
            }

            var entry = JsonDocument.Parse(data).RootElement;
            Assert.Equal(id, entry.GetProperty("sequence").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal("/api/items", entry.GetProperty("path").GetString());
            Assert.Equal("?page=2&token=REDACTED", entry.GetProperty("query").GetString());
            Assert.Equal(201, entry.GetProperty("status").GetInt32());
            Assert.Equal("GET", entry.GetProperty("method").GetString());
            Assert.Equal("198.51.100.7", entry.GetProperty("clientAddress").GetString());
            Assert.Equal("rid-/api/items", entry.GetProperty("requestIdentifier").GetString());
            Assert.False(entry.TryGetProperty("matchedHost", out _));
            Assert.DoesNotContain("hunter2", data, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task FiltersKeepOnlyTheMatchingRequestsAndABacklogComesFirst()
    {
        Hub.Offer(Entry("/earlier-ok"));
        Hub.Offer(Entry("/earlier-bad", 502));
        var (response, reader) = await OpenAsync("?statusClass=5xx&method=get&path=bad&backlog=50");
        using (response)
        using (reader)
        {
            Assert.Equal(": open", await NextLineAsync(reader));
            Assert.Equal("/earlier-bad", (await NextEntryAsync(reader)).GetProperty("path").GetString());

            Hub.Offer(Entry("/live-ok"));
            Hub.Offer(Entry("/live-bad", 503));
            Hub.Offer(Entry("/live-bad-too-teapot", 418));

            var live = await NextEntryAsync(reader);
            Assert.Equal("/live-bad", live.GetProperty("path").GetString());
            Assert.Equal(503, live.GetProperty("status").GetInt32());
        }
    }

    [Fact]
    public async Task AQuietStreamSendsAHeartbeatSoItStaysOpen()
    {
        var heartbeat = Service.Heartbeat;
        Service.Heartbeat = TimeSpan.FromMilliseconds(100);
        try
        {
            var (response, reader) = await OpenAsync();
            using (response)
            using (reader)
            {
                Assert.Equal(": open", await NextLineAsync(reader));
                Assert.Equal(": heartbeat", await NextLineAsync(reader));
            }
        }
        finally
        {
            Service.Heartbeat = heartbeat;
        }
    }

    [Fact]
    public async Task AReaderThatFallsBehindIsToldHowManyEntriesItMissed()
    {
        var (response, reader) = await OpenAsync();
        using (response)
        using (reader)
        {
            Assert.Equal(": open", await NextLineAsync(reader));

            // Far more than the sockets and the stream's buffer can hold while nobody reads.
            for (var i = 0; i < 40_000; i++)
            {
                Hub.Offer(Entry("/burst/" + i) with { UserAgent = new string('u', 400) });
            }

            long skipped = 0;
            while (skipped == 0)
            {
                var line = await NextLineAsync(reader);
                if (line == "event: skipped")
                {
                    skipped = JsonDocument.Parse((await NextLineAsync(reader))["data: ".Length..]).RootElement.GetProperty("count").GetInt64();
                }
            }

            Assert.True(skipped > 0);
        }
    }

    [Fact]
    public async Task AClientThatDisconnectsFreesItsPlaceAndTheProcessServesAtMostThirtyTwoStreams()
    {
        var (first, firstReader) = await OpenAsync();
        Assert.Equal(": open", await NextLineAsync(firstReader));
        await WaitForSubscribersAsync(1);

        firstReader.Dispose();
        first.Dispose();
        await WaitForSubscribersAsync(0);

        var held = Enumerable.Range(0, AccessLogTail.MaxSubscribers).Select(_ => Hub.Subscribe(AccessLogTailFilter.All, 0)!).ToList();
        try
        {
            var (refused, refusedReader) = await OpenAsync();
            using (refused)
            using (refusedReader)
            {
                Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
                Assert.Equal("5", refused.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        finally
        {
            held.ForEach(subscription => subscription.Dispose());
        }

        await WaitForSubscribersAsync(0);
    }

    [Theory]
    [InlineData("?statusClass=6")]
    [InlineData("?statusClass=x")]
    [InlineData("?status=99")]
    [InlineData("?status=600")]
    [InlineData("?unmatched=maybe")]
    [InlineData("?backlog=1001")]
    [InlineData("?backlog=-1")]
    [InlineData("?host=a.test&unmatched=true")]
    public async Task ABadParameterIsAProblemAndOpensNoStream(string query)
    {
        var (response, reader) = await OpenAsync(query);
        using (response)
        using (reader)
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        }

        await WaitForSubscribersAsync(0);
    }

    [Fact]
    public async Task ATooLongFilterTextIsAProblem()
    {
        var (response, reader) = await OpenAsync("?path=" + new string('a', 257));
        using (response)
        using (reader)
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task TheOpenApiDocumentDescribesTheStream()
    {
        using var response = await shared.Api.GetAsync("/api/v1/openapi.json");
        var document = await response.Content.ReadAsStringAsync(Ct);

        Assert.Contains("/api/v1/logs/tail", document, StringComparison.Ordinal);
        Assert.Contains("text/event-stream", document, StringComparison.Ordinal);
        Assert.Contains("StreamAccessLog", document, StringComparison.Ordinal);
    }
}
