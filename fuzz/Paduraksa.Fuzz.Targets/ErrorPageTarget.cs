using System.Buffers;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;

using Paduraksa.Core.Ids;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Http;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Pages;
using Paduraksa.Proxy.Snapshot;

namespace Paduraksa.Fuzz;

/// <summary>
/// The operator's error pages as the proxy writes them, with every variable in the attacker's hands (PLAN §7.3, "As built (M8): custom error pages"). A request the gate
/// refuses with 404 (an unknown host, which is the global defaults' page, or a path no route of a wildcard host takes, which is the host's own) carries a <c>Host</c>, an
/// <c>X-Request-Id</c> from a trusted or an untrusted peer, a <c>CF-Ray</c> in a Cloudflare mode or not, and an <c>Accept</c>; the pages use <c>{host}</c>, <c>{requestId}</c>,
/// <c>{cfRay}</c>, <c>{status}</c> and <c>{statusText}</c>, as a message and as a document. The input is a flag byte, then the four values as lines (<c>Host</c>, <c>X-Request-Id</c>,
/// <c>CF-Ray</c>, <c>Accept</c>); the flags choose the page's mode, the trusted-proxy mode, the peer, <c>GET</c> or <c>HEAD</c>, how the host name is formed and how the lines are decoded
/// (UTF-8, Latin-1, or UTF-16, which can hold a lone surrogate).
/// Properties: no exception; the same output for the same input; the page is the host's template with each variable's text replaced by a plain HTML encoder's reading of its value
/// (the host without its port, cut at 253 characters; the ray cut at 64 and empty unless Cloudflare's headers are trusted; the request ID the client's only when its peer is trusted and it is
/// one visible-ASCII line of at most 128 characters), so no variable adds an unescaped <c>&lt; &gt; " '</c> or <c>&amp;</c> or a non-ASCII byte; <c>HEAD</c> has no body; the answer is HTML exactly
/// when a plain reading of <c>Accept</c> says so; and the writer and the <c>Accept</c> decision allocate nothing.
/// </summary>
internal static class ErrorPageTarget
{
    private const string IdToken = "QQIDQQ";
    private const string HostToken = "QQHOSTQQ";
    private const string RayToken = "QQRAYQQ";
    private const string TrustedPeer = "192.0.2.1";
    private const string OtherPeer = "198.51.100.1";

    private static readonly ErrorPageSpec MessagePage = new()
    {
        Heading = "Not here: {status} {statusText} on {host}",
        Text = "Ref {requestId}, ray {cfRay}. {{not a variable}",
    };

    private static readonly ErrorPageSpec DocumentPage = new()
    {
        Mode = ErrorPageMode.Html,
        Html = "<!doctype html><html><head><style>b{color:#900}</style></head><body><b>{status}</b> {statusText} <i>{host}</i> <u>{requestId}</u> <s>{cfRay}</s> {requestId}</body></html>",
    };

    // One snapshot for each choice of the defaults' page (a message or a document) and of whether Cloudflare's headers are trusted of the trusted peer.
    private static readonly Lazy<Setup>[] Setups = [.. Enumerable.Range(0, 4).Select(static i => new Lazy<Setup>(() => new Setup(documentDefaults: (i & 1) != 0, cloudflare: (i & 2) != 0)))];

    private static readonly ArrayBufferWriter<byte> Scratch = new(256 * 1024);

    public static FuzzTarget Target { get; } = new(
        "error-page",
        "custom error pages with the client's Host, a trusted peer's X-Request-Id, CF-Ray and Accept in every variable",
        Run,
        Seeds);

