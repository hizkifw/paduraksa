using System.Collections.Concurrent;
using System.Globalization;
using System.Net;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Paduraksa.Core.Logging;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Logging;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Yarp;
using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Hosting;
using Paduraksa.Tests.Shared;

namespace Paduraksa.Tests.Integration.Proxy;

/// <summary>
/// The access log end to end: a real process, real Kestrel and YARP, and the core host's drain writing lines
/// into a sink. Every outcome gets one line - forwarded, refused, redirected, an unknown host - in the documented order,
/// with the request ID, the durations and the upstream, and never a cookie, an <c>Authorization</c> header or a secret in
/// a query parameter.
/// </summary>
public sealed class AccessLogEndToEndTests
{
    private static int s_tag;

    [Fact]
    public async Task EveryOutcomeGetsOneLineInTheDocumentedFormat()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using EchoUpstream upstream = await EchoUpstream.StartAsync(ct);
        CapturingSink sink = new();
        await using PaduraksaHost host = TestHost.Create(configureShared: shared => shared.Replace<IAccessLogSink>(sink));
        await host.StartAsync(ct);
        await PublishAsync(host, upstream, ct);
        // Plain HTTP everywhere, and the redirect is the answer under test, not a hop to follow.
        using HttpClient client = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
        // A path segment only this test uses: a stray connection to the reused port (another test's, under a parallel run)
        // can add a line, but it can't carry this tag, so the lines we assert on are ours alone.
        string tag = NextTag();

        // Forwarded.
        using (HttpRequestMessage request = Request(host, "http", "app.test", $"/{tag}/hello?q=1&token=s3cret&page=2"))
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer eyJhbGciOiJIUzI1NiJ9.topsecret.signature");
            request.Headers.TryAddWithoutValidation("Cookie", "__Host-paduraksa-session=live; theme=dark");
            request.Headers.TryAddWithoutValidation("User-Agent", "IntegrationTest/1.0");
            request.Headers.TryAddWithoutValidation("Referer", "https://ref.example/");
            using HttpResponseMessage response = await client.SendAsync(request, ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal($"upstream:/{tag}/hello", await response.Content.ReadAsStringAsync(ct));
            string id = Assert.Single(response.Headers.GetValues(RequestIds.HeaderName));
            Assert.Equal(RequestIds.GeneratedLength, id.Length);
        }

        // Refused by an IP allow list.
        using (HttpResponseMessage denied = await client.SendAsync(Request(host, "http", "blocked.test", $"/{tag}/blocked"), ct))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        // Redirected to HTTPS.
        using (HttpResponseMessage redirect = await client.SendAsync(Request(host, "http", "secure.test", $"/{tag}/x?y=1"), ct))
        {
            Assert.Equal(HttpStatusCode.MovedPermanently, redirect.StatusCode);
        }

