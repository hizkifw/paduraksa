using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Tls;
using Paduraksa.Tests.Proxy.Testing;

namespace Paduraksa.Tests.Proxy.Tls;

public sealed class AltSvcHeaderTests
{
    [Theory]
    [InlineData(443, AltSvcHeader.DefaultMaxAgeSeconds, "h3=\":443\"; ma=86400")]
    [InlineData(8443, 3600, "h3=\":8443\"; ma=3600")]
    [InlineData(65535, 0, "h3=\":65535\"; ma=0")]
    public void ValueUsesThePublicPort(int port, int maxAge, string expected) =>
        Assert.Equal(expected, new AltSvcHeader(port, http3Listening: true, maxAge).Value.ToString());

    [Fact]
    public void NoValueWithoutAnHttp3Listener()
    {
        var header = new AltSvcHeader(443, http3Listening: false);

        Assert.True(StringValues.IsNullOrEmpty(header.Value));
        Assert.True(StringValues.IsNullOrEmpty(header.ForHost(TlsPolicy.Default)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void InvalidPortsAreRejected(int port) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new AltSvcHeader(port, http3Listening: true));

    [Fact]
    public void NegativeMaxAgeIsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new AltSvcHeader(443, http3Listening: true, maxAgeSeconds: -1));

    [Fact]
    public void OnlyH3EligibleHostsGetTheSharedValue()
    {
        var header = new AltSvcHeader(443, http3Listening: true);

        var eligible = header.ForHost(new TlsPolicy { MinimumVersion = TlsMinimumVersion.Tls13 });
        var optedOut = header.ForHost(new TlsPolicy { Http3Enabled = false });

        Assert.Same((string?)header.Value, (string?)eligible); // one string shared by every host
        Assert.True(StringValues.IsNullOrEmpty(optedOut));
    }

    [Theory]
    [InlineData("https", "HTTP/2", true)]
    [InlineData("https", "HTTP/1.1", true)]
    [InlineData("https", "HTTP/3", false)] // already on H3
    [InlineData("http", "HTTP/1.1", false)] // never advertise H3 for a plain-HTTP origin
    public void ApplySetsTheHeaderOnNonH3HttpsResponses(string scheme, string protocol, bool expected)
    {
        var value = new AltSvcHeader(443, http3Listening: true).Value;
        var context = Context(scheme, protocol);

        AltSvcHeader.Apply(context, value);

        Assert.Equal(expected ? value : StringValues.Empty, context.Response.Headers.AltSvc);
    }

    [Fact]
    public void ApplyWithAnEmptyValueLeavesTheResponseAlone()
    {
        var context = Context("https", "HTTP/2");

        AltSvcHeader.Apply(context, StringValues.Empty);

        Assert.False(context.Response.Headers.ContainsKey("Alt-Svc"));
    }

    [Fact]
    public void ApplyDoesNotAllocate()
    {
        var value = new AltSvcHeader(443, http3Listening: true).Value;
        var context = Context("https", "HTTP/2");

        AllocAssert.Zero((context, value), static s => AltSvcHeader.Apply(s.context, s.value), "AltSvcHeader.Apply");
    }

    [Fact]
    public async Task KestrelDeliversThePublicPortNotTheBoundPort()
    {
        using var pki = TestPki.Create();
        var table = new CertificateTableBuilder()
            .Add(["a.test"], pki.Leaf("a.test", "a.test"), TlsPolicy.Default)
            .Add(["b.test"], pki.Leaf("b.test", "b.test"), new TlsPolicy { Http3Enabled = false })
            .Build();
        var header = new AltSvcHeader(443, http3Listening: true);
        // Stand-in for the gate: the per-host value comes from the host's policy, precomputed.
        var a = header.ForHost(table.Find("a.test")!.Policy);
        var b = header.ForHost(table.Find("b.test")!.Policy);
        await using var server = await TlsTestServer.StartAsync(
            static web => web.UsePublicHttpsTransports(),
            kestrel => kestrel.ListenPublicHttps(
                new PublicHttpsListenerOptions { Address = IPAddress.Loopback, Port = 0, EnableHttp3 = false },
                new TlsHandshakeSelector(new CertificateTableSource(table))),
            context =>
            {
                AltSvcHeader.Apply(context, context.Request.Host.Host == "a.test" ? a : b);
                return context.Response.WriteAsync(context.Request.Protocol);
            });

        Assert.NotEqual(443, server.Port);
        // Exact bytes on the wire (HttpClient normalises Alt-Svc and drops the default ma=86400).
        Assert.Contains("\r\nAlt-Svc: h3=\":443\"; ma=86400\r\n", await TlsProbe.RawHttp11HeadAsync(server.Port, "a.test") + "\r\n", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Alt-Svc", await TlsProbe.RawHttp11HeadAsync(server.Port, "b.test"), StringComparison.OrdinalIgnoreCase);
        foreach (var version in new[] { HttpVersion.Version11, HttpVersion.Version20 })
        {
            Assert.Equal("h3=\":443\"", (await TlsProbe.GetAsync(pki, server.Port, "a.test", version)).AltSvc);
            Assert.Null((await TlsProbe.GetAsync(pki, server.Port, "b.test", version)).AltSvc);
        }
    }

    private static DefaultHttpContext Context(string scheme, string protocol)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = scheme;
        context.Request.Protocol = protocol;
        return context;
    }
}
