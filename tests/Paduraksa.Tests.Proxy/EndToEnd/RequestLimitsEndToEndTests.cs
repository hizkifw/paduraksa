using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

using Microsoft.AspNetCore.Server.Kestrel.Core;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.EndToEnd;

/// <summary>
/// The request limits on real Kestrel and YARP: what the gate refuses up front (a declared body, the request target, the
/// headers), what Kestrel cuts off while the body streams, on HTTP/1.1 and HTTP/2, and that an upstream never sees a body
/// that was refused.
/// </summary>
public sealed class RequestLimitsEndToEndTests
{
    private const long HostBodyLimit = 64 * 1024;
    private const long UploadBodyLimit = 256 * 1024;
    private const int UrlLimit = 2048;
    private const int HeaderLimit = 30;
    private const int HeaderBytesLimit = 1024;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADeclaredBodyOverTheLimitIsRefusedBeforeTheUpstreamSeesIt(bool h2c)
    {
        await using var fixture = await Fixture.StartAsync(h2c);

        using var response = await fixture.SendBodyAsync("limits.test", "/", HostBodyLimit + 1, declared: true);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("content too large\n", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Upstream.Requests);
        Assert.Equal(0, fixture.Upstream.Bytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABodyWithinTheLimitIsForwardedWhole(bool h2c)
    {
        await using var fixture = await Fixture.StartAsync(h2c);

        using var declared = await fixture.SendBodyAsync("limits.test", "/", HostBodyLimit, declared: true);
        Assert.Equal(HttpStatusCode.OK, declared.StatusCode);
        Assert.Equal(HostBodyLimit, fixture.Upstream.Bytes);

        // Kestrel counts the chunk framing of an HTTP/1.1 body against the limit, so a chunked body is taken a little under it.
        const long StreamedLength = HostBodyLimit - 1024;
        using var streamed = await fixture.SendBodyAsync("limits.test", "/", StreamedLength, declared: false);
        Assert.Equal(HttpStatusCode.OK, streamed.StatusCode);
        Assert.Equal(HostBodyLimit + StreamedLength, fixture.Upstream.Bytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABodyWithNoDeclaredLengthIsCutOffByKestrelWhenItPassesTheLimit(bool h2c)
    {
        await using var fixture = await Fixture.StartAsync(h2c);

        // The server answers and closes while an HTTP/1.1 client is still sending, so that client may see the reset first.
        HttpStatusCode? status;
        try
        {
            using var response = await fixture.SendBodyAsync("limits.test", "/", HostBodyLimit + 16 * 1024, declared: false);
            status = response.StatusCode;
        }
        catch (HttpRequestException) when (!h2c)
        {
            status = null;
        }

        Assert.True(status is null or HttpStatusCode.RequestEntityTooLarge, $"answered {status}");
        // The upstream may have been handed the start of it (the proxy streams), never more than the limit.
        Assert.True(fixture.Upstream.Bytes <= HostBodyLimit, $"the upstream received {fixture.Upstream.Bytes} bytes");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARouteTakesItsOwnBodyLimit(bool h2c)
    {
        await using var fixture = await Fixture.StartAsync(h2c);

        using var upload = await fixture.SendBodyAsync("limits.test", "/upload", UploadBodyLimit, declared: true);
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);

        using var tooLarge = await fixture.SendBodyAsync("limits.test", "/upload", UploadBodyLimit + 1, declared: true);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);

        using var other = await fixture.SendBodyAsync("limits.test", "/other", HostBodyLimit + 1, declared: true);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, other.StatusCode);

        using var unlimited = await fixture.SendBodyAsync("limits.test", "/unlimited", 2 * UploadBodyLimit, declared: false);
        Assert.Equal(HttpStatusCode.OK, unlimited.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARequestTargetOverTheLimitGets414WithTheHostsHeaders(bool h2c)
    {
        await using var fixture = await Fixture.StartAsync(h2c);

        var allowed = "/" + new string('a', UrlLimit - 1);
        using var within = await fixture.GetAsync("limits.test", allowed);
        Assert.Equal(HttpStatusCode.OK, within.StatusCode);

        using var over = await fixture.GetAsync("limits.test", allowed + "a");
        Assert.Equal(HttpStatusCode.RequestUriTooLong, over.StatusCode);
        Assert.Equal("uri too long\n", await over.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Paduraksa", Assert.Single(over.Headers.GetValues("Server")));
        Assert.True(over.Headers.Contains("X-Request-Id"));

        // The query counts too, and a browser gets the page.
        using var query = await fixture.GetAsync("limits.test", "/?" + new string('q', UrlLimit), html: true);
        Assert.Equal(HttpStatusCode.RequestUriTooLong, query.StatusCode);
        Assert.Contains("414 URI Too Long", await query.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal(1, fixture.Upstream.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoreHeadersOrMoreHeaderBytesThanTheHostTakesGet431(bool h2c)
    {
        await using var fixture = await Fixture.StartAsync(h2c);

        using var few = await fixture.GetAsync("limits.test", "/", headers: Enumerable.Range(0, 5).Select(i => ($"X-Test-{i}", "v")));
        Assert.Equal(HttpStatusCode.OK, few.StatusCode);

        using var many = await fixture.GetAsync("limits.test", "/", headers: Enumerable.Range(0, HeaderLimit + 10).Select(i => ($"X-Test-{i}", "v")));
        Assert.Equal(HttpStatusCode.RequestHeaderFieldsTooLarge, many.StatusCode);
        Assert.Equal("request header fields too large\n", await many.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Paduraksa", Assert.Single(many.Headers.GetValues("Server")));

        using var big = await fixture.GetAsync("limits.test", "/", headers: [("X-Big", new string('b', HeaderBytesLimit + 1))], html: true);
        Assert.Equal(HttpStatusCode.RequestHeaderFieldsTooLarge, big.StatusCode);
        Assert.Contains("431 Request Header Fields Too Large", await big.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal(1, fixture.Upstream.Requests);
    }

    [Fact]
    public async Task TheLimitsAreExactOnTheWire()
    {
        await using var fixture = await Fixture.StartAsync(h2c: false);

        // Host and Connection are two of the lines; the names and values together are what the host counts.
        static string Request(int extra, int valueLength) =>
            "GET / HTTP/1.1\r\nHost: limits.test\r\nConnection: close\r\n"
            + string.Concat(Enumerable.Range(0, extra).Select(i => $"X-{i}: v\r\n"))
            + (valueLength > 0 ? $"X-Pad: {new string('p', valueLength)}\r\n" : string.Empty)
            + "\r\n";
        static int Bytes(int extra, int valueLength) =>
            "Host".Length + "limits.test".Length + "Connection".Length + "close".Length
            + Enumerable.Range(0, extra).Sum(i => $"X-{i}".Length + 1)
            + (valueLength > 0 ? "X-Pad".Length + valueLength : 0);

        Assert.Equal(200, await StatusAsync(fixture, Request(HeaderLimit - 2, 0)));
        Assert.Equal(431, await StatusAsync(fixture, Request(HeaderLimit - 1, 0)));

        var pad = HeaderBytesLimit - Bytes(0, 1) + 1;
        Assert.Equal(HeaderBytesLimit, Bytes(0, pad));
        Assert.Equal(200, await StatusAsync(fixture, Request(0, pad)));
        Assert.Equal(431, await StatusAsync(fixture, Request(0, pad + 1)));
    }

    [Fact]
    public async Task AContentLengthHeaderCountsLikeAnyOtherAndSurvivesTheCheck()
    {
        await using var fixture = await Fixture.StartAsync(h2c: false);

        // Content-Length is one more line and 14 + 2 bytes of "Content-Length" and "10": the host takes the request at the
        // limit, refuses it one line or byte over, and the upstream still gets the whole body the header declares.
        const string Body = "0123456789";
        static string Request(int extra, int pad) =>
            "POST / HTTP/1.1\r\nHost: limits.test\r\nConnection: close\r\nContent-Length: 10\r\n"
            + string.Concat(Enumerable.Range(0, extra).Select(i => $"X-{i}: v\r\n"))
            + (pad > 0 ? $"X-Pad: {new string('p', pad)}\r\n" : string.Empty)
            + "\r\n" + Body;

        Assert.Equal(200, await StatusAsync(fixture, Request(HeaderLimit - 3, 0)));
        Assert.Equal(10, fixture.Upstream.Bytes);
        Assert.Equal(431, await StatusAsync(fixture, Request(HeaderLimit - 2, 0)));

        var fixedBytes = "Host".Length + "limits.test".Length + "Connection".Length + "close".Length + "Content-Length".Length + 2 + "X-Pad".Length;
        var pad = HeaderBytesLimit - fixedBytes;
        Assert.Equal(200, await StatusAsync(fixture, Request(0, pad)));
        Assert.Equal(20, fixture.Upstream.Bytes);
        Assert.Equal(431, await StatusAsync(fixture, Request(0, pad + 1)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheListenerNeverRefusesWhatTheLargestHostLimitsAdmit(bool h2c)
    {
        await using var fixture = await Fixture.StartAsync(h2c);

        // One 20 KiB field (a cookie) is under the host's limit, and over what Kestrel would take of a field by default on HTTP/2.
        using var cookie = await fixture.GetAsync("wide.test", "/", headers: [("Cookie", "c=" + new string('c', 20 * 1024))]);
        Assert.Equal(HttpStatusCode.OK, cookie.StatusCode);

        // The longest target with a lot of header bytes: on HTTP/2 the target counts again with the pseudo-headers, and
        // the gate, not the server, has to be the one that answers at the host's limit.
        var target = "/" + new string('a', RequestLimitBounds.MaxUrlLength - 1);
        using var heavy = await fixture.GetAsync("wide.test", target, headers: Enumerable.Range(0, 4).Select(i => ($"X-Pad-{i}", new string('h', 15 * 1024))));
        Assert.Equal(HttpStatusCode.OK, heavy.StatusCode);
        using var over = await fixture.GetAsync("wide.test", target, headers: Enumerable.Range(0, 4).Select(i => ($"X-Pad-{i}", new string('h', 16 * 1024 + 512))));
        Assert.Equal(HttpStatusCode.RequestHeaderFieldsTooLarge, over.StatusCode);
    }

    [Fact]
    public async Task ALongMethodWithTheLongestTargetIsAdmittedToo()
    {
        await using var fixture = await Fixture.StartAsync(h2c: false);

        // The request line is method, target and protocol: a method token has no length of its own.
        var line = new string('M', 200) + " /" + new string('a', RequestLimitBounds.MaxUrlLength - 1) + " HTTP/1.1\r\n";
        Assert.Equal(200, await StatusAsync(fixture, line + "Host: wide.test\r\nConnection: close\r\n\r\n"));
    }

    private static async Task<int> StatusAsync(Fixture fixture, string request)
    {
        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, fixture.Port, TestContext.Current.CancellationToken);
        await using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), TestContext.Current.CancellationToken);
        using MemoryStream received = new();
        await stream.CopyToAsync(received, TestContext.Current.CancellationToken);
        return int.Parse(Encoding.ASCII.GetString(received.ToArray()).AsSpan(9, 3), System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ProxyTestServer _server;
        private readonly HttpClient _client;

        private Fixture(ProxyTestServer server, BodyCountingUpstream upstream)
        {
            _server = server;
            Upstream = upstream;
            _client = ProxyTestServer.CreateClient(maxConnections: 4);
            _client.Timeout = TimeSpan.FromSeconds(30);
        }

        public BodyCountingUpstream Upstream { get; }

        public static async Task<Fixture> StartAsync(bool h2c)
        {
            var upstream = await BodyCountingUpstream.StartAsync(TestContext.Current.CancellationToken);
            ProxyConfigModel model = new()
            {
                Upstreams = [TestSnapshots.Upstream("web", upstream.Address)],
                Hosts =
                [
                    ProxyHost(
                        "limits", "limits.test",
                        Route("limits-upload", "web", "/upload") with { MaxBodySize = UploadBodyLimit, MaxBodySizeRouteOverride = true },
                        Route("limits-unlimited", "web", "/unlimited") with { MaxBodySize = RequestLimitBounds.UnlimitedBodySize, MaxBodySizeRouteOverride = true },
                        Route("limits-root", "web"))
                        with
                    {
                        RequestLimits = new HostRequestLimitsSpec
                        {
                            MaxBodySize = HostBodyLimit,
                            MaxUrlLength = UrlLimit,
                            MaxHeaderCount = HeaderLimit,
                            MaxHeaderBytes = HeaderBytesLimit,
                        },
                    },
                    ProxyHost("wide", "wide.test", Route("wide-root", "web")) with
                    {
                        RequestLimits = new HostRequestLimitsSpec { MaxUrlLength = RequestLimitBounds.MaxUrlLength, MaxHeaderBytes = RequestLimitBounds.MaxHeaderBytes },
                    },
                ],
            };
            var server = await ProxyTestServer.StartAsync(model, new() { Protocols = h2c ? HttpProtocols.Http2 : HttpProtocols.Http1 });
            return new Fixture(server, upstream);
        }

        public int Port => _server.BaseAddress.Port;

        public async Task<HttpResponseMessage> GetAsync(string host, string target, IEnumerable<(string Name, string Value)>? headers = null, bool html = false)
        {
            var request = _server.Request(host, target);
            foreach (var (name, value) in headers ?? [])
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }

            if (html)
            {
                request.Headers.Accept.ParseAdd("text/html");
            }

            return await _client.SendAsync(request, TestContext.Current.CancellationToken);
        }

        public async Task<HttpResponseMessage> SendBodyAsync(string host, string path, long length, bool declared)
        {
            var request = _server.Request(host, path, HttpMethod.Post);
            request.Content = new StreamContent(new ZeroStream(length));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            if (declared)
            {
                request.Content.Headers.ContentLength = length;

                // The client waits for the server's word before sending the body, as a client of a large upload does: a
                // refusal then comes back before any of it is sent, instead of racing the connection the server closes.
                request.Headers.ExpectContinue = true;
            }

            return await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _server.DisposeAsync();
            await Upstream.DisposeAsync();
        }
    }

    /// <summary>A readable stream of <c>length</c> zero bytes, without allocating them.</summary>
    private sealed class ZeroStream(long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var count = (int)Math.Min(buffer.Length, length - _position);
            buffer[..count].Clear();
            _position += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(Read(buffer.Span));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
