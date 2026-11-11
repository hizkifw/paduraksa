using System.Net;
using System.Net.Http.Headers;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Hosting;
using Paduraksa.Tests.Shared;

namespace Paduraksa.Tests.Integration.Proxy;

/// <summary>
/// An upload through a real <c>PaduraksaHost</c> (the public app's own Kestrel limits and pipeline): a body under the host's
/// limit reaches the upstream whole, one over it is refused, and the upstream never sees a byte of what was refused. The
/// route that takes bigger uploads proves the limit is per route, and a long target and many headers are refused too, with
/// the limits above Kestrel's defaults honoured because the listener makes room for them.
/// </summary>
public sealed class PublicRequestLimitsTests
{
    private const long HostLimit = 64 * 1024;
    private const long UploadLimit = 1024 * 1024;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnUploadUnderTheLimitForwardsAndOneOverItIsRefusedBeforeTheUpstreamSeesIt()
    {
        await using var upstream = await BodyCountingUpstream.StartAsync(Ct);
        await using var host = TestHost.Create();
        await host.StartAsync(Ct);
        var published = await host.Shared.SnapshotPublisher.PublishAsync(Compile(host, upstream.Address), Ct);
        Assert.True(published.Succeeded, string.Join("; ", published.Errors));
        using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(30) };
        var endpoint = TestHost.Endpoint(host, HostRole.Public, "http");

        // Under the limit, declared and streamed (chunked): the upstream gets all of it.
        using var small = await client.SendAsync(Upload(endpoint, "/", 40 * 1024, declared: true), Ct);
        Assert.Equal(HttpStatusCode.OK, small.StatusCode);
        using var smallChunked = await client.SendAsync(Upload(endpoint, "/", 40 * 1024, declared: false), Ct);
        Assert.Equal(HttpStatusCode.OK, smallChunked.StatusCode);
        Assert.Equal(80 * 1024, upstream.Bytes);
        Assert.Equal(2, Posts(upstream));

