using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Hosting;

namespace Paduraksa.Tests.Integration.Admin;

/// <summary>
/// A plain-HTTP request on the admin's TLS port is answered with a <c>308</c> to the same URL over <c>https</c>, so a
/// browser given <c>http://host:9443/</c> reaches the panel instead of an empty response. TLS on that port is untouched:
/// the first byte decides, and a ClientHello is handed on unread.
/// </summary>
public sealed class AdminPlainHttpRedirectTests(SharedHost shared) : IClassFixture<SharedHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_plain_request_is_redirected_to_https_on_the_same_host_and_port()
    {
        var admin = Admin();
        var host = $"127.0.0.1:{admin.Port.ToString(CultureInfo.InvariantCulture)}";

        var (head, body) = await SendAsync(admin, $"GET / HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 308 Permanent Redirect", head, StringComparison.Ordinal);
        Assert.Equal($"https://{host}/", Header(head, "Location"));
        Assert.Equal("no-store", Header(head, "Cache-Control"));
        Assert.Equal("0", Header(head, "Content-Length"));
        Assert.Equal("close", Header(head, "Connection"));
        Assert.Equal(string.Empty, body);
    }

    [Fact]
    public async Task The_path_and_query_are_kept()
    {
        var admin = Admin();
        var host = $"127.0.0.1:{admin.Port.ToString(CultureInfo.InvariantCulture)}";

        var (head, _) = await SendAsync(admin, $"GET /api/v1/hosts?limit=5&cursor=x HTTP/1.1\r\nHost: {host}\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 308", head, StringComparison.Ordinal);
        Assert.Equal($"https://{host}/api/v1/hosts?limit=5&cursor=x", Header(head, "Location"));
    }

    [Fact]
    public async Task A_head_request_is_redirected_too()
    {
        var admin = Admin();
        var host = $"127.0.0.1:{admin.Port.ToString(CultureInfo.InvariantCulture)}";

        var (head, body) = await SendAsync(admin, $"HEAD / HTTP/1.1\r\nHost: {host}\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 308", head, StringComparison.Ordinal);
        Assert.Equal($"https://{host}/", Header(head, "Location"));
        Assert.Equal(string.Empty, body);
    }

    [Fact]
    public async Task Http_1_0_with_a_host_is_redirected_too()
    {
        var admin = Admin();
        var host = $"127.0.0.1:{admin.Port.ToString(CultureInfo.InvariantCulture)}";

        var (head, _) = await SendAsync(admin, $"GET /old HTTP/1.0\r\nHost: {host}\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 308", head, StringComparison.Ordinal);
        Assert.Equal($"https://{host}/old", Header(head, "Location"));
    }

    [Fact]
    public async Task An_ipv6_literal_host_is_kept_as_it_is()
    {
        var admin = Admin();
        var port = admin.Port.ToString(CultureInfo.InvariantCulture);

        var (head, _) = await SendAsync(admin, $"GET / HTTP/1.1\r\nHost: [::1]:{port}\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 308", head, StringComparison.Ordinal);
        Assert.Equal($"https://[::1]:{port}/", Header(head, "Location"));
    }

    [Theory]
    [InlineData("GET / HTTP/1.1\r\n\r\n")] // no Host at all
    [InlineData("GET / HTTP/1.1\r\nHost: a.example\r\nHost: b.example\r\n\r\n")] // two, so which one is a guess
    [InlineData("GET http://127.0.0.1/ HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n")] // absolute-form, not a path
    [InlineData("GET * HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n")] // asterisk-form
    [InlineData("POST / HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Length: 0\r\n\r\n")]
    [InlineData("PRI * HTTP/2.0\r\n\r\n")] // an h2c preface is not a request this port follows
    [InlineData("GET / HTTP/9.9\r\nHost: 127.0.0.1\r\n\r\n")]
    public async Task A_request_it_cannot_redirect_is_a_400(string request)
    {
        var (head, body) = await SendAsync(Admin(), request);

        Assert.StartsWith("HTTP/1.1 400 Bad Request", head, StringComparison.Ordinal);
        Assert.Null(Header(head, "Location"));
        Assert.Equal(string.Empty, body);
    }

    [Theory]
    [InlineData("evil.example/")] // would turn the authority into a path
    [InlineData("evil.example@other.example")]
    [InlineData("evil.example?x")]
    [InlineData("evil.example#x")]
    [InlineData("evil.example\\x")]
    [InlineData("[::1")] // unbalanced
    [InlineData("a]b")]
    [InlineData("[]")]
    [InlineData("[1.2.3.4]")] // brackets are for IPv6 literals only
    [InlineData("[::1]x")]
    [InlineData("1.2.3.4]:")]
    [InlineData(":9443")]
    [InlineData("admin.example:")] // a colon and no port
    [InlineData("admin.example:http")] // a named port
    public async Task A_host_that_is_malformed_or_would_move_the_browser_elsewhere_is_refused(string value)
    {
        var (head, _) = await SendAsync(Admin(), $"GET / HTTP/1.1\r\nHost: {value}\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 400 Bad Request", head, StringComparison.Ordinal);
        Assert.Null(Header(head, "Location"));
    }

    [Fact]
    public async Task A_source_outside_the_allowlist_never_sees_even_the_plaintext_answer()
    {
        // The allowlist aborts the connection before this stage reads a byte, so nothing plaintext goes out at all.
        await using var outside = await AdminHarness.StartAsync(new Dictionary<string, string?>
        {
            ["Paduraksa:Listen:AdminAllowedSources:0"] = "203.0.113.0/24",
        });
        var admin = TestHost.Endpoint(outside.Host, HostRole.Admin, "https");

        Assert.Empty(await SendRawAsync(admin, $"GET / HTTP/1.1\r\nHost: 127.0.0.1:{admin.Port}\r\n\r\n"));
    }

    [Fact]
    public async Task Tls_on_the_same_port_is_unaffected()
    {
        // A plaintext connection to the listener comes first, then an ordinary HTTPS request on a fresh connection: the peek
        // must leave both the port and every later handshake working.
        var admin = Admin();
        var host = $"127.0.0.1:{admin.Port.ToString(CultureInfo.InvariantCulture)}";
        await SendAsync(admin, $"GET / HTTP/1.1\r\nHost: {host}\r\n\r\n");

        using var client = TestHost.CreateAdminClient(shared.Host);
        using var response = await client.GetAsync(new Uri(admin, "/healthz"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok\n", await response.Content.ReadAsStringAsync(Ct));
        Assert.False(response.Headers.Contains("Location"));
    }

    private Uri Admin() => TestHost.Endpoint(shared.Host, HostRole.Admin, "https");

    /// <summary>Writes one raw request to the admin port and reads the answer up to the close, as a wrong-scheme client would.</summary>
    private static async Task<(string Head, string Body)> SendAsync(Uri admin, string request)
    {
        var response = Encoding.Latin1.GetString(await SendRawAsync(admin, request));
        var split = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        return split < 0 ? (response, string.Empty) : (response[..split], response[(split + 4)..]);
    }

    /// <summary>The bytes the admin port wrote before closing, or none when it closed without a byte (a reset included).</summary>
    private static async Task<byte[]> SendRawAsync(Uri admin, string request)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        using TcpClient tcp = new();
        await tcp.ConnectAsync(IPAddress.Loopback, admin.Port, timeout.Token);
        await using var stream = tcp.GetStream();
        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request), timeout.Token);
        }
        catch (IOException)
        {
            return []; // the listener aborted before we could speak
        }

        using MemoryStream received = new();
        try
        {
            await stream.CopyToAsync(received, timeout.Token);
        }
        catch (IOException)
        {
            // A reset after the answer (or before one): what was read is still the answer.
        }

        return received.ToArray();
    }

    private static string? Header(string head, string name)
    {
        foreach (var line in head.Split("\r\n"))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && line.AsSpan(0, colon).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return line[(colon + 1)..].Trim();
            }
        }

        return null;
    }
}
