using System.Net;
using System.Security.Authentication;

using Paduraksa.Application.Compilation;
using Paduraksa.Application.Config;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Simulation;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Tls;

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

    /// <summary>The certificate table says the same: no entry takes the name, or the entry only speaks TLS 1.3.</summary>
    private static bool Rejects(RuntimeSnapshot snapshot, GateProbe probe)
    {
        var name = HostTable.HostName(probe.Host).ToString();
        var selection = TlsHandshakeSelector.Select(snapshot.Certificates, name.StartsWith('[') ? string.Empty : name, quic: false);
        return selection.Options is null || (selection.Options.EnabledSslProtocols & SslProtocols.Tls12) == 0;
    }
}