        // An unknown host.
        using (HttpResponseMessage unknown = await client.SendAsync(Request(host, "http", "nobody.test", $"/{tag}/unknown"), ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        }

        string forwarded = await sink.WaitForLineAsync($"/{tag}/hello?", ct);
        string refused = await sink.WaitForLineAsync($"/{tag}/blocked", ct);
        string redirected = await sink.WaitForLineAsync($"/{tag}/x?y=1", ct);
        string unknownHost = await sink.WaitForLineAsync($"/{tag}/unknown", ct);
        IReadOnlyList<string> lines = sink.Lines;

        // The forwarded line, field by field.
        string[] fields = Fields(forwarded);
        Assert.Equal(13, fields.Length);
        Assert.True(DateTime.TryParseExact(fields[0], "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out _), fields[0]);
        Assert.Equal("127.0.0.1", fields[1]);
        Assert.Equal("\"app.test\"", fields[2]);
        Assert.Equal($"\"GET /{tag}/hello?q=1&token=REDACTED&page=2 HTTP/1.1\"", fields[3]);
        Assert.Equal("200", fields[4]);
        // A Content-Length the upstream set is the byte count; a chunked body would be '-'.
        Assert.Matches("^(\\d+|-)$", fields[5]);
        Assert.Matches(@"^\d+\.\d{3}$", fields[6]);
        // The upstream leg was measured: a successful forward always has a duration, never '-'.
        Assert.Matches(@"^\d+\.\d{3}$", fields[7]);
        // The destination is the one the config named, which is where YARP chose to send it.
        Assert.Equal($"\"{upstream.Address}\"", fields[8]);
        Assert.Matches($"^\"[0-9a-z]{{8}}-[0-9a-z]{{13}}\"$", fields[9]);
        Assert.Equal("-", fields[10]); // no Cloudflare
        Assert.Equal("\"https://ref.example/\"", fields[11]);
        Assert.Equal("\"IntegrationTest/1.0\"", fields[12]);

        // Nothing secret, from either request.
        foreach (string line in lines)
        {
            Assert.DoesNotContain("topsecret", line, StringComparison.Ordinal);
            Assert.DoesNotContain("Bearer", line, StringComparison.Ordinal);
            Assert.DoesNotContain("__Host-paduraksa-session", line, StringComparison.Ordinal);
            Assert.DoesNotContain("theme=dark", line, StringComparison.Ordinal);
            Assert.DoesNotContain("s3cret", line, StringComparison.Ordinal);
            Assert.DoesNotContain("\n", line, StringComparison.Ordinal);
        }

        // The three answered requests have no upstream leg at all.
        Assert.Equal("403", Fields(refused)[4]);
        Assert.Equal("-", Fields(refused)[7]);
        Assert.Equal("301", Fields(redirected)[4]);
        Assert.Equal("-", Fields(redirected)[7]);
        Assert.Equal("404", Fields(unknownHost)[4]);
        Assert.Equal("-", Fields(unknownHost)[7]);
        Assert.Contains("\"nobody.test\"", Fields(unknownHost)[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHostThatTurnsTheLogOffStillServesRequestsAndLogsNothing()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using EchoUpstream upstream = await EchoUpstream.StartAsync(ct);
        CapturingSink sink = new();
        await using PaduraksaHost host = TestHost.Create(configureShared: shared => shared.Replace<IAccessLogSink>(sink));
        await host.StartAsync(ct);
        await PublishAsync(host, upstream, ct, quiet: true);
        using HttpClient client = TestHost.CreateClient(host);
        string tag = NextTag();

        using HttpResponseMessage quiet = await client.SendAsync(Request(host, "http", "quiet.test", $"/{tag}/quiet"), ct);

        Assert.Equal(HttpStatusCode.OK, quiet.StatusCode);
        // A request to the logged host, recorded after the quiet one, proves the drain is running: by the time its line is
        // out, a line for the quiet request would be out too.
        using HttpResponseMessage logged = await client.SendAsync(Request(host, "http", "app.test", $"/{tag}/logged"), ct);
        Assert.Equal(HttpStatusCode.OK, logged.StatusCode);
        await sink.WaitForLineAsync($"/{tag}/logged", ct);

        Assert.DoesNotContain(sink.Lines, line => line.Contains("\"quiet.test\"", StringComparison.Ordinal));
        Assert.DoesNotContain(sink.Lines, line => line.Contains($"/{tag}/quiet", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ShutdownFlushesWhatIsLeftInTheRing()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using EchoUpstream upstream = await EchoUpstream.StartAsync(ct);
        CapturingSink sink = new();
        PaduraksaHost host = TestHost.Create(configureShared: shared => shared.Replace<IAccessLogSink>(sink));
        await host.StartAsync(ct);
        await PublishAsync(host, upstream, ct);
        using HttpClient client = TestHost.CreateClient(host);

        string tag = NextTag();
        using HttpResponseMessage response = await client.SendAsync(Request(host, "http", "app.test", $"/{tag}/at-stop"), ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await host.StopAsync(ct);
        await host.DisposeAsync();

        Assert.True(sink.Flushed, "the drain should flush on shutdown");
        Assert.Contains(sink.Lines, line => line.Contains($"/{tag}/at-stop", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailedForwardIs502WithTheUpstreamLegRecorded()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using EchoUpstream upstream = await EchoUpstream.StartAsync(ct);
        CapturingSink sink = new();
        await using PaduraksaHost host = TestHost.Create(configureShared: shared => shared.Replace<IAccessLogSink>(sink));
        await host.StartAsync(ct);
        // A port that stays dead for the whole test: bound, never listening, held open until the test ends.
        using TestPorts.DeadPort dead = TestPorts.ReserveDeadPort();
        await PublishAsync(host, upstream, ct, deadAddress: dead.Address);
        using HttpClient client = TestHost.CreateClient(host);
        string tag = NextTag();

        // dead.test forwards to that port: YARP answers 502 itself.
        using HttpResponseMessage failed = await client.SendAsync(Request(host, "http", "dead.test", $"/{tag}/gone"), ct);
        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);

        string line = await sink.WaitForLineAsync($"/{tag}/gone", ct);
        string[] fields = Fields(line);
        Assert.Equal(13, fields.Length);
        Assert.Contains("\"dead.test\"", fields[2], StringComparison.Ordinal);
        Assert.Equal("502", fields[4]);
        // The upstream leg was attempted: it has a duration up to the failure, and names the destination that failed.
        Assert.Matches(@"^\d+\.\d{3}$", fields[7]);
        Assert.Equal($"\"{dead.Address}\"", fields[8]);
    }

    [Fact]
    public async Task RealRequestsLandInTheAccessLogUnderTheDataDirectory()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using EchoUpstream upstream = await EchoUpstream.StartAsync(ct);
        string data = TestDataDirectories.NewPath();
        // No sink is swapped here: the process's own writer, over <data>/logs/access.log, is what the drain writes into.
        await using PaduraksaHost host = TestHost.Create(dataDirectory: data);
        await host.StartAsync(ct);
        await PublishAsync(host, upstream, ct);
        using HttpClient client = TestHost.CreateClient(host);
        string tag = NextTag();

        using HttpResponseMessage response = await client.SendAsync(Request(host, "http", "app.test", $"/{tag}/on-disk?token=s3cret"), ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string line = await WaitForLogAsync(data, tag, ct);
        string[] fields = Fields(line);
        Assert.Equal(13, fields.Length);
        Assert.Equal("\"app.test\"", fields[2]);
        Assert.Equal($"\"GET /{tag}/on-disk?token=REDACTED HTTP/1.1\"", fields[3]);
        Assert.Equal("200", fields[4]);
        Assert.DoesNotContain("s3cret", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALineWrittenJustBeforeStoppingReachesTheFile()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using EchoUpstream upstream = await EchoUpstream.StartAsync(ct);
        string data = TestDataDirectories.NewPath();
        PaduraksaHost host = TestHost.Create(dataDirectory: data);
        await host.StartAsync(ct);
        await PublishAsync(host, upstream, ct);
        using HttpClient client = TestHost.CreateClient(host);
        string tag = NextTag();

        using HttpResponseMessage response = await client.SendAsync(Request(host, "http", "app.test", $"/{tag}/at-stop"), ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The drain's last pass and its flush happen while the core host stops; the log files are only disposed afterwards, with
        // the shared services. So the line is on disk the moment StopAsync returns, which is what proves the order: a writer
        // disposed before the drain, or a drain that skipped its last flush, would leave it in a buffer (the writer's own flush
        // tick is a second away) instead.
        await host.StopAsync(ct);
        string path = Path.Combine(data, LogFileNames.DefaultDirectoryName, LogFileNames.Live(LogFileKind.Access));
        Assert.True(File.Exists(path), $"no access log at {path}");
        string log = await File.ReadAllTextAsync(path, ct);
        Assert.Contains($"GET /{tag}/at-stop", log, StringComparison.Ordinal);

        await host.DisposeAsync();
    }

    /// <summary>The access log's contents once a line carrying <paramref name="tag"/> is in it; the writer flushes once a second.</summary>
    private static async Task<string> WaitForLogAsync(string data, string tag, CancellationToken ct)
    {
        string path = Path.Combine(data, LogFileNames.DefaultDirectoryName, LogFileNames.Live(LogFileKind.Access));
        long deadline = Environment.TickCount64 + 15_000;
        while (true)
        {
            if (File.Exists(path))
            {
                string log = await File.ReadAllTextAsync(path, ct);
                string[] lines = [.. log.Split('\n', StringSplitOptions.RemoveEmptyEntries)];
                if (lines.FirstOrDefault(line => line.Contains(tag, StringComparison.Ordinal)) is { } found)
                {
                    return found;
                }
            }

            if (Environment.TickCount64 >= deadline)
            {
                Assert.Fail($"nothing carrying '{tag}' reached {path} in 15 s");
            }

            await Task.Delay(100, ct);
        }
    }

    /// <summary>A path segment unique to one test: the drain may also see a stray connection, never one carrying this.</summary>
    private static string NextTag() => "a" + Interlocked.Increment(ref s_tag).ToString(CultureInfo.InvariantCulture);

    /// <summary>Compiles and publishes like the server does: its compiler options, the placeholder as the default certificate.</summary>
    private static async Task PublishAsync(PaduraksaHost host, EchoUpstream upstream, CancellationToken ct, bool quiet = false, string? deadAddress = null)
    {
        ProxyConfigModel model = new()
        {
            Revision = TestIds.Revision("access-log"),
            Upstreams =
            [
                new UpstreamSpec { Id = TestIds.Upstream("app"), Destinations = [new(TestIds.Destination("app-1"), upstream.Address)] },
                new UpstreamSpec { Id = TestIds.Upstream("dead"), Destinations = [new(TestIds.Destination("dead-1"), deadAddress ?? upstream.Address)] },
            ],
            Hosts =
            [
                new HostSpec
                {
                    Id = TestIds.Host("app"),
                    Names = ["app.test"],
                    Routes = [new RouteSpec { Id = TestIds.Route("app-root"), Upstream = TestIds.Upstream("app") }],
                },
                new HostSpec
                {
                    Id = TestIds.Host("dead"),
                    Names = ["dead.test"],
                    Routes = [new RouteSpec { Id = TestIds.Route("dead-root"), Upstream = TestIds.Upstream("dead") }],
                },
                quiet
                    ? new HostSpec
                    {
                        Id = TestIds.Host("quiet"),
                        Names = ["quiet.test"],
                        AccessLog = false,
                        Routes = [new RouteSpec { Id = TestIds.Route("quiet-root"), Upstream = TestIds.Upstream("app") }],
                    }
                    : new HostSpec
                    {
                        Id = TestIds.Host("blocked"),
                        Names = ["blocked.test"],
                        Policies = [new IpAllowListPolicySpec(["10.0.0.5"])],
                        Routes = [new RouteSpec { Id = TestIds.Route("blocked-root"), Upstream = TestIds.Upstream("app") }],
                    },
                new HostSpec { Id = TestIds.Host("secure"), Names = ["secure.test"], ForceHttps = true, Routes = [new RouteSpec { Id = TestIds.Route("secure-root"), Upstream = TestIds.Upstream("app") }] },
            ],
        };
        PublishResult published = await host.Shared.SnapshotPublisher.PublishAsync(PublicProxyTests.Compile(host, model), ct);
        Assert.True(published.Succeeded, string.Join("; ", published.Errors));
    }

    private static HttpRequestMessage Request(PaduraksaHost host, string scheme, string hostHeader, string pathAndQuery)
    {
        HttpRequestMessage request = new(HttpMethod.Get, new Uri(TestHost.Endpoint(host, HostRole.Public, scheme), pathAndQuery));
        request.Headers.Host = hostHeader;
        return request;
    }

    /// <summary>Splits a line on the spaces outside its quoted fields.</summary>
    private static string[] Fields(string line)
    {
        List<string> fields = [];
        int start = 0;
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"' && (i == 0 || line[i - 1] != '\\'))
            {
                quoted = !quoted;
            }
            else if (line[i] == ' ' && !quoted)
            {
                fields.Add(line[start..i]);
                start = i + 1;
            }
        }

        fields.Add(line[start..]);
        return [.. fields];
    }

    /// <summary>The sink the drain writes into: keeps every line, and lets a test wait for a number of them.</summary>
    private sealed class CapturingSink : IAccessLogSink
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IReadOnlyList<string> Lines => [.. _lines];

        public bool Flushed { get; private set; }

        public void WriteLine(ReadOnlySpan<char> line) => _lines.Enqueue(line.ToString());

        public void Flush() => Flushed = true;

        /// <summary>
        /// The one line containing <paramref name="fragment"/> (a per-test path segment), waiting up to 15 s for the drain
        /// (it runs every 200 ms). Lines a stray connection added never carry the fragment, so they don't count.
        /// </summary>
        public async Task<string> WaitForLineAsync(string fragment, CancellationToken ct)
        {
            long deadline = Environment.TickCount64 + 15_000;
            while (true)
            {
                string[] matches = [.. Lines.Where(line => line.Contains(fragment, StringComparison.Ordinal))];
                if (matches.Length == 1)
                {
                    return matches[0];
                }

                if (matches.Length > 1)
                {
                    Assert.Fail($"more than one line carries '{fragment}': {string.Join(" | ", matches)}");
                }

                if (Environment.TickCount64 >= deadline)
                {
                    Assert.Fail($"no line carries '{fragment}' after 15 s: {string.Join(" | ", Lines)}");
                }

                await Task.Delay(50, ct);
            }
        }
    }

    /// <summary>A loopback upstream answering <c>upstream:{path}</c> with a known body, and remembering the last path.</summary>
    private sealed class EchoUpstream : IAsyncDisposable
    {
        private readonly IHost _host;

        private EchoUpstream(IHost host, string address)
        {
            _host = host;
            Address = address;
        }

        public string Address { get; }

        public static async Task<EchoUpstream> StartAsync(CancellationToken ct)
        {
            IHost host = new HostBuilder()
                .ConfigureLogging(static logging => logging.ClearProviders())
                .ConfigureWebHost(static web => web
                    .UseKestrel(static kestrel => kestrel.Listen(IPAddress.Loopback, 0))
                    .Configure(static app => app.Run(static context => context.Response.WriteAsync("upstream:" + context.Request.Path))))
                .Build();
            await host.StartAsync(ct);
            string address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new EchoUpstream(host, address + "/");
        }

        public async ValueTask DisposeAsync()
        {
            await _host.StopAsync(CancellationToken.None);
            _host.Dispose();
        }
    }
}
