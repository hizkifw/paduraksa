using System.Diagnostics.CodeAnalysis;
using System.Security.Authentication;

using Microsoft.AspNetCore.Http;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Tls;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Proxy.Tls;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.Gate;

/// <summary>
/// Per-host TLS settings hold per request, not only per handshake: a coalesced connection that is weaker gets 421, and
/// so does any HTTPS request for a host without HTTPS.
/// </summary>
public sealed class ConnectionTlsGateTests : IDisposable
{
    private static readonly TlsPolicy Strict = new() { MinimumVersion = TlsMinimumVersion.Tls13 };
    private static readonly TlsPolicy NoH3 = new() { Http3Enabled = false };
    private static readonly TlsPolicy H1Only = new() { TcpAlpn = TcpAlpn.Http11Only };

    private readonly TestPki _pki = TestPki.Create();
    private readonly SnapshotStore _store = new();
    private readonly GateMiddleware _gate;

    public ConnectionTlsGateTests()
    {
        ServerCertificate certificate = _pki.Leaf("shared", "*.test");
        _store.Publish(Bound(
            new ProxyConfigModel
            {
                Upstreams = [Upstream("web", "http://127.0.0.1:1/")],
                Hosts =
                [
                    ProxyHost("lax", "lax.test", Route("lax-root", "web")),
                    ProxyHost("tcp", "tcp.test", Route("tcp-root", "web")) with { Tls = new HostTlsSpec { Http3 = false } },
                    ProxyHost("plain", "plain.test", Route("plain-root", "web")) with { Https = false },
                ],
                Defaults = new DefaultsSpec { Certificate = TestIds.Certificate("shared") },
            },
            options: new SnapshotCompilerOptions { Certificates = new Single(certificate) }));
        _gate = new GateMiddleware(_store, new TrustedProxyClientAddressSource());
    }

    public void Dispose()
    {
        _store.Dispose();
        _pki.Dispose();
    }

    [Fact]
    public void TheComparison()
    {
        // The default policy accepts any connection.
        Assert.True(ConnectionTlsCheck.Satisfies(TlsPolicy.Default, "HTTP/3", SslProtocols.None));
        Assert.True(ConnectionTlsCheck.Satisfies(TlsPolicy.Default, "HTTP/2", SslProtocols.Tls12));

        // TLS 1.3 minimum: TCP must have negotiated 1.3 (unknown counts as older); QUIC is always 1.3.
        Assert.True(ConnectionTlsCheck.Satisfies(Strict, "HTTP/2", SslProtocols.Tls13));
        Assert.False(ConnectionTlsCheck.Satisfies(Strict, "HTTP/2", SslProtocols.Tls12));
        Assert.False(ConnectionTlsCheck.Satisfies(Strict, "HTTP/1.1", SslProtocols.None));
        Assert.True(ConnectionTlsCheck.Satisfies(Strict, "HTTP/3", SslProtocols.None));
        Assert.False(ConnectionTlsCheck.Satisfies(NoH3, "HTTP/3", SslProtocols.None));
        Assert.True(ConnectionTlsCheck.Satisfies(NoH3, "HTTP/2", SslProtocols.Tls12));
        Assert.False(ConnectionTlsCheck.Satisfies(H1Only, "HTTP/2", SslProtocols.Tls13));
        Assert.True(ConnectionTlsCheck.Satisfies(H1Only, "HTTP/1.1", SslProtocols.Tls12));
    }

    [Theory]
    [InlineData("tcp.test", "https", "HTTP/3", 421)] // coalesced onto another name's QUIC connection
    [InlineData("tcp.test", "https", "HTTP/2", -1)]
    [InlineData("lax.test", "https", "HTTP/3", -1)]
    [InlineData("tcp.test", "http", "HTTP/1.1", -1)] // plain HTTP: no TLS to check
    [InlineData("plain.test", "https", "HTTP/2", 421)] // no HTTPS for the host: the default (wildcard) certificate mustn't reach it
    [InlineData("plain.test", "https", "HTTP/3", 421)]
    [InlineData("plain.test", "http", "HTTP/1.1", -1)]
    public void TheGateAnswers421ForAWeakerConnection(string host, string scheme, string protocol, int status)
    {
        DefaultHttpContext context = TestContexts.Create(host, "/", scheme: scheme);
        context.Request.Protocol = protocol;

        GateDecision decision = _gate.Decide(context);

        if (status < 0)
        {
            Assert.Equal(GateAction.Forward, decision.Action);
        }
        else
        {
            Assert.Equal(GateAction.Respond, decision.Action);
            Assert.Equal(status, decision.Response!.StatusCode);
        }
    }

    [Fact]
    public void CheckingAllocatesNothing()
    {
        DefaultHttpContext context = TestContexts.Create("tcp.test", "/", scheme: "https");
        context.Request.Protocol = "HTTP/3";
        AllocAssert.Zero((_gate, context), static s => _ = s._gate.Decide(s.context), "gate answering 421");
    }

    private sealed class Single(ServerCertificate certificate) : IServerCertificateResolver
    {
        public bool TryResolve(CertificateId id, [NotNullWhen(true)] out ServerCertificate? resolved)
        {
            resolved = certificate;
            return true;
        }
    }
}
