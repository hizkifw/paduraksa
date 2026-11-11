using System.Net;
using System.Security.Authentication;
using CsCheck;
using Paduraksa.Application.Compilation;
using Paduraksa.Application.Config;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Simulation;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Tls;
using Paduraksa.Tests.Unit.Config;
using static Paduraksa.Tests.Unit.Config.ConfigSamples;

namespace Paduraksa.Tests.Unit.Compilation;

/// <summary>
/// Parity over generated documents (the <see cref="CompiledBehaviourTests"/> harness is the seed): for every
/// document that validates and every request, what the request simulator says equals what the real gate answers
/// (<see cref="GateHarness"/> runs <c>GateMiddleware.InvokeAsync</c> over the same compiled snapshot).
/// </summary>
public sealed class SimulatedBehaviourTests : IDisposable
{
    private static readonly ConfigRevisionId Revision = Id<ConfigRevisionId>(7);

    private static readonly IPAddress[] Clients =
        [IPAddress.Parse("10.1.2.3"), IPAddress.Parse("192.168.1.9"), IPAddress.Parse("203.0.113.7"), IPAddress.Parse("2001:db8::5"), IPAddress.IPv6Loopback, IPAddress.Parse("198.51.100.1")];

    private static readonly string[] Paths = ["/", "/p1", "/P2/x", "/p3", "/zz", "/a//b/../p1"];

    private readonly AnyCertificate _certificates = new();

    public void Dispose() => _certificates.Dispose();

    [Fact]
    public async Task TheSimulatorSaysWhatTheGateAnswersForGeneratedDocuments()
    {
        var documents = 0;
        var compared = 0;
        var rejectedHandshakes = 0;
        for (var seed = 0; seed < 300; seed++)
        {
            var random = Random(seed);
            var document = random with { Settings = random.Settings with { TrustedProxies = new TrustedProxySettings() } };
            if (ConfigValidator.Validate(document).Count > 0)
            {
                continue;
            }

            var compilation = _certificates.Compile(document, Revision);
            Assert.True(compilation.Succeeded, string.Join("; ", compilation.Errors.Select(e => $"{e.Path}: {e.Message}")));
            var snapshot = compilation.Snapshot!;
            documents++;
            using GateHarness gate = new(snapshot);
            foreach (var host in document.Hosts.Where(h => h.Enabled))
            {
                foreach (var scheme in (string[])["http", "https"])
                {
                    foreach (var path in Paths)
                    {
                        foreach (var client in Clients)
                        {
                            GateProbe probe = new()
                            {
                                Https = scheme == "https",
                                Host = host.Names[0],
                                Path = path,
                                ClientAddress = client,
                                // The gate harness sets up no TLS facts: an HTTP/1.1 connection on TLS 1.2, which every host that
                                // completes a handshake on this connection accepts.
                                TlsVersion = SslProtocols.Tls12,
                            };
                            var simulated = GateSimulator.Run(snapshot, probe);
                            if (simulated.Outcome.Reason == GateReason.HandshakeRejected)
                            {
                                // A request behind a failed handshake never reaches the gate: the certificate table must agree.
                                Assert.True(Rejects(snapshot, probe), $"seed {seed}: {scheme}://{host.Names[0]} was rejected by the simulator only");
                                rejectedHandshakes++;
                                continue;
                            }

                            var expected = await gate.SendAsync(host.Names[0], scheme, path, client);
                            var actual = simulated.Outcome.Action switch
                            {
                                GateOutcomeAction.Forward => GateHarness.Forwarded,
                                GateOutcomeAction.Abort => GateHarness.Aborted,
                                _ => simulated.Outcome.StatusCode!.Value,
                            };
                            Assert.True(expected == actual, $"seed {seed}, {scheme}://{host.Names[0]}{path} from {client}: the gate gave {expected}, the simulator {actual}");
                            Assert.Equal(expected == GateHarness.Forwarded, simulated.Upstream is not null);
                            compared++;
                        }
                    }
                }
            }
        }

        Assert.True(documents >= 50, $"only {documents} documents validate");
        Assert.True(compared > 5000, $"only {compared} requests compared");
        Assert.True(rejectedHandshakes > 0, "no handshake was ever rejected: the generated documents don't cover that");
    }

    private static readonly string[] RandomPaths = ["/", "/api", "/api/v1/x", "/static", "/Admin", "/wp-admin/x", "/a//b/../c", "/a;b", "/a%2Fb", "/.env", "/x/.env"];

    private static readonly string?[] RandomAgents = [null, "curl/8", "sqlmap/1.7", "Mozilla/5.0", "nikto", string.Empty];

    private static readonly IPAddress[] RandomClients =
        [.. Clients, IPAddress.Parse("172.16.0.9"), IPAddress.Parse("198.51.100.1"), IPAddress.Parse("2001:db8:1::1")];

