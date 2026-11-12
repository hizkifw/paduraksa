using System.Net;
using System.Security.Authentication;
using System.Text.RegularExpressions;
using CsCheck;
using Paduraksa.Application.Compilation;
using Paduraksa.Application.Config;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Pages;
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
public sealed partial class SimulatedBehaviourTests : IDisposable
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

    /// <summary>
    /// The page the simulator says a browser is served (<c>errorPage</c>: the operator's, its layer and mode, or the built-in one) is the page the real gate writes, for the
    /// refusals and for maintenance. The operator's layer is found again here by a plain walk over the document (host, then its profile, then the global defaults; an entry is
    /// replaced whole), and the page it names is rendered by the preview compiler with the request's own values for the samples: the gate's bytes must be those, exactly when the simulator says
    /// custom, and never those when it says built-in.
    /// </summary>
    [Fact]
    public void TheSimulatedErrorPageIsTheOneTheGateServesForRefusalsAndMaintenance()
    {
        var documents = 0;
        var compared = 0;
        var custom = 0;
        var customDocuments = 0;
        var maintenance = 0;
        var maintenanceCustom = 0;
        var fromEachLayer = new HashSet<ErrorPageSource>();
        Gen.Select(ConfigGen.Documents, Gen.Int[0, 1_000_000]).Check(
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
                var salt = t.Item2;
                var enabled = document.Hosts.Where(static h => h.Enabled).ToList();
                List<(HostConfig? Owner, string Host)> targets = [(null, "nohost.example.com")];
                targets.AddRange(enabled.Select(h => ((HostConfig?)h, Concrete(h.Names[salt % h.Names.Count]))));
                foreach (var (owner, host) in targets)
                {
                    foreach (var path in (string[])["/", "/zz", "/p1", "/api"])
                    {
                        foreach (var client in (IPAddress[])[RandomClients[salt % RandomClients.Length], RandomClients[(salt / 5) % RandomClients.Length]])
                        {
                            var https = (salt + path.Length) % 2 == 0;
                            GateProbe probe = new() { Https = https, Host = host, Path = path, ClientAddress = client, TlsVersion = SslProtocols.Tls12 };
                            var simulated = GateSimulator.Run(snapshot, probe);
                            if (simulated.Outcome is not { Action: GateOutcomeAction.Respond, ErrorPage: { } page } outcome || simulated.Outcome.Reason == GateReason.HandshakeRejected)
                            {
                                continue;
                            }

                            var served = gate.ServePageAsync(host, https ? "https" : "http", path, client).GetAwaiter().GetResult();
                            Assert.Equal(outcome.StatusCode, served.Status);
                            var isMaintenance = outcome.Reason == GateReason.Maintenance;
                            var key = isMaintenance ? ErrorPageStatuses.MaintenanceKey : served.Status;
                            var (entry, layer) = Effective(document, owner, key);
                            var servedIsEntry = entry is not null && served.Body == RenderFor(entry, served.Status, host, GeneratedRequestId().Match(served.Body).Value);
                            var context = $"{host}{path} from {client}: status {served.Status}, key {key}, simulated {page}";
                            Assert.True(servedIsEntry == page.Custom, $"{context}: the gate serves the operator's page: {servedIsEntry}");
                            if (page.Custom)
                            {
                                Assert.True(page.Source == layer && page.Mode == entry!.Mode, $"{context}: the plain walk finds the {layer} layer's {entry!.Mode} page");
                                Assert.Equal(entry.Mode == ErrorPageMode.Html, served.ContentSecurityPolicy.StartsWith("default-src 'none'; style-src 'unsafe-inline'", StringComparison.Ordinal));
                                custom++;
                                customDocuments += entry.Mode == ErrorPageMode.Html ? 1 : 0;
                                fromEachLayer.Add(layer);
                            }

                            maintenance += isMaintenance ? 1 : 0;
                            maintenanceCustom += isMaintenance && page.Custom ? 1 : 0;
                            compared++;
                        }
                    }
                }
            });

        Assert.True(documents >= 30, $"only {documents} documents validate");
        Assert.True(compared >= 500 && custom >= 50 && customDocuments >= 10, $"{compared} pages compared, {custom} custom, {customDocuments} of them documents");
        Assert.True(maintenance >= 50 && maintenanceCustom >= 10, $"{maintenance} maintenance answers, {maintenanceCustom} with a page of the operator's");
        Assert.True(fromEachLayer.Count == 3, $"the custom pages came from {string.Join(", ", fromEachLayer)}, not all of host, profile and global defaults");
    }

    // The operator's entry for a key as a plain walk over the document finds it: the host's own layer, then its profile's, then the global defaults'; the first that sets it.
    private static (ErrorPageContent? Entry, ErrorPageSource Layer) Effective(ConfigDocument document, HostConfig? host, int key)
    {
        if (host is not null && host.Settings.ErrorPages?.Entry(key) is { } own)
        {
            return (own, ErrorPageSource.Host);
        }

        if (host?.ProfileId is { } profileId && document.Profiles.FirstOrDefault(p => p.Id == profileId)?.Settings.ErrorPages?.Entry(key) is { } shared)
        {
            return (shared, ErrorPageSource.Profile);
        }

        return document.Settings.Defaults.ErrorPages?.Entry(key) is { } global ? (global, ErrorPageSource.GlobalDefaults) : (null, ErrorPageSource.BuiltIn);
    }

    // The preview compiler's page for this entry, with the request's own host and ID where it put its samples (the ray is empty: no peer here is a Cloudflare edge).
    private static string RenderFor(ErrorPageContent entry, int status, string host, string requestId) =>
        System.Text.Encoding.UTF8.GetString(ErrorPagePreviews.Render(entry.Mode, entry.Heading, entry.Text, entry.Html, status).Body)
            .Replace(ErrorPagePreviews.SampleRequestId, requestId, StringComparison.Ordinal)
            .Replace(ErrorPagePreviews.SampleHost, host, StringComparison.Ordinal)
            .Replace(ErrorPagePreviews.SampleRay, string.Empty, StringComparison.Ordinal);

    // The ID the gate generated for the request (the response header may be off for the host): 8 and 13 characters of the proxy's alphabet around a hyphen.
    [GeneratedRegex("[0-9a-hjkmnpqrstv-z]{8}-[0-9a-hjkmnpqrstv-z]{13}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex GeneratedRequestId();

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
