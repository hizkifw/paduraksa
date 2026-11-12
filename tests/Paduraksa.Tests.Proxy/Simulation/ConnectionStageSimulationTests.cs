using System.Net;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Simulation;
using Paduraksa.Proxy.Tls;
using Paduraksa.Tests.Proxy.Tls;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.Simulation;

/// <summary>The simulator says what the connection stage would do: a deny-listed address is refused before TLS, on both transports and
/// both listeners, by the same decision the runtime makes; a client behind a proxy isn't, and the trace says why.</summary>
public sealed class ConnectionStageSimulationTests : IDisposable
{
    private static readonly AccessListSpec Scanners = new()
    {
        Id = TestIds.AccessList("scanners"),
        Name = "scanners",
        Realm = "scanners",
        Deny = ["203.0.113.0/24"],
    };

    private static readonly IPAddress Listed = IPAddress.Parse("203.0.113.9");
    private static readonly IPAddress Visitor = IPAddress.Parse("198.51.100.4");
    private static readonly ListenerFacts Listeners = new(null, null, Http3: true);

    private readonly TestPki _pki = TestPki.Create();

    public void Dispose() => _pki.Dispose();

    private Paduraksa.Proxy.Snapshot.RuntimeSnapshot Snapshot(ConnectionsSpec? connections = null, TrustedProxySpec? proxies = null) =>
        Bound(
            new ProxyConfigModel
            {
                Upstreams = [Upstream("web", "http://127.0.0.1:1/")],
                AccessLists = [Scanners],
                Defaults = new DefaultsSpec
                {
                    Connections = connections ?? new ConnectionsSpec { DenyList = Scanners.Id },
                    TrustedProxies = proxies ?? TrustedProxySpec.Off,
                    Certificate = TestIds.Certificate("shared"),
                },
                Hosts = [ProxyHost("app", "app.test", Route("root", "web"))],
            },
            options: new SnapshotCompilerOptions { Certificates = new Single(_pki.Leaf("shared", "*.test")) });

    private static GateSimulation Run(Paduraksa.Proxy.Snapshot.RuntimeSnapshot snapshot, IPAddress client, bool https, ProbeHttpVersion version = ProbeHttpVersion.Http11, IPAddress? peer = null, ProbeArrival arrival = ProbeArrival.Direct) =>
        GateSimulator.Run(
            snapshot,
            new GateProbe { Https = https, Host = "app.test", ClientAddress = client, PeerAddress = peer, HttpVersion = version, Arrival = arrival },
            listeners: Listeners);

    [Theory]
    [InlineData(false, ProbeHttpVersion.Http11)]
    [InlineData(true, ProbeHttpVersion.Http11)]
    [InlineData(true, ProbeHttpVersion.Http2)]
    [InlineData(true, ProbeHttpVersion.Http3)]
    public void ADenyListedAddressIsRefusedBeforeTlsOnEveryListener(bool https, ProbeHttpVersion version)
    {
        var simulation = Run(Snapshot(), Listed, https, version);

        Assert.Equal(GateOutcomeAction.Abort, simulation.Outcome.Action);
        Assert.Equal(GateReason.ConnectionRejected, simulation.Outcome.Reason);
        var step = simulation.Steps.Single(s => s.Kind == GateStepKind.Connection && s.Verdict == GateStepVerdict.Stopped);
        Assert.Contains("connection deny list 'scanners'", step.Summary, StringComparison.Ordinal);
        Assert.Contains("before TLS", step.Summary, StringComparison.Ordinal);
        Assert.Equal("scanners", step.Facts.Single(f => f.Name == "connectionDenyList").Value);
        Assert.DoesNotContain(simulation.Steps, s => s.Kind is GateStepKind.HostMatch or GateStepKind.HostPolicy);
    }

    [Fact]
    public void ARefusedQuicHandshakeStillShowsTheHandshakeBeforeIt()
    {
        var simulation = Run(Snapshot(), Listed, https: true, ProbeHttpVersion.Http3);

        var kinds = simulation.Steps.Select(s => s.Kind).ToList();
        Assert.True(kinds.IndexOf(GateStepKind.TlsHandshake) < kinds.LastIndexOf(GateStepKind.Connection));
    }

    [Fact]
    public void AnAddressOffTheListGoesThroughAndTheStepStatesTheListAndTheLimit()
    {
        var simulation = Run(Snapshot(), Visitor, https: false);

        Assert.Equal(GateOutcomeAction.Forward, simulation.Outcome.Action);
        var facts = simulation.Steps.First(s => s.Kind == GateStepKind.Connection).Facts;
        Assert.Equal("scanners", facts.Single(f => f.Name == "connectionDenyList").Value);
        Assert.Contains("256 per address", facts.Single(f => f.Name == "connectionLimit").Value, StringComparison.Ordinal);
        Assert.DoesNotContain(facts, f => f.Name == "clientInConnectionDenyList");
    }

    [Fact]
    public void TheLimitOffAndNoListAreSaidToo()
    {
        var facts = Run(Snapshot(new ConnectionsSpec { MaxPerIp = 0 }), Visitor, https: false).Steps.First(s => s.Kind == GateStepKind.Connection).Facts;

        Assert.Equal("none", facts.Single(f => f.Name == "connectionDenyList").Value);
        Assert.Equal("off", facts.Single(f => f.Name == "connectionLimit").Value);
    }

    [Fact]
    public void AClientBehindATrustedProxyIsNotRefusedAtConnectionTimeAndTheTraceSaysItIsJudgedAtTheGate()
    {
        var edge = IPAddress.Parse("173.245.48.1");
        var snapshot = Snapshot(proxies: new TrustedProxySpec { Mode = TrustedProxyMode.CloudflareProxy });

        var simulation = Run(snapshot, Listed, https: true, version: ProbeHttpVersion.Http2, peer: edge, arrival: ProbeArrival.CloudflareProxy);

        var step = simulation.Steps.First(s => s.Kind == GateStepKind.Connection);
        Assert.Equal(GateStepVerdict.Passed, step.Verdict);
        Assert.Contains("trusted proxy: not counted", step.Facts.Single(f => f.Name == "connectionLimit").Value, StringComparison.Ordinal);
        Assert.Contains("judged at the gate", step.Facts.Single(f => f.Name == "clientInConnectionDenyList").Value, StringComparison.Ordinal);
        Assert.NotEqual(GateReason.ConnectionRejected, simulation.Outcome.Reason);
    }

    [Theory]
    [InlineData("203.0.113.9", true)]
    [InlineData("203.0.113.255", true)]
    [InlineData("203.0.114.1", false)]
    [InlineData("198.51.100.4", false)]
    public void TheSimulatorAndTheRuntimeAgreeOnWhoIsDenied(string address, bool denied)
    {
        var snapshot = Snapshot();
        var runtime = new ConnectionRuntime(new ConnectionTable(64));

        var live = runtime.Admit(snapshot, IpKey.Parse(address), ConnectionTransport.Tcp, out _) == ConnectionDecision.Denied;
        var simulated = Run(snapshot, IPAddress.Parse(address), https: true).Outcome.Reason == GateReason.ConnectionRejected;

        Assert.Equal(denied, live);
        Assert.Equal(denied, simulated);
    }

    private sealed class Single(ServerCertificate certificate) : IServerCertificateResolver
    {
        public bool TryResolve(CertificateId id, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ServerCertificate? resolved)
        {
            resolved = certificate;
            return true;
        }
    }
}