    private static void Run(ReadOnlySpan<byte> input)
    {
        if (input.IsEmpty)
        {
            return;
        }

        var flags = input[0];
        var setup = Setups[((flags & 1) != 0 ? 1 : 0) | ((flags & 2) != 0 ? 2 : 0)].Value;
        var trustedPeer = (flags & 4) != 0;
        var head = (flags & 8) != 0;
        var formed = (flags >> 4) & 3;
        var decode = (flags >> 6) & 3;
        var fields = Fields(input[1..], decode);
        var hostValue = fields[0] is { Length: > 0 } raw
            ? formed switch
            {
                1 => raw + ".msg.test",
                2 => raw + ".doc.test",
                3 => "doc.test:" + raw,
                _ => raw,
            }
            : null;
        var sent = new Request(hostValue, fields[1], fields[2], fields[3], trustedPeer ? TrustedPeer : OtherPeer, head);

        var first = setup.Serve(sent);
        var second = setup.Serve(sent);
        Checks.That(first.Normalised(first.Id) == second.Normalised(second.Id) && first.Status == second.Status && first.ContentType == second.ContentType, "the same request was answered differently the second time");
        Checks.That(first.Status == 404, $"the refusal was answered {first.Status}");
        Checks.That(first.RequestIdHeader is not null, "the answer carries no X-Request-Id");

        // The request ID: the client's only from a trusted peer, and only one visible-ASCII line of 1 to 128 characters; otherwise a generated one of 22.
        var acceptable = trustedPeer && sent.RequestId is { Length: > 0 and <= 128 } id && id.All(static c => c is >= '!' and <= '~');
        Checks.That(
            acceptable ? first.Id == sent.RequestId : first.Id != sent.RequestId && first.Id.Length == 22 && first.Id.All(static c => c is >= '!' and <= '~'),
            $"the request ID is '{first.Id}' for the client's '{sent.RequestId}' (trusted peer {trustedPeer})");

        var wantsHtml = PlainAcceptsHtml(sent.Accept);
        Checks.That(first.ContentType.StartsWith(wantsHtml ? "text/html" : "text/plain", StringComparison.Ordinal), $"Accept '{sent.Accept}' was answered with {first.ContentType}");
        Checks.AllocatesNothing(() => _ = MediaTypes.AcceptsHtml(sent.Accept ?? StringValues.Empty), "MediaTypes.AcceptsHtml");
        Checks.That(MediaTypes.AcceptsHtml(sent.Accept ?? StringValues.Empty) == wantsHtml, $"Accept '{sent.Accept}': the proxy and a plain reading disagree");

        if (head)
        {
            Checks.That(first.Body.Length == 0, $"a HEAD answer has {first.Body.Length} body bytes");
            return;
        }

        if (!wantsHtml)
        {
            var plainAnswer = setup.Matches(sent.Host) ? "not found\n" : "no host configured\n";
            Checks.That(first.Body == plainAnswer, $"the plain answer is '{first.Body}', not '{plainAnswer}'");
            return;
        }

        var page = setup.PageFor(sent.Host);
        var host = PlainHost(sent.Host ?? string.Empty);
        host = host.Length > 253 ? host[..253] : host;
        var ray = setup.CloudflareTrusted && trustedPeer ? sent.Ray ?? string.Empty : string.Empty;
        ray = ray.Length > 64 ? ray[..64] : ray;
        var template = page.Render(IdToken, HostToken, RayToken);
        var plain = Encoding.Latin1.GetString(template);
        var expected = plain
            .Replace(IdToken, HtmlEncoder.Default.Encode(first.Id), StringComparison.Ordinal)
            .Replace(HostToken, HtmlEncoder.Default.Encode(host), StringComparison.Ordinal)
            .Replace(RayToken, HtmlEncoder.Default.Encode(ray), StringComparison.Ordinal);
        Checks.That(first.Body == expected, $"the page is not the template with the encoded values: host '{host}', ray '{ray}', id '{first.Id}'");

        // No variable adds a character the HTML around it could read as markup, or a non-ASCII byte; checked on the bytes served, not on the encoder's output.
        foreach (var special in "<>\"'")
        {
            Checks.That(first.Body.Count(c => c == special) == plain.Count(c => c == special), $"a value added a raw {special}");
        }

        Checks.That(BareAmpersands(first.Body) == BareAmpersands(plain), "a value added an & that starts no entity");
        Checks.That(first.Body.Count(static c => c >= 0x80) == plain.Count(static c => c >= 0x80), "a value added a non-ASCII byte");

        // The writer itself: the bytes it writes for the values, and nothing allocated.
        Checks.AllocatesNothing(
            () =>
            {
                Scratch.ResetWrittenCount();
                page.Write(Scratch, first.Id, host, ray);
            },
            "CustomErrorPage.Write");
        Checks.That(Encoding.Latin1.GetString(Scratch.WrittenSpan) == first.Body, "the page writer and the gate's answer differ");
    }

