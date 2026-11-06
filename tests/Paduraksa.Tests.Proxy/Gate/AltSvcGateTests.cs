using System.Diagnostics.CodeAnalysis;

using Microsoft.AspNetCore.Http;

using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Tls;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Proxy.Tls;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.Gate;

/// <summary>Per-host <c>Alt-Svc</c> (PLAN §7.5): compiled from the public port, applied by the gate to TCP HTTPS responses.</summary>
public sealed class AltSvcGateTests : IDisposable
{
    private const string Advertised = "h3=\":8443\"; ma=86400";

    private readonly TestPki _pki = TestPki.Create();
    private readonly SnapshotStore _store = new();
    private readonly SnapshotCompilerOptions _options;
    private readonly GateMiddleware _gate;

    public AltSvcGateTests()
    {
        _options = new SnapshotCompilerOptions { Certificates = new OneCertificate(_pki.Leaf("any", "app.test")), PublicHttpsPort = 8443, AdvertiseHttp3 = true };
        _store.Publish(Bound(Model(), options: _options));
        _gate = new GateMiddleware(_store, new TrustedProxyClientAddressSource());
    }

    public void Dispose()
    {
        _store.Dispose();
        _pki.Dispose();
    }

    private static ProxyConfigModel Model() => new()
    {
        Upstreams = [Upstream("web", "http://127.0.0.1:1/")],
        Hosts =
        [
            ProxyHost("app", "app.test", Route("app-api", "web", "/api")) with { Certificate = "cert" },
            ProxyHost("tcp", "tcp.test", Route("tcp-root", "web")) with { Certificate = "cert", Tls = new HostTlsSpec { Http3 = false } },
            ProxyHost("plain", "plain.test", Route("plain-root", "web")),
            ProxyHost("literal", "192.0.2.10", Route("literal-root", "web")) with { Certificate = "cert", Names = ["192.0.2.10", "[2001:db8::10]"] },
            new HostSpec { Key = "soon", Names = ["soon.test"], Kind = HostKind.Respond, Certificate = "cert", Respond = new RespondSpec { StatusCode = 503 } },
        ],
    };

    [Fact]
    public void TheCompilerPrecomputesOneValuePerHost()
    {
        RuntimeSnapshot snapshot = _store.Current;

        Assert.Equal(Advertised, snapshot.Hosts.Find("app.test")!.AltSvc);
        Assert.Same(snapshot.Hosts.Find("app.test")!.AltSvc.ToString(), snapshot.Hosts.Find("soon.test")!.AltSvc.ToString());
        Assert.True(snapshot.Hosts.Find("tcp.test")!.AltSvc.Count == 0); // opted out of H3
        Assert.True(snapshot.Hosts.Find("plain.test")!.AltSvc.Count == 0); // no certificate: no HTTPS at all
        Assert.True(snapshot.Hosts.Find("192.0.2.10")!.AltSvc.Count == 0); // IP literals only: no certificate-table names

        // Without an H3 listener nobody advertises it.
        RuntimeSnapshot noH3 = Compile(Model(), _options with { AdvertiseHttp3 = false });
        Assert.True(noH3.Hosts.Find("app.test")!.AltSvc.Count == 0);
    }

    [Theory]
    [InlineData("app.test", "/api/x", "https", "HTTP/2", Advertised)] // forwarded
    [InlineData("app.test", "/nothing", "https", "HTTP/1.1", Advertised)] // answered by the gate (no route)
    [InlineData("soon.test", "/", "https", "HTTP/2", Advertised)] // a Respond host
    [InlineData("app.test", "/api/x", "http", "HTTP/1.1", null)] // plain HTTP: never
    [InlineData("app.test", "/api/x", "https", "HTTP/3", null)] // already on H3
    [InlineData("tcp.test", "/", "https", "HTTP/2", null)]
    [InlineData("nobody.test", "/", "https", "HTTP/2", null)] // unknown hosts get the default answer only
    public void TheGateSetsTheHostsValueOnTcpHttpsResponses(string host, string path, string scheme, string protocol, string? expected)
    {
        DefaultHttpContext context = TestContexts.Create(host, path, scheme: scheme);
        context.Request.Protocol = protocol;

        _ = _gate.Decide(context);

        Assert.Equal(expected, context.Response.Headers.AltSvc.Count == 0 ? null : context.Response.Headers.AltSvc.ToString());
    }

    [Fact]
    public void ApplyingItAllocatesNothing()
    {
        DefaultHttpContext context = TestContexts.Create("app.test", "/api/x", scheme: "https");
        AllocAssert.Zero((_gate, context), static s => _ = s._gate.Decide(s.context), "gate forward with Alt-Svc");
    }

    private sealed class OneCertificate(ServerCertificate certificate) : IServerCertificateResolver
    {
        public bool TryResolve(string reference, [NotNullWhen(true)] out ServerCertificate? resolved)
        {
            resolved = certificate;
            return true;
        }
    }
}