        // Over it with a declared length: refused up front, nothing forwarded.
        using var declared = await client.SendAsync(Upload(endpoint, "/", HostLimit + 1, declared: true), Ct);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, declared.StatusCode);
        Assert.Equal("content too large\n", await declared.Content.ReadAsStringAsync(Ct));
        Assert.Equal("Paduraksa", Assert.Single(declared.Headers.GetValues("Server")));
        Assert.Equal(2, Posts(upstream));
        Assert.Equal(80 * 1024, upstream.Bytes);

        // Over it with no length: Kestrel cuts the body off once it passes the limit, and the refused part never completes. The
        // server answers and closes while the client is still sending, so a client may see the reset before the answer.
        var streamed = await StatusOfCutOffAsync(client, Upload(endpoint, "/", HostLimit + 16 * 1024, declared: false));
        Assert.True(streamed is null or HttpStatusCode.RequestEntityTooLarge, $"answered {streamed}");
        Assert.True(upstream.Bytes <= 80 * 1024 + HostLimit, $"the upstream received {upstream.Bytes} bytes");

        // The upload route has a bigger limit of its own.
        var before = upstream.Bytes;
        using var upload = await client.SendAsync(Upload(endpoint, "/upload", UploadLimit, declared: true), Ct);
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        Assert.Equal(before + UploadLimit, upstream.Bytes);
        using var uploadTooLarge = await client.SendAsync(Upload(endpoint, "/upload", UploadLimit + 1, declared: true), Ct);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, uploadTooLarge.StatusCode);
        Assert.Equal(before + UploadLimit, upstream.Bytes);
    }

    [Fact]
    public async Task TheListenerMakesRoomForLimitsAboveKestrelsDefaults()
    {
        await using var upstream = await BodyCountingUpstream.StartAsync(Ct);
        await using var host = TestHost.Create();
        await host.StartAsync(Ct);
        var published = await host.Shared.SnapshotPublisher.PublishAsync(Compile(host, upstream.Address, longTargets: true), Ct);
        Assert.True(published.Succeeded, string.Join("; ", published.Errors));
        using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(30) };
        var endpoint = TestHost.Endpoint(host, HostRole.Public, "http");

        // 12 KiB of target is more than Kestrel's 8 KiB default and within what this host allows; one byte more than the
        // host's own 16 KiB is refused by the gate, not the server.
        using var long12 = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, "/" + new string('a', 12 * 1024)));
        using var allowed = await client.SendAsync(long12, Ct);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

        using var tooLong = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, "/" + new string('a', RequestLimitBounds.MaxUrlLength)));
        using var refused = await client.SendAsync(tooLong, Ct);
        Assert.Equal(HttpStatusCode.RequestUriTooLong, refused.StatusCode);

        // The same with headers: 48 KiB of them is over Kestrel's 32 KiB default and under the host's 64 KiB.
        using var heavy = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, "/"));
        for (var i = 0; i < 6; i++)
        {
            heavy.Headers.TryAddWithoutValidation($"X-Pad-{i}", new string('h', 8 * 1024));
        }

        using var heavyResponse = await client.SendAsync(heavy, Ct);
        Assert.Equal(HttpStatusCode.OK, heavyResponse.StatusCode);
        Assert.Contains(upstream.Targets, target => target.Length > 12 * 1024);
        Assert.DoesNotContain(upstream.Targets, target => target.Length > RequestLimitBounds.MaxUrlLength);
    }

    /// <summary>The status of a request the server cuts off mid-body; null when the connection was reset before the answer arrived.</summary>
    private static async Task<HttpStatusCode?> StatusOfCutOffAsync(HttpClient client, HttpRequestMessage request)
    {
        try
        {
            using var response = await client.SendAsync(request, Ct);
            return response.StatusCode;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        finally
        {
            request.Dispose();
        }
    }

    /// <summary>The uploads the upstream received: the proxy's own probe of an upstream is a GET it makes by itself.</summary>
    private static int Posts(BodyCountingUpstream upstream) => upstream.Targets.Count(target => target.StartsWith("POST ", StringComparison.Ordinal));

    private static HttpRequestMessage Upload(Uri endpoint, string path, long length, bool declared)
    {
        HttpRequestMessage request = new(HttpMethod.Post, new Uri(endpoint, path)) { Content = new StreamContent(new ZeroStream(length)) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        if (declared)
        {
            request.Content.Headers.ContentLength = length;

            // The client waits for the server's word before sending the body, as a client of a large upload does: a refusal
            // then comes back before any of it is sent, instead of racing the connection the server closes.
            request.Headers.ExpectContinue = true;
        }

        return request;
    }

    private static Paduraksa.Proxy.Snapshot.RuntimeSnapshot Compile(PaduraksaHost host, string upstreamAddress, bool longTargets = false)
    {
        var limits = longTargets
            ? new HostRequestLimitsSpec { MaxUrlLength = RequestLimitBounds.MaxUrlLength, MaxHeaderBytes = RequestLimitBounds.MaxHeaderBytes }
            : new HostRequestLimitsSpec { MaxBodySize = HostLimit };
        ProxyConfigModel model = new()
        {
            Upstreams = [new UpstreamSpec { Id = TestIds.Upstream("limits-upstream"), Destinations = [new(TestIds.Destination("limits-upstream-0"), upstreamAddress)] }],
            Hosts =
            [
                new HostSpec
                {
                    Id = TestIds.Host("limits"),
                    Names = ["127.0.0.1"],
                    RequestLimits = limits,
                    Routes =
                    [
                        new RouteSpec { Id = TestIds.Route("limits-upload"), Path = "/upload", Upstream = TestIds.Upstream("limits-upstream"), MaxBodySize = UploadLimit, MaxBodySizeRouteOverride = true },
                        new RouteSpec { Id = TestIds.Route("limits-root"), Upstream = TestIds.Upstream("limits-upstream") },
                    ],
                },
            ],
        };
        return PublicProxyTests.Compile(host, model);
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