    // The rules in one place, written plainly: a comma-separated list of media ranges, a range counts when it is text/html (any case) and none of its parameters is q=0.
    private static bool PlainAcceptsHtml(string? accept) => accept is not null
        && accept.Split(',').Any(static entry =>
        {
            var parts = entry.Split(';');
            return string.Equals(parts[0].Trim(), "text/html", StringComparison.OrdinalIgnoreCase)
                && !parts.Skip(1).Any(static parameter =>
                    parameter.Split('=', 2) is [var name, var value]
                    && string.Equals(name.Trim(), "q", StringComparison.OrdinalIgnoreCase)
                    && double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var weight)
                    && weight == 0);
        });

    // The Host header's name: an IPv6 literal up to its bracket, else without the port after the last colon and without one trailing dot.
    private static string PlainHost(string header)
    {
        if (header.StartsWith('['))
        {
            var close = header.IndexOf(']');
            return close < 0 ? header : header[..(close + 1)];
        }

        var colon = header.LastIndexOf(':');
        var name = colon < 0 ? header : header[..colon];
        return name.EndsWith('.') ? name[..^1] : name;
    }

    // An ampersand that does not begin one of the entities the encoder writes.
    private static int BareAmpersands(string text)
    {
        var bare = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '&')
            {
                continue;
            }

            var end = text.IndexOf(';', i);
            var entity = end < 0 ? string.Empty : text[(i + 1)..end];
            if (!(entity is "amp" or "lt" or "gt" or "quot" || (entity.StartsWith("#x", StringComparison.Ordinal) && entity.Length > 2 && entity[2..].All(char.IsAsciiHexDigitUpper))))
            {
                bare++;
            }
        }

        return bare;
    }

    private static string?[] Fields(ReadOnlySpan<byte> bytes, int decode)
    {
        var fields = new string?[4];
        for (var i = 0; i < fields.Length && (i == 0 || !bytes.IsEmpty || fields[i - 1] is not null); i++)
        {
            var feed = bytes.IndexOf((byte)'\n');
            var line = feed < 0 ? bytes : bytes[..feed];
            fields[i] = decode switch
            {
                1 => Encoding.Latin1.GetString(line),
                2 => Encoding.Unicode.GetString(line),
                _ => Encoding.UTF8.GetString(line),
            };
            if (feed < 0)
            {
                break;
            }

            bytes = bytes[(feed + 1)..];
        }

        return fields;
    }

    private static IEnumerable<(string Name, byte[] Bytes)> Seeds()
    {
        static byte[] Case(int flags, params string[] lines) => [(byte)flags, .. Encoding.UTF8.GetBytes(string.Join('\n', lines))];

        // flags: 1 document defaults, 2 Cloudflare, 4 trusted peer, 8 HEAD, then the host's form (x16: <v>.msg.test, x32: <v>.doc.test, x48: doc.test:<v>).
        yield return ("unknown-host", Case(4, "shop.example", "req-1234", "8a1b2c3d4e5f6a7b-AMS", "text/html"));
        yield return ("unknown-host-hostile", Case(4 | 2, "<script>alert('x')</script>", "\"><img src=x onerror=1>", "a&b<c>", "text/html,application/xhtml+xml"));
        yield return ("unknown-host-document", Case(1 | 2 | 4, "x\"y'z&w", "a-b_c", "ray", "TEXT/HTML;q=0.8"));
        yield return ("wildcard-message", Case(4 | 16, "evil<b>", "id-1", "ray-1", "text/html"));
        yield return ("wildcard-document", Case(4 | 2 | 32, "café", "id-2", "räy", "text/html"));
        yield return ("host-with-port", Case(4 | 48, "8080", "id-3", string.Empty, "text/html"));
        yield return ("untrusted-peer", Case(0 | 2, "a.test", "forged-id", "forged-ray", "text/html"));
        yield return ("request-id-too-long", Case(4, "a.test", new string('i', 129), "ray", "text/html"));
        yield return ("request-id-space", Case(4, "a.test", "two words", "ray", "text/html"));
        yield return ("head", Case(4 | 8, "a.test", "id", "ray", "text/html"));
        yield return ("plain-curl", Case(4, "a.test", "id", "ray", "*/*"));
        yield return ("quality-zero", Case(4, "a.test", "id", "ray", "text/html;q=0, */*"));
        yield return ("long-host", Case(4 | 16, new string('h', 300), "id", new string('r', 100), "text/html"));
        yield return ("ipv6-host", Case(4, "[2001:db8::1]:8080", "id", "ray", "text/html"));
        yield return ("emoji", Case(4 | 2, "\U0001F600.example", "\U0001F600", "\U0001F600", "text/html"));
        yield return ("no-values", Case(4, string.Empty, string.Empty, string.Empty, "text/html"));
    }

    private sealed record Request(string? Host, string? RequestId, string? Ray, string? Accept, string Peer, bool Head);

    private sealed record Served(int Status, string ContentType, string Body, string? RequestIdHeader)
    {
        public string Id => RequestIdHeader ?? string.Empty;

        /// <summary>The body with its request ID (a generated one differs every time) replaced, so two answers can be compared.</summary>
        public string Normalised(string id) => id.Length == 0 ? Body : Body.Replace(id, "<id>", StringComparison.Ordinal);
    }

    private sealed class Setup : IDisposable
    {
        private readonly SnapshotStore _store = new();
        private readonly GateMiddleware _gate;
        private readonly RuntimeSnapshot _snapshot;

        public Setup(bool documentDefaults, bool cloudflare)
        {
            var upstream = UpstreamId.FromValue(new Guid("01900000-0000-7000-8000-00000000b001"));
            HostSpec Host(int n, string name, ErrorPageSpec page) => new()
            {
                Id = HostId.FromValue(new Guid($"01900000-0000-7000-8000-00000000b1{n:00}")),
                Names = [name, "*." + name],
                Routes = [new RouteSpec { Id = RouteId.FromValue(new Guid($"01900000-0000-7000-8000-00000000b2{n:00}")), Path = "/only", Upstream = upstream }],
                ErrorPages = new HostErrorPagesSpec { Pages = new Dictionary<int, ErrorPageSpec> { [404] = page } },
            };

            ProxyConfigModel model = new()
            {
                Upstreams = [new UpstreamSpec { Id = upstream, Destinations = [new DestinationSpec(DestinationId.FromValue(new Guid("01900000-0000-7000-8000-00000000b003")), "http://127.0.0.1:1")] }],
                Defaults = new DefaultsSpec
                {
                    TrustedProxies = new TrustedProxySpec
                    {
                        Mode = cloudflare ? TrustedProxyMode.CloudflareTunnel : TrustedProxyMode.Generic,
                        Proxies = ["192.0.2.0/24"],
                    },
                    ErrorPages = new HostErrorPagesSpec { Pages = new Dictionary<int, ErrorPageSpec> { [404] = documentDefaults ? DocumentPage : MessagePage } },
                },
                Hosts = [Host(1, "msg.test", MessagePage), Host(2, "doc.test", DocumentPage)],
            };
            var compiled = SnapshotCompiler.Compile(model, new SnapshotCompilerOptions());
            if (!compiled.Succeeded)
            {
                throw new InvalidOperationException(string.Join("; ", compiled.Errors));
            }

            var candidate = compiled.Snapshot!;
            var endpoints = new Endpoint[candidate.RouteIdBySlot.Length];
            for (var i = 0; i < endpoints.Length; i++)
            {
                endpoints[i] = new Endpoint(static _ => Task.CompletedTask, EndpointMetadataCollection.Empty, candidate.RouteIdBySlot[i].ToString());
            }

            _store.Publish(candidate.Bind(1, endpoints));
            _snapshot = _store.Current;
            _gate = new GateMiddleware(_store, new TrustedProxyClientAddressSource());
            CloudflareTrusted = cloudflare;
        }

        public void Dispose() => _store.Dispose();

        /// <summary>Whether the trusted peer's Cloudflare headers are believed in this snapshot.</summary>
        public bool CloudflareTrusted { get; }

        public bool Matches(string? host) => host is not null && _snapshot.Hosts.Find(host, out _) is not null;

        /// <summary>The page the request's <c>Host</c> is answered with: its host's, or the global defaults' for a name no host takes.</summary>
        public CustomErrorPage PageFor(string? host)
        {
            var found = host is null ? null : _snapshot.Hosts.Find(host, out _);
            return (found is null ? _snapshot.DefaultErrorPages : found.ErrorPages).Find(404) ?? throw new InvalidOperationException("no page for 404");
        }

        public Served Serve(Request request)
        {
            var body = new MemoryStream();
            var context = new DefaultHttpContext();
            context.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(body));
            context.Features.Set<IHttpRequestLifetimeFeature>(new Lifetime());
            context.Connection.RemoteIpAddress = IPAddress.Parse(request.Peer);
            context.Request.Method = request.Head ? "HEAD" : "GET";
            context.Request.Scheme = "http";
            context.Request.Path = "/elsewhere";
            if (request.Host is { Length: > 0 })
            {
                context.Request.Headers.Host = request.Host;
            }

            if (request.RequestId is not null)
            {
                context.Request.Headers["X-Request-Id"] = request.RequestId;
            }

            if (request.Ray is not null)
            {
                context.Request.Headers["CF-Ray"] = request.Ray;
            }

            if (request.Accept is not null)
            {
                context.Request.Headers.Accept = request.Accept;
            }

            _gate.InvokeAsync(context, static _ => Task.CompletedTask).GetAwaiter().GetResult();
            context.Features.Get<IHttpResponseBodyFeature>()!.CompleteAsync().GetAwaiter().GetResult();
            return new Served(
                context.Response.StatusCode,
                context.Response.Headers.ContentType.ToString(),
                Encoding.Latin1.GetString(body.ToArray()),
                context.Response.Headers["X-Request-Id"] is { Count: 1 } header ? header[0] : null);
        }
    }

    private sealed class Lifetime : IHttpRequestLifetimeFeature
    {
        public CancellationToken RequestAborted { get; set; }

        public void Abort()
        {
        }
    }
}