    /// <summary>
    /// The same parity over documents and requests that are both generated: any document that validates, with what the gate answers
    /// through state (a rate limit's tokens, a proof of work's key, a provider's endpoint) left out, and any request.
    /// </summary>
    [Fact]
    public void TheSimulatorSaysWhatTheGateAnswersForAnyGeneratedDocumentAndRequest()
    {
        var documents = 0;
        var compared = 0;
        Gen.Select(ConfigGen.Documents, Gen.Int[0, RandomPaths.Length - 1].List[1, 6], Gen.Int[0, 1_000_000]).Check(
            400,
            t =>
            {
                var document = Stateless(t.Item1);
                if (ConfigValidator.Validate(document).Count > 0)
                {
                    return;
                }

                var compilation = _certificates.Compile(document, Revision);
                Assert.True(compilation.Succeeded, string.Join("; ", compilation.Errors.Select(e => $"{e.Path}: {e.Message}")));
                var snapshot = compilation.Snapshot!;
                documents++;
                using GateHarness gate = new(snapshot);
                var salt = t.Item3;
                foreach (var (pathAt, i) in t.Item2.Select((p, i) => (p, i)))
                {
                    var host = document.Hosts.Count == 0 ? "nohost.example.com" : Concrete(document.Hosts[(salt + i) % document.Hosts.Count].Names[(salt + i) % document.Hosts[(salt + i) % document.Hosts.Count].Names.Count]);
                    var https = (salt + i) % 2 == 0;
                    var client = RandomClients[(salt / 3 + i) % RandomClients.Length];
                    var method = (salt + i) % 5 == 0 ? "POST" : "GET";
                    var agent = RandomAgents[(salt / 7 + i) % RandomAgents.Length];
                    var path = RandomPaths[pathAt];
                    GateProbe probe = new()
                    {
                        Https = https,
                        Host = host,
                        Path = path,
                        Method = method,
                        Headers = agent is null ? [] : [new ProbeHeader("User-Agent", agent)],
                        ClientAddress = client,
                        TlsVersion = SslProtocols.Tls12,
                    };
                    var simulated = GateSimulator.Run(snapshot, probe);
                    if (simulated.Outcome.Reason == GateReason.HandshakeRejected)
                    {
                        Assert.True(Rejects(snapshot, probe), $"{host}: rejected by the simulator only");
                        continue;
                    }

                    var expected = gate.SendAsync(host, https ? "https" : "http", path, client, method, agent).GetAwaiter().GetResult();
                    var actual = simulated.Outcome.Action switch
                    {
                        GateOutcomeAction.Forward => GateHarness.Forwarded,
                        GateOutcomeAction.Abort => GateHarness.Aborted,
                        _ => simulated.Outcome.StatusCode!.Value,
                    };
                    Assert.True(expected == actual, $"{(https ? "https" : "http")}://{host}{path} {method} {agent} from {client}: the gate gave {expected}, the simulator {actual} ({simulated.Outcome.Reason})");
                    compared++;
                }
            });

        Assert.True(documents >= 30, $"only {documents} documents validate");
        Assert.True(compared >= 200, $"only {compared} requests compared");
    }

    // A wildcard name stands for one label in front.
    private static string Concrete(string name) => name.StartsWith("*.", StringComparison.Ordinal) ? "sub" + name[1..] : name;

    // What a gate answers from state of its own (a rate limit's tokens, a proof of work's key, a provider's endpoint) isn't a function of the document and the request.
    private static ConfigDocument Stateless(ConfigDocument document)
    {
        static HostSettings Plain(HostSettings settings) => settings with { ProofOfWork = null, RateLimit = null, AuthProvider = null };
        static RouteSettings PlainRoute(RouteSettings settings) => settings with { ProofOfWork = null, RateLimit = null, AuthProvider = null };
        return document with
        {
            Settings = document.Settings with { Defaults = Plain(document.Settings.Defaults), TrustedProxies = new TrustedProxySettings() },
            Profiles = [.. document.Profiles.Select(p => p with { Settings = Plain(p.Settings) })],
            Hosts = [.. document.Hosts.Select(h => h with { Settings = Plain(h.Settings), Routes = [.. h.Routes.Select(r => r with { Settings = PlainRoute(r.Settings) })] })],
        };
    }

    /// <summary>The certificate table says the same: no entry takes the name, or the entry only speaks TLS 1.3.</summary>
    private static bool Rejects(RuntimeSnapshot snapshot, GateProbe probe)
    {
        var name = HostTable.HostName(probe.Host).ToString();
        var selection = TlsHandshakeSelector.Select(snapshot.Certificates, name.StartsWith('[') ? string.Empty : name, quic: false);
        return selection.Options is null || (selection.Options.EnabledSslProtocols & SslProtocols.Tls12) == 0;
    }
}
